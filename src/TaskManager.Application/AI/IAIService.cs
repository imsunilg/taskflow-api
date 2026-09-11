using TaskManager.Application.AI.DTOs;

namespace TaskManager.Application.AI;

/// <summary>
/// The single seam between TaskFlow and whatever AI model powers it (Gemini today).
/// Every method returns plain data — no method here ever mutates TaskFlow state.
/// Implementations must throw <see cref="AIServiceException"/> on an unparsable response
/// and <see cref="AIServiceUnavailableException"/> on timeout/network failure — never
/// silently fall back to a fabricated result.
/// </summary>
public interface IAIService
{
    Task<TaskDraftDto> AnalyzeTaskTextAsync(string input, DateTime currentDateUtc, CancellationToken ct);

    Task<TaskAnalysisDto> AnalyzeTaskAsync(
        string title, string? description, string priority, DateOnly? dueDate, bool completed, bool overdue,
        int subtaskTotal, int subtaskDone, string? nextOpenSubtask, string[] tags, CancellationToken ct);

    Task<SubtaskTreeDto> BreakDownTaskAsync(string title, string? description, CancellationToken ct);

    Task<DailyPlanDto> GenerateDailyPlanAsync(
        IReadOnlyList<TaskSummaryDto> todaysTasks, WorkingHoursDto workingHours, DateOnly date, CancellationToken ct);

    Task<NextTaskRecommendationDto?> RecommendNextTaskAsync(IReadOnlyList<TaskSummaryDto> openTasks, CancellationToken ct);

    Task<SearchFilterDto> ParseSearchQueryAsync(string naturalLanguageQuery, CancellationToken ct);

    Task<string> SummarizeDayAsync(DailyStatsDto stats, CancellationToken ct);

    /// <summary>
    /// Function-calling entry point for the AI Assistant. Given the whitelist of tool names
    /// the caller is allowed to invoke, resolves the user's message to either a tool call
    /// (name + arguments) or a plain text reply. Never invents a tool name outside the whitelist —
    /// if Gemini attempts to, implementations must surface it as <c>ToolName = null</c> with a
    /// clarifying <c>TextReply</c> rather than passing the invalid name through.
    /// </summary>
    Task<AiToolCallDto> ResolveAssistantIntentAsync(
        string userMessage, IReadOnlyList<ChatMessageDto> recentHistory, IReadOnlyList<string> allowedTools, CancellationToken ct);
}
