namespace TaskManager.Application.AI.DTOs;

// ---- Smart Quick Add / task drafting -------------------------------------

public record TaskDraftDto(
    string Title,
    string? Description,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    string? Priority,
    string? Category,
    string[] Tags);

// ---- Task Analyzer ---------------------------------------------------------

public record TaskAnalysisDto(
    string Priority,
    string EstimatedEffort,
    string Complexity,
    string SuggestedDeadline,
    string[] SuggestedTags,
    string PotentialBlocker,
    string RecommendedNextAction);

// ---- Task Breakdown ---------------------------------------------------------

public record SubtaskNodeDto(string Title, List<SubtaskNodeDto>? Children = null);
public record SubtaskTreeDto(List<SubtaskNodeDto> Subtasks);

// ---- Daily Planner -----------------------------------------------------------

public record WorkingHoursDto(TimeOnly Start, TimeOnly End);

public record PlanBlockDto(
    Guid TaskId,
    string Title,
    string Priority,
    TimeOnly Start,
    TimeOnly End,
    string Reason);

public record DailyPlanDto(DateOnly Date, List<PlanBlockDto> Blocks);

// ---- Next-task recommendation ------------------------------------------------

public record NextTaskRecommendationDto(
    Guid TaskId,
    string Title,
    List<string> Reasons,
    string SuggestedAction);

// ---- Natural-language search --------------------------------------------------

public record SearchFilterDto(
    string? Status,        // overdue | incomplete | completed
    string[]? Priority,
    string? Category,
    string? Due,           // today | tomorrow | this_week
    string? Search);

// ---- Task summary shapes shared by planner / recommendation / assistant tools -

public record TaskSummaryDto(
    Guid Id,
    string Title,
    string Priority,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    bool Completed,
    bool Important,
    string? CategoryName,
    int SubtaskCount,
    int SubtaskDoneCount,
    bool HasRecurrence);

// ---- Daily summary -------------------------------------------------------------

public record DailyStatsDto(
    int Completed,
    int Remaining,
    int Overdue,
    int CompletionRate,
    int HighPriorityCompleted);

// ---- AI Assistant (function calling) --------------------------------------------

public record ChatMessageDto(string Role, string Content);

/// <summary>The resolved outcome of one assistant turn: either plain text, or a whitelisted tool call.</summary>
public record AiToolCallDto(
    string? ToolName,
    IReadOnlyDictionary<string, object?>? Arguments,
    string? TextReply);
