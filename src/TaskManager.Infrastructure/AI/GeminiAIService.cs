using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskManager.Application.AI;
using TaskManager.Application.AI.DTOs;

namespace TaskManager.Infrastructure.AI;

/// <summary>
/// Gemini-backed implementation of <see cref="IAIService"/>. Builds one prompt per method from the
/// embedded templates in AI/Prompts, calls <see cref="GeminiClient"/>, and turns the JSON/text
/// response into the corresponding DTO. Every call records token usage onto the scoped
/// <see cref="AiCallContext"/> so <see cref="AiUsageLoggingBehavior{TRequest,TResponse}"/> can log it.
/// </summary>
public class GeminiAIService : IAIService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly GeminiClient _client;
    private readonly AiCallContext _callContext;

    public GeminiAIService(GeminiClient client, AiCallContext callContext)
    {
        _client = client;
        _callContext = callContext;
    }

    // ---------------------------------------------------------------- Smart Quick Add

    public async Task<TaskDraftDto> AnalyzeTaskTextAsync(string input, DateTime currentDateUtc, CancellationToken ct)
    {
        var prompt = PromptLoader.Load("AnalyzeQuickAdd.txt")
            .Replace("{{CURRENT_DATE}}", currentDateUtc.ToString("yyyy-MM-dd"))
            .Replace("{{USER_TEXT}}", input);

        var raw = await CallJsonAsync("system prompt inline", prompt, ct);
        var parsed = Deserialize<RawTaskDraft>(raw);

        return new TaskDraftDto(
            string.IsNullOrWhiteSpace(parsed.Title) ? "New task" : parsed.Title.Trim(),
            NullIfEmpty(parsed.Description),
            ParseDate(parsed.DueDate),
            ParseTime(parsed.DueTime),
            NullIfEmpty(parsed.Priority),
            NullIfEmpty(parsed.Category),
            parsed.Tags ?? Array.Empty<string>());
    }

    // ---------------------------------------------------------------- Task Analyzer

    public async Task<TaskAnalysisDto> AnalyzeTaskAsync(
        string title, string? description, string priority, DateOnly? dueDate, bool completed, bool overdue,
        int subtaskTotal, int subtaskDone, string? nextOpenSubtask, string[] tags, CancellationToken ct)
    {
        var facts = $"""
            Title: {title}
            Description: {description ?? "(none)"}
            Current priority: {priority}
            Due date: {(dueDate?.ToString("yyyy-MM-dd") ?? "(not scheduled)")}
            Completed: {completed}
            Overdue: {overdue}
            Subtasks: {subtaskDone}/{subtaskTotal} done{(nextOpenSubtask is null ? "" : $"; next open: \"{nextOpenSubtask}\"")}
            Existing tags: {(tags.Length == 0 ? "(none)" : string.Join(", ", tags))}
            """;
        var prompt = PromptLoader.Load("AnalyzeTask.txt").Replace("{{TASK_FACTS}}", facts);

        var raw = await CallJsonAsync("system prompt inline", prompt, ct);
        var parsed = Deserialize<RawTaskAnalysis>(raw);

        return new TaskAnalysisDto(
            NullIfEmpty(parsed.Priority) ?? priority,
            NullIfEmpty(parsed.EstimatedEffort) ?? "1–2 hours",
            NullIfEmpty(parsed.Complexity) ?? "Medium",
            NullIfEmpty(parsed.SuggestedDeadline) ?? "Not specified",
            parsed.SuggestedTags ?? Array.Empty<string>(),
            NullIfEmpty(parsed.PotentialBlocker) ?? "None identified",
            NullIfEmpty(parsed.RecommendedNextAction) ?? "Review the task and decide the next step.");
    }

    // ---------------------------------------------------------------- Task Breakdown

    public async Task<SubtaskTreeDto> BreakDownTaskAsync(string title, string? description, CancellationToken ct)
    {
        var prompt = PromptLoader.Load("BreakDownTask.txt")
            .Replace("{{TITLE}}", title)
            .Replace("{{DESCRIPTION}}", description ?? "(none)");

        var raw = await CallJsonAsync("system prompt inline", prompt, ct);
        var parsed = Deserialize<RawBreakdown>(raw);
        var subtasks = (parsed.Subtasks ?? new List<RawSubtask>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Title))
            .Select(s => new SubtaskNodeDto(s.Title!.Trim()))
            .ToList();

        if (subtasks.Count == 0)
        {
            throw new AIServiceException("The AI did not return any subtasks.");
        }
        return new SubtaskTreeDto(subtasks);
    }

    // ---------------------------------------------------------------- Daily Planner

    public async Task<DailyPlanDto> GenerateDailyPlanAsync(
        IReadOnlyList<TaskSummaryDto> todaysTasks, WorkingHoursDto workingHours, DateOnly date, CancellationToken ct)
    {
        if (todaysTasks.Count == 0)
        {
            return new DailyPlanDto(date, new List<PlanBlockDto>());
        }

        var tasksJson = JsonSerializer.Serialize(todaysTasks.Select(t => new
        {
            id = t.Id,
            title = t.Title,
            priority = t.Priority,
            overdue = t.DueDate is { } d && d < date,
        }));
        var prompt = PromptLoader.Load("DailyPlan.txt")
            .Replace("{{WORKING_HOURS}}", $"{workingHours.Start:HH:mm}-{workingHours.End:HH:mm}")
            .Replace("{{CURRENT_DATE}}", date.ToString("yyyy-MM-dd"))
            .Replace("{{TASKS_JSON}}", tasksJson);

        var raw = await CallJsonAsync("system prompt inline", prompt, ct);
        var parsed = Deserialize<RawDailyPlan>(raw);

        var byId = todaysTasks.ToDictionary(t => t.Id);
        var blocks = new List<PlanBlockDto>();
        foreach (var b in parsed.Blocks ?? new List<RawPlanBlock>())
        {
            if (!Guid.TryParse(b.TaskId, out var id) || !byId.TryGetValue(id, out var task))
            {
                continue; // never trust a task id Gemini invented
            }
            if (!TimeOnly.TryParse(b.Start, out var start) || !TimeOnly.TryParse(b.End, out var end))
            {
                continue;
            }
            blocks.Add(new PlanBlockDto(id, task.Title, task.Priority, start, end, NullIfEmpty(b.Reason) ?? "Scheduled"));
        }

        return new DailyPlanDto(date, blocks.OrderBy(b => b.Start).ToList());
    }

    // ---------------------------------------------------------------- Next-task recommendation

    public async Task<NextTaskRecommendationDto?> RecommendNextTaskAsync(
        IReadOnlyList<TaskSummaryDto> openTasks, CancellationToken ct)
    {
        if (openTasks.Count == 0)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var tasksJson = JsonSerializer.Serialize(openTasks.Select(t => new
        {
            id = t.Id,
            title = t.Title,
            priority = t.Priority,
            dueDate = t.DueDate?.ToString("yyyy-MM-dd"),
            overdue = t.DueDate is { } d && d < today,
            subtaskProgress = t.SubtaskCount == 0 ? (double?)null : (double)t.SubtaskDoneCount / t.SubtaskCount,
        }));
        var prompt = PromptLoader.Load("NextTask.txt").Replace("{{TASKS_JSON}}", tasksJson);

        var raw = await CallJsonAsync("system prompt inline", prompt, ct);
        var parsed = Deserialize<RawNextTask>(raw);

        if (!Guid.TryParse(parsed.TaskId, out var id) || openTasks.All(t => t.Id != id))
        {
            return null; // never trust an invented id — the UI simply shows no recommendation
        }
        var task = openTasks.First(t => t.Id == id);
        return new NextTaskRecommendationDto(
            id, task.Title,
            (parsed.Reasons ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList(),
            NullIfEmpty(parsed.SuggestedAction) ?? "Block focus time and get started.");
    }

    // ---------------------------------------------------------------- Natural-language search

    public async Task<SearchFilterDto> ParseSearchQueryAsync(string naturalLanguageQuery, CancellationToken ct)
    {
        var prompt = PromptLoader.Load("SearchQuery.txt").Replace("{{QUERY}}", naturalLanguageQuery);
        var raw = await CallJsonAsync("system prompt inline", prompt, ct);
        var parsed = Deserialize<RawSearchFilter>(raw);

        return new SearchFilterDto(
            NullIfEmpty(parsed.Status), parsed.Priority, NullIfEmpty(parsed.Category),
            NullIfEmpty(parsed.Due), NullIfEmpty(parsed.Search));
    }

    // ---------------------------------------------------------------- Daily Summary (plain text)

    public async Task<string> SummarizeDayAsync(DailyStatsDto stats, CancellationToken ct)
    {
        var statsText = $"""
            Completed: {stats.Completed}
            Remaining: {stats.Remaining}
            Overdue: {stats.Overdue}
            Completion rate: {stats.CompletionRate}%
            High-priority tasks completed: {stats.HighPriorityCompleted}
            """;
        var prompt = PromptLoader.Load("DailySummary.txt").Replace("{{STATS}}", statsText);

        if (!_client.IsConfigured)
        {
            throw new AIServiceUnavailableException("The Gemini API key is not configured on the server.");
        }
        var result = await _client.GenerateTextAsync(prompt, "Write the summary now.", ct);
        _callContext.Record(_client.Model, result.InputTokens, result.OutputTokens);
        return result.RawText.Trim('"', ' ', '\n', '\r');
    }

    // ---------------------------------------------------------------- AI Assistant (function calling)

    public async Task<AiToolCallDto> ResolveAssistantIntentAsync(
        string userMessage, IReadOnlyList<ChatMessageDto> recentHistory, IReadOnlyList<string> allowedTools, CancellationToken ct)
    {
        var system = PromptLoader.Load("AssistantSystem.txt")
            .Replace("{{CURRENT_DATE}}", DateTime.UtcNow.ToString("yyyy-MM-dd"));

        var tools = allowedTools
            .Select(AssistantToolCatalog.Find)
            .Where(spec => spec is not null)
            .Select(spec => new GeminiToolDeclaration(spec!.Name, spec.Description, BuildSchema(spec)))
            .ToList();

        var history = recentHistory.Select(m => (m.Role, m.Content));

        if (!_client.IsConfigured)
        {
            throw new AIServiceUnavailableException("The Gemini API key is not configured on the server.");
        }
        var result = await _client.GenerateWithToolsAsync(system, history, userMessage, tools, ct);
        _callContext.Record(_client.Model, result.InputTokens, result.OutputTokens);

        if (result.ToolName is null)
        {
            return new AiToolCallDto(null, null, result.TextReply);
        }
        if (!allowedTools.Contains(result.ToolName, StringComparer.Ordinal))
        {
            // Defense in depth: Gemini named a tool that either isn't in the whitelist at all, or
            // wasn't offered in *this* request's tool list. Never pass it through.
            return new AiToolCallDto(null, null, "I'm not able to do that directly, but I can help another way — try rephrasing.");
        }

        var args = result.Arguments is null
            ? new Dictionary<string, object?>()
            : result.Arguments.ToDictionary(kv => kv.Key, kv => JsonNodeToObject(kv.Value));
        return new AiToolCallDto(result.ToolName, args, null);
    }

    // ---------------------------------------------------------------- shared plumbing

    private async Task<string> CallJsonAsync(string _, string userPrompt, CancellationToken ct)
    {
        if (!_client.IsConfigured)
        {
            throw new AIServiceUnavailableException("The Gemini API key is not configured on the server.");
        }
        // Every structured-output prompt is fully self-contained (instructions + data), so the
        // "system" half is folded into the single prompt text and passed as the system instruction.
        var split = SplitSystemAndUser(userPrompt);
        var result = await _client.GenerateJsonAsync(split.system, split.user, ct);
        _callContext.Record(_client.Model, result.InputTokens, result.OutputTokens);
        return result.RawText;
    }

    /// <summary>
    /// Our prompt templates are single blocks of "instructions + data" text. Gemini works fine
    /// with everything in one user turn, so we just pass the whole thing as the system
    /// instruction and a minimal user turn — keeps GenerateJsonAsync's signature simple.
    /// </summary>
    private static (string system, string user) SplitSystemAndUser(string prompt) => (prompt, "Respond now, following the format above exactly.");

    private static T Deserialize<T>(string json)
    {
        try
        {
            // Gemini's JSON-mode output is sometimes fenced despite instructions not to — strip it defensively.
            var cleaned = json.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.Trim('`').Trim();
                if (cleaned.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned[4..].Trim();
                }
            }
            return JsonSerializer.Deserialize<T>(cleaned, JsonOpts)
                ?? throw new AIServiceException("The AI response parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new AIServiceException("The AI response was not in the expected format.", ex);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateOnly? ParseDate(string? s) => DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static TimeOnly? ParseTime(string? s) => TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static JsonObject BuildSchema(AssistantToolSpec spec)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in spec.Parameters)
        {
            properties[p.Name] = new JsonObject
            {
                ["type"] = p.Type == "number" ? "NUMBER" : "STRING",
                ["description"] = p.Description,
            };
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }
        var schema = new JsonObject { ["type"] = "OBJECT", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static object? JsonNodeToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out bool b) => b,
        _ => node.ToJsonString(),
    };

    // ---- raw wire-shape records used only for deserializing Gemini's JSON output ----
    private record RawTaskDraft(string? Title, string? Description, string? DueDate, string? DueTime, string? Priority, string? Category, string[]? Tags);
    private record RawTaskAnalysis(string? Priority, string? EstimatedEffort, string? Complexity, string? SuggestedDeadline, string[]? SuggestedTags, string? PotentialBlocker, string? RecommendedNextAction);
    private record RawSubtask(string? Title);
    private record RawBreakdown(List<RawSubtask>? Subtasks);
    private record RawPlanBlock(string? TaskId, string? Start, string? End, string? Reason);
    private record RawDailyPlan(List<RawPlanBlock>? Blocks);
    private record RawNextTask(string? TaskId, List<string>? Reasons, string? SuggestedAction);
    private record RawSearchFilter(string? Status, string[]? Priority, string? Category, string? Due, string? Search);
}
