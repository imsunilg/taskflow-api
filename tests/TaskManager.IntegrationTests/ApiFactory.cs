using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TaskManager.Application.AI;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Interfaces;
using Xunit;

namespace TaskManager.IntegrationTests;

/// <summary>Minimal <see cref="IHostEnvironment"/> for exercising env-gated code paths.</summary>
public sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

/// <summary>Captures reset emails instead of hitting a real SMTP relay during tests.</summary>
public class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = new();
    public bool IsConfigured => true;
    public IReadOnlyList<EmailMessage> Sent => _sent;
    public EmailMessage? LastTo(string email) => _sent.LastOrDefault(m =>
        string.Equals(m.ToEmail, email, StringComparison.OrdinalIgnoreCase));

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stands in for GeminiAIService so integration tests exercise the real controller → MediatR →
/// ToolDispatcher → repository pipeline without ever calling the real Gemini API. Deterministic,
/// simple pattern matching only — real intent parsing is covered by GeminiAIService/GeminiClient
/// unit tests with a mocked HttpMessageHandler.
/// </summary>
public class FakeAIService : IAIService
{
    private readonly AiCallContext _callContext;
    public FakeAIService(AiCallContext callContext) => _callContext = callContext;

    private void RecordUsage() => _callContext.Record("fake-model", 10, 10);

    public Task<TaskDraftDto> AnalyzeTaskTextAsync(string input, DateTime currentDateUtc, CancellationToken ct)
    {
        RecordUsage();
        return Task.FromResult(new TaskDraftDto(
            string.IsNullOrWhiteSpace(input) ? "New task" : input.Trim(), null,
            DateOnly.FromDateTime(currentDateUtc.AddDays(1)), null, "High", null, Array.Empty<string>()));
    }

    public Task<TaskAnalysisDto> AnalyzeTaskAsync(
        string title, string? description, string priority, DateOnly? dueDate, bool completed, bool overdue,
        int subtaskTotal, int subtaskDone, string? nextOpenSubtask, string[] tags, CancellationToken ct)
    {
        RecordUsage();
        return Task.FromResult(new TaskAnalysisDto(
            priority, "1-2 hours", "Medium", dueDate?.ToString() ?? "Not scheduled",
            Array.Empty<string>(), "None identified", "Just do it."));
    }

    public Task<SubtaskTreeDto> BreakDownTaskAsync(string title, string? description, CancellationToken ct)
    {
        RecordUsage();
        return Task.FromResult(new SubtaskTreeDto(new List<SubtaskNodeDto>
        {
            new("Step one"), new("Step two"), new("Step three"),
        }));
    }

    public Task<DailyPlanDto> GenerateDailyPlanAsync(
        IReadOnlyList<TaskSummaryDto> todaysTasks, WorkingHoursDto workingHours, DateOnly date, CancellationToken ct)
    {
        RecordUsage();
        var blocks = todaysTasks.Take(3).Select((t, i) => new PlanBlockDto(
            t.Id, t.Title, t.Priority, new TimeOnly(9 + i, 0), new TimeOnly(10 + i, 0), "Scheduled")).ToList();
        return Task.FromResult(new DailyPlanDto(date, blocks));
    }

    public Task<NextTaskRecommendationDto?> RecommendNextTaskAsync(IReadOnlyList<TaskSummaryDto> openTasks, CancellationToken ct)
    {
        RecordUsage();
        if (openTasks.Count == 0) return Task.FromResult<NextTaskRecommendationDto?>(null);
        var top = openTasks[0];
        return Task.FromResult<NextTaskRecommendationDto?>(
            new NextTaskRecommendationDto(top.Id, top.Title, new List<string> { "Top of the list" }, "Get started."));
    }

    public Task<SearchFilterDto> ParseSearchQueryAsync(string naturalLanguageQuery, CancellationToken ct)
    {
        RecordUsage();
        var lower = naturalLanguageQuery.ToLowerInvariant();
        return Task.FromResult(new SearchFilterDto(
            lower.Contains("overdue") ? "overdue" : null, null, null, null, null));
    }

    public Task<string> SummarizeDayAsync(DailyStatsDto stats, CancellationToken ct)
    {
        RecordUsage();
        return Task.FromResult($"You completed {stats.Completed} task(s) today ({stats.CompletionRate}%). Keep it up tomorrow.");
    }

    /// <summary>
    /// Test-controlled intent resolution: "create task &lt;title&gt;" -> CreateTask;
    /// "reschedule &lt;title&gt; to &lt;yyyy-MM-dd&gt;" -> RescheduleTask;
    /// "USE_INVALID_TOOL" -> a tool name outside the whitelist (proves the server rejects it);
    /// anything else -> a plain text reply.
    /// </summary>
    public Task<AiToolCallDto> ResolveAssistantIntentAsync(
        string userMessage, IReadOnlyList<ChatMessageDto> recentHistory, IReadOnlyList<string> allowedTools, CancellationToken ct)
    {
        RecordUsage();
        var msg = userMessage.Trim();

        if (msg == "USE_INVALID_TOOL")
        {
            return Task.FromResult(new AiToolCallDto("HackTheMainframe", new Dictionary<string, object?>(), null));
        }
        if (msg.StartsWith("create task ", StringComparison.OrdinalIgnoreCase))
        {
            var title = msg["create task ".Length..].Trim();
            return Task.FromResult(new AiToolCallDto("CreateTask",
                new Dictionary<string, object?> { ["title"] = title }, null));
        }
        var rescheduleMatch = System.Text.RegularExpressions.Regex.Match(
            msg, @"^reschedule (.+) to (\d{4}-\d{2}-\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rescheduleMatch.Success)
        {
            return Task.FromResult(new AiToolCallDto("RescheduleTask", new Dictionary<string, object?>
            {
                ["titleQuery"] = rescheduleMatch.Groups[1].Value,
                ["newDueDate"] = rescheduleMatch.Groups[2].Value,
            }, null));
        }
        if (msg.StartsWith("show overdue", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new AiToolCallDto("GetOverdueTasks", new Dictionary<string, object?>(), null));
        }

        return Task.FromResult(new AiToolCallDto(null, null, "I'm a fake assistant and didn't recognise that."));
    }
}

/// <summary>
/// Spins up the real API against a local PostgreSQL test database. If PostgreSQL is not
/// reachable the whole collection is skipped (so CI without a DB service stays green).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string BaseConn =
        "Host=localhost;Port=5432;Username=postgres;Password=284228";
    private const string TestDb = "taskflow_inttest";

    public bool DatabaseAvailable { get; private set; }

    public readonly CapturingEmailSender Emails = new();

    public async Task InitializeAsync()
    {
        try
        {
            await using var admin = new NpgsqlConnection($"{BaseConn};Database=postgres");
            await admin.OpenAsync();
            await using (var drop = admin.CreateCommand())
            {
                drop.CommandText = $"DROP DATABASE IF EXISTS {TestDb} WITH (FORCE);";
                await drop.ExecuteNonQueryAsync();
            }
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE {TestDb};";
                await create.ExecuteNonQueryAsync();
            }
            DatabaseAvailable = true;
        }
        catch
        {
            DatabaseAvailable = false;
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = $"{BaseConn};Database={TestDb}",
                ["ConnectionStrings:Redis"] = "",
                ["SpeechToText:Provider"] = "None",
                ["RateLimiting:Enabled"] = "false",
                ["Jwt:Secret"] = "integration-tests-signing-secret-that-is-long-enough",
                ["Email:Enabled"] = "true",
                ["Email:SmtpHost"] = "unused-in-tests",
                ["Email:FromEmail"] = "noreply@taskflow.test",
                ["Captcha:Provider"] = "disabled",
                ["Captcha:DevBypassToken"] = "test-bypass",
                ["Gemini:DailyRequestCap"] = "1000",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            services.RemoveAll<IAIService>();
            services.AddScoped<IAIService, FakeAIService>();
        });
        return base.CreateHost(builder);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
