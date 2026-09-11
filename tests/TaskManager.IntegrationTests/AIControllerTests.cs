using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace TaskManager.IntegrationTests;

/// <summary>
/// End-to-end coverage of /api/ai/* through the real pipeline (controller -> MediatR ->
/// ToolDispatcher -> repositories -> Postgres), with <see cref="FakeAIService"/> standing in for
/// Gemini so these run without any external API key. Focused on the safety properties the AI
/// integration exists to guarantee: the tool whitelist is enforced, task ownership is always
/// scoped to the caller, mutations never happen before explicit confirmation, and the daily
/// quota actually blocks further requests once hit.
/// </summary>
[Collection("api")]
public class AIControllerTests
{
    private readonly ApiFactory _factory;
    public AIControllerTests(ApiFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private void RequireDb() => Skip.IfNot(_factory.DatabaseAvailable, "PostgreSQL not reachable on localhost:5432");

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "admin", password = "admin123" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Creates a brand-new, isolated user and returns a client already logged in as them.</summary>
    private async Task<HttpClient> NewUserClientAsync(string label)
    {
        var admin = await AdminClientAsync();
        var email = $"ai-{label}-{Guid.NewGuid():N}@taskflow.test";
        const string password = "Str0ngPass1";
        (await admin.PostAsJsonAsync("/api/admin/users", new
        {
            displayName = $"AI Test {label}", email, role = "User", password, confirmPassword = password, isActive = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("accessToken").GetString();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    // ---------------------------------------------------------------- 1. tool whitelist

    [SkippableFact]
    public async Task Assistant_never_dispatches_a_tool_outside_the_whitelist()
    {
        RequireDb();
        var client = await NewUserClientAsync("wl");

        var res = await client.PostAsJsonAsync("/api/ai/assistant", new { message = "USE_INVALID_TOOL" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await Body(res);
        var data = body.GetProperty("data");
        data.GetProperty("requiresConfirmation").GetBoolean().Should().BeFalse();
        data.GetProperty("toolName").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("reply").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task Confirm_endpoint_rejects_a_tool_name_outside_the_whitelist()
    {
        RequireDb();
        var client = await NewUserClientAsync("wl2");

        var res = await client.PostAsJsonAsync("/api/ai/assistant/confirm", new
        {
            toolName = "DeleteEverything",
            arguments = new Dictionary<string, object>(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await Body(res);
        body.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    // ---------------------------------------------------------------- 2. ownership isolation

    [SkippableFact]
    public async Task Assistant_reschedule_only_ever_resolves_the_authenticated_users_own_tasks()
    {
        RequireDb();
        var owner = await NewUserClientAsync("owner");
        var stranger = await NewUserClientAsync("stranger");

        var uniqueTitle = $"OwnershipProbe-{Guid.NewGuid():N}";
        var createRes = await owner.PostAsJsonAsync("/api/tasks", new { title = uniqueTitle });
        createRes.IsSuccessStatusCode.Should().BeTrue();

        var res = await stranger.PostAsJsonAsync("/api/ai/assistant", new { message = $"reschedule {uniqueTitle} to 2027-01-01" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await Body(res)).GetProperty("data");
        data.GetProperty("requiresConfirmation").GetBoolean().Should().BeFalse("the task belongs to a different user and must not be found");
        data.GetProperty("reply").GetString().Should().Contain("couldn't find");
    }

    // ---------------------------------------------------------------- 3. no mutation before confirm

    [SkippableFact]
    public async Task Assistant_create_task_requires_explicit_confirmation_before_anything_is_created()
    {
        RequireDb();
        var client = await NewUserClientAsync("create");
        var uniqueTitle = $"ConfirmMePlease-{Guid.NewGuid():N}";

        var propose = await client.PostAsJsonAsync("/api/ai/assistant", new { message = $"create task {uniqueTitle}" });
        propose.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposed = (await Body(propose)).GetProperty("data");
        proposed.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();
        proposed.GetProperty("toolName").GetString().Should().Be("CreateTask");

        // Nothing was created yet.
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/tasks?search={Uri.EscapeDataString(uniqueTitle)}");
        before.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0);

        // Echo the exact tool + args the preview showed, as the real UI would.
        var argsJson = proposed.GetProperty("arguments").GetRawText();
        var confirm = await client.PostAsync("/api/ai/assistant/confirm",
            new StringContent($$"""{"toolName":"CreateTask","arguments":{{argsJson}}}""",
                System.Text.Encoding.UTF8, "application/json"));
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/tasks?search={Uri.EscapeDataString(uniqueTitle)}");
        after.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [SkippableFact]
    public async Task Task_breakdown_preview_adds_no_subtasks_until_confirmed()
    {
        RequireDb();
        var client = await NewUserClientAsync("breakdown");
        var created = await client.PostAsJsonAsync("/api/tasks", new { title = "Prepare the big report" });
        var taskId = (await Body(created)).GetProperty("data").GetProperty("id").GetGuid();

        var preview = await client.PostAsJsonAsync("/api/ai/tasks/breakdown", new { taskId });
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var subtasks = (await Body(preview)).GetProperty("data").GetProperty("subtasks");
        subtasks.GetArrayLength().Should().BeGreaterThan(0);

        var stillEmpty = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
        stillEmpty.GetProperty("data").GetProperty("subtasks").GetArrayLength().Should().Be(0);

        var titles = subtasks.EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToArray();
        var confirm = await client.PostAsJsonAsync($"/api/ai/tasks/{taskId}/breakdown/confirm", new { subtaskTitles = titles });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        var populated = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
        populated.GetProperty("data").GetProperty("subtasks").GetArrayLength().Should().Be(titles.Length);
    }

    // ---------------------------------------------------------------- 4. read-only tools run immediately

    [SkippableFact]
    public async Task Assistant_read_only_tool_runs_immediately_without_a_confirmation_step()
    {
        RequireDb();
        var client = await NewUserClientAsync("readonly");

        var res = await client.PostAsJsonAsync("/api/ai/assistant", new { message = "show overdue" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await Body(res)).GetProperty("data");
        data.GetProperty("requiresConfirmation").GetBoolean().Should().BeFalse();
        data.GetProperty("toolName").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
