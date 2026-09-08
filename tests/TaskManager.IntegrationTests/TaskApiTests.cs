using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace TaskManager.IntegrationTests;

[Collection("api")]
public class TaskApiTests
{
    private readonly ApiFactory _factory;

    public TaskApiTests(ApiFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private void RequireDb() => Skip.IfNot(_factory.DatabaseAvailable, "PostgreSQL not reachable on localhost:5432");

    private async Task<HttpClient> LoggedInClientAsync(string email = "demo@taskflow.app", string password = "password123")
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpClient> AdminClientAsync() => LoggedInClientAsync("admin", "admin123");

    [SkippableFact]
    public async Task Login_with_wrong_password_returns_401()
    {
        RequireDb();
        var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = "demo@taskflow.app", password = "nope" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Create_task_without_title_returns_400_with_errors()
    {
        RequireDb();
        var client = await LoggedInClientAsync();

        var res = await client.PostAsJsonAsync("/api/tasks", new { title = "" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Create_then_get_then_soft_delete_task()
    {
        RequireDb();
        var client = await LoggedInClientAsync();

        var create = await client.PostAsJsonAsync("/api/tasks", new { title = "Integration task", priority = "High" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("id").GetString();

        (await client.GetAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"/api/tasks/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Complete_nonexistent_task_returns_404()
    {
        RequireDb();
        var client = await LoggedInClientAsync();

        var res = await client.PostAsync($"/api/tasks/{Guid.NewGuid()}/complete", null);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Voice_transcribe_text_path_returns_extracted_fields()
    {
        RequireDb();
        var client = await LoggedInClientAsync();

        using var form = new MultipartFormDataContent { { new StringContent("call the bank tomorrow at 3pm urgent"), "text" } };
        var res = await client.PostAsync("/api/voice/transcribe", form);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var fields = body.GetProperty("data").GetProperty("extractedFields");
        fields.GetProperty("priority").GetString().Should().Be("Urgent");
    }

    [SkippableFact]
    public async Task Voice_config_reports_browser_mode_by_default()
    {
        RequireDb();
        var client = _factory.CreateClient();

        var res = await client.GetAsync("/api/voice/config");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("mode").GetString().Should().Be("Browser");
    }

    [SkippableFact]
    public async Task Voice_audio_upload_in_browser_mode_returns_409_not_400()
    {
        RequireDb();
        var client = await LoggedInClientAsync();

        var audio = new ByteArrayContent(new byte[2048]);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        using var form = new MultipartFormDataContent { { audio, "audio", "clip.webm" } };
        var res = await client.PostAsync("/api/voice/transcribe", form);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("errors")[0].GetProperty("message").GetString().Should().Be("provider_mode_mismatch");
    }

    // ---------- admin user management ----------

    [SkippableFact]
    public async Task Admin_can_list_users_without_password_hashes()
    {
        RequireDb();
        var client = await AdminClientAsync();

        var res = await client.GetAsync("/api/admin/users?page=1&pageSize=20");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().NotContainEquivalentOf("passwordHash");
        raw.Should().NotContainEquivalentOf("password_hash");
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Non_admin_is_forbidden_from_the_admin_api()
    {
        RequireDb();
        var client = await LoggedInClientAsync(); // demo user

        var list = await client.GetAsync("/api/admin/users");
        var summary = await client.GetAsync("/api/admin/users/summary");

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        summary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Admin_cannot_deactivate_their_own_account()
    {
        RequireDb();
        var client = await AdminClientAsync();
        var me = await client.GetFromJsonAsync<JsonElement>("/api/admin/users?search=admin");
        var adminId = me.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();

        var res = await client.PatchAsJsonAsync($"/api/admin/users/{adminId}/status", new { isActive = false });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Contain("your own account");
    }

    [SkippableFact]
    public async Task Deactivated_user_cannot_log_in_or_use_an_existing_token()
    {
        RequireDb();
        var demo = await LoggedInClientAsync();
        var demoToken = demo.DefaultRequestHeaders.Authorization!.Parameter;

        var admin = await AdminClientAsync();
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users?search=demo");
        var demoId = users.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();

        try
        {
            var deactivate = await admin.PatchAsJsonAsync(
                $"/api/admin/users/{demoId}/status", new { isActive = false, reason = "integration test" });
            deactivate.StatusCode.Should().Be(HttpStatusCode.OK);

            // 1. existing access token is rejected
            var withOldToken = _factory.CreateClient();
            withOldToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", demoToken);
            (await withOldToken.GetAsync("/api/tasks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // 2. cannot log in again
            var relogin = await _factory.CreateClient()
                .PostAsJsonAsync("/api/auth/login", new { email = "demo@taskflow.app", password = "password123" });
            relogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var body = await relogin.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("message").GetString().Should().Contain("inactive");
        }
        finally
        {
            await admin.PatchAsJsonAsync($"/api/admin/users/{demoId}/status", new { isActive = true });
        }
    }

    // ---------- admin user CRUD ----------

    private static object NewUserBody(string email, string role = "User", bool active = true) => new
    {
        displayName = "Integration User",
        email,
        role,
        password = "Str0ngPass1",
        confirmPassword = "Str0ngPass1",
        isActive = active,
    };

    [SkippableFact]
    public async Task Admin_creates_a_user_and_it_appears_in_the_list_without_secrets()
    {
        RequireDb();
        var admin = await AdminClientAsync();
        var email = $"crud-{Guid.NewGuid():N}@taskflow.test";

        var create = await admin.PostAsJsonAsync("/api/admin/users", NewUserBody(email));
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await create.Content.ReadAsStringAsync();
        raw.Should().NotContainEquivalentOf("passwordHash");
        raw.Should().NotContainEquivalentOf("password");

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={email}");
        list.GetProperty("data").GetProperty("items")[0].GetProperty("email").GetString().Should().Be(email);

        // the new user can sign in with the temporary password
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPass1" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Creating_a_user_with_a_duplicate_email_returns_400()
    {
        RequireDb();
        var admin = await AdminClientAsync();

        var res = await admin.PostAsJsonAsync("/api/admin/users", NewUserBody("demo@taskflow.app"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Non_admin_cannot_create_update_or_delete_users()
    {
        RequireDb();
        var user = await LoggedInClientAsync();

        var create = await user.PostAsJsonAsync("/api/admin/users", NewUserBody($"x-{Guid.NewGuid():N}@t.test"));
        var update = await user.PutAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}", new { displayName = "x", email = "x@x.x", role = "User", isActive = true });
        var reset = await user.PostAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}/reset-password", new { newPassword = "Str0ngPass1", confirmPassword = "Str0ngPass1" });
        var del = await user.DeleteAsync($"/api/admin/users/{Guid.NewGuid()}");

        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        reset.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task Admin_resets_a_password_and_the_old_one_stops_working()
    {
        RequireDb();
        var admin = await AdminClientAsync();
        var email = $"reset-{Guid.NewGuid():N}@taskflow.test";
        await admin.PostAsJsonAsync("/api/admin/users", NewUserBody(email));
        var created = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={email}");
        var id = created.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();

        var reset = await admin.PostAsJsonAsync($"/api/admin/users/{id}/reset-password",
            new { newPassword = "Rot4tedPass9", confirmPassword = "Rot4tedPass9" });
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reset.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("passwordHash");

        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPass1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Rot4tedPass9" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Deleting_a_user_soft_deletes_it_and_blocks_login()
    {
        RequireDb();
        var admin = await AdminClientAsync();
        var email = $"del-{Guid.NewGuid():N}@taskflow.test";
        await admin.PostAsJsonAsync("/api/admin/users", NewUserBody(email));
        var created = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={email}");
        var id = created.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();

        var del = await admin.DeleteAsync($"/api/admin/users/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        // hidden from the default list, visible with includeDeleted
        var def = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={email}");
        def.GetProperty("data").GetProperty("items").GetArrayLength().Should().Be(0);
        var incl = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={email}&includeDeleted=true");
        incl.GetProperty("data").GetProperty("items")[0].GetProperty("isDeleted").GetBoolean().Should().BeTrue();

        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPass1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- change password ----------

    private async Task<(string Email, string Token)> NewLoginAsync(string password = "Str0ngPass1")
    {
        var admin = await AdminClientAsync();
        var email = $"cpw-{Guid.NewGuid():N}@taskflow.test";
        (await admin.PostAsJsonAsync("/api/admin/users", new
        {
            displayName = "CP User", email, role = "User", password, confirmPassword = password, isActive = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("accessToken").GetString()!;
        return (email, token);
    }

    private static HttpClient WithToken(ApiFactory factory, string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [SkippableFact]
    public async Task Change_password_succeeds_and_the_old_password_stops_working()
    {
        RequireDb();
        var (email, token) = await NewLoginAsync();
        var client = WithToken(_factory, token);

        var res = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "Str0ngPass1", newPassword = "Rot4tedPass9", confirmNewPassword = "Rot4tedPass9",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().NotContainEquivalentOf("passwordHash");
        raw.Should().NotContainEquivalentOf("refreshToken");
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("data").GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();

        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPass1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Rot4tedPass9" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Change_password_rejects_an_incorrect_current_password()
    {
        RequireDb();
        var (_, token) = await NewLoginAsync();

        var res = await WithToken(_factory, token).PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "totally-wrong", newPassword = "Rot4tedPass9", confirmNewPassword = "Rot4tedPass9",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("message").GetString().Should().Be("Current password is incorrect.");
    }

    [SkippableTheory]
    [InlineData("Str0ngPass1", "Rot4tedPass9", "different99")] // mismatch
    [InlineData("Str0ngPass1", "Str0ngPass1", "Str0ngPass1")]  // same as current
    [InlineData("Str0ngPass1", "weak", "weak")]                // policy
    public async Task Change_password_validation_failures_return_400(string cur, string next, string confirm)
    {
        RequireDb();
        var (_, token) = await NewLoginAsync();

        var res = await WithToken(_factory, token).PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = cur, newPassword = next, confirmNewPassword = confirm });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Change_password_requires_authentication()
    {
        RequireDb();
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "x", newPassword = "Rot4tedPass9", confirmNewPassword = "Rot4tedPass9",
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Changing_the_password_invalidates_other_access_tokens()
    {
        RequireDb();
        var (email, tokenA) = await NewLoginAsync();
        var loginB = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Str0ngPass1" });
        var tokenB = (await loginB.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("accessToken").GetString()!;

        // session B still works before the change
        (await WithToken(_factory, tokenB).GetAsync("/api/tasks")).StatusCode.Should().Be(HttpStatusCode.OK);

        var change = await WithToken(_factory, tokenA).PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "Str0ngPass1", newPassword = "Rot4tedPass9", confirmNewPassword = "Rot4tedPass9",
        });
        change.StatusCode.Should().Be(HttpStatusCode.OK);

        // session B's access token is now rejected (security stamp rotated)
        (await WithToken(_factory, tokenB).GetAsync("/api/tasks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Admin_cannot_delete_their_own_account()
    {
        RequireDb();
        var admin = await AdminClientAsync();
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users?search=admin");
        var adminId = me.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();

        var res = await admin.DeleteAsync($"/api/admin/users/{adminId}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("message").GetString().Should().Contain("your own account");
    }

    // ---------- forgot password / OTP reset ----------

    private const string GenericReset = "If an account exists for this email address, we have sent reset instructions.";
    private static readonly Regex OtpPattern = new("[A-Z2-9]{5}-[A-Z2-9]{5}");

    private async Task<string> CreateActiveUserAsync(string password = "Str0ngPass1")
    {
        var admin = await AdminClientAsync();
        var email = $"reset-{Guid.NewGuid():N}@taskflow.test";
        (await admin.PostAsJsonAsync("/api/admin/users", new
        {
            displayName = "Reset User", email, role = "User", password, confirmPassword = password, isActive = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        return email;
    }

    private async Task<string> RequestOtpAsync(string email)
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password",
            new { email, captchaToken = "test-bypass" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var msg = _factory.Emails.LastTo(email);
        msg.Should().NotBeNull("an active user should receive a reset email");
        return OtpPattern.Match(msg!.TextBody).Value;
    }

    [SkippableFact]
    public async Task Forgot_password_gives_the_same_generic_answer_for_known_and_unknown_emails()
    {
        RequireDb();
        var known = await CreateActiveUserAsync();
        var client = _factory.CreateClient();

        var a = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = known, captchaToken = "test-bypass" });
        var b = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { email = $"nobody-{Guid.NewGuid():N}@taskflow.test", captchaToken = "test-bypass" });

        a.StatusCode.Should().Be(HttpStatusCode.OK);
        b.StatusCode.Should().Be(HttpStatusCode.OK);
        (await a.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Be(GenericReset);
        (await b.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Be(GenericReset);
    }

    [SkippableFact]
    public async Task Forgot_password_rejects_a_bad_captcha()
    {
        RequireDb();
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password",
            new { email = "whoever@taskflow.test", captchaToken = "wrong" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors")[0].GetProperty("field").GetString().Should().Be("captchaToken");
    }

    [SkippableFact]
    public async Task Otp_login_forces_a_password_change_then_the_reset_flow_completes()
    {
        RequireDb();
        var email = await CreateActiveUserAsync();
        var otp = await RequestOtpAsync(email);
        otp.Should().NotBeNullOrEmpty();

        // 1. sign in with the one-time password
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = otp });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
        loginBody.GetProperty("data").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        var otpToken = loginBody.GetProperty("data").GetProperty("accessToken").GetString()!;

        // 2. every protected endpoint is blocked until the password is changed
        var blocked = await WithToken(_factory, otpToken).GetAsync("/api/tasks");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors")[0].GetProperty("message").GetString().Should().Be("must_change_password");

        // 3. finish the forced change (no current password required)
        var complete = await WithToken(_factory, otpToken).PostAsJsonAsync("/api/auth/complete-password-reset",
            new { newPassword = "Fr3shPass77", confirmNewPassword = "Fr3shPass77" });
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        var freshToken = (await complete.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("accessToken").GetString()!;

        // 4. the fresh token works everywhere; the old OTP token is dead
        (await WithToken(_factory, freshToken).GetAsync("/api/tasks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WithToken(_factory, otpToken).GetAsync("/api/tasks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 5. the temporary password no longer works; the new one does
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = otp }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "Fr3shPass77" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task A_used_otp_cannot_be_used_again()
    {
        RequireDb();
        var email = await CreateActiveUserAsync();
        var otp = await RequestOtpAsync(email);

        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = otp }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        // second attempt with the same code
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = otp }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Requesting_a_new_otp_invalidates_the_previous_one()
    {
        RequireDb();
        var email = await CreateActiveUserAsync();
        var first = await RequestOtpAsync(email);
        var second = await RequestOtpAsync(email);
        second.Should().NotBe(first);

        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = first }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = second }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Complete_password_reset_requires_authentication()
    {
        RequireDb();
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/complete-password-reset",
            new { newPassword = "Fr3shPass77", confirmNewPassword = "Fr3shPass77" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Deactivated_user_cannot_request_a_reset()
    {
        RequireDb();
        var email = await CreateActiveUserAsync();
        var admin = await AdminClientAsync();
        var users = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={email}");
        var id = users.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString();
        (await admin.PatchAsJsonAsync($"/api/admin/users/{id}/status", new { isActive = false })).EnsureSuccessStatusCode();

        var before = _factory.Emails.Sent.Count;
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password",
            new { email, captchaToken = "test-bypass" });

        res.StatusCode.Should().Be(HttpStatusCode.OK); // still generic
        _factory.Emails.Sent.Count.Should().Be(before); // but nothing was sent
    }
}
