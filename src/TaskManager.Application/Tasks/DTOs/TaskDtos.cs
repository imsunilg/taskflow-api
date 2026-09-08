using TaskManager.Domain.Enums;

namespace TaskManager.Application.Tasks.DTOs;

public record CreateTaskRequest(
    string Title,
    string? Description,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    Priority? Priority,
    Guid? CategoryId,
    string[]? TagNames,
    string[]? SubtaskTexts,
    int? ReminderOffsetMinutes,
    RecurrenceFrequency? RecurrenceFrequency,
    int? RecurrenceInterval,
    string? Notes);

public record UpdateTaskRequest(
    string? Title,
    string? Description,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    Priority? Priority,
    Guid? CategoryId,
    string[]? TagNames,
    int? ReminderOffsetMinutes,
    RecurrenceFrequency? RecurrenceFrequency,
    int? RecurrenceInterval,
    string? Notes,
    bool? Completed,
    bool? Important);

public record SubtaskResponse(Guid Id, Guid TaskId, string Text, bool IsDone, int SortOrder);

public record ReminderResponse(Guid Id, Guid TaskId, int OffsetMinutes, DateTime FiresAt, DateTime? DeliveredAt);

public record RecurrenceRuleResponse(
    Guid Id,
    string Frequency,
    int Interval,
    int[]? DaysOfWeek,
    int? DayOfMonth,
    DateOnly? Until);

public record TaskHistoryResponse(Guid Id, Guid TaskId, string ChangeText, DateTime ChangedAt, Guid ChangedBy);

public record TaskResponse(
    Guid Id,
    Guid UserId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    string Priority,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryColor,
    bool Completed,
    DateTime? CompletedAt,
    bool Important,
    string? Notes,
    bool IsVoiceCreated,
    string[] Tags,
    SubtaskResponse[] Subtasks,
    ReminderResponse? Reminder,
    RecurrenceRuleResponse? RecurrenceRule,
    TaskHistoryResponse[] History,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreateSubtaskRequest(string Text);
public record UpdateSubtaskRequest(string? Text, bool? IsDone, int? SortOrder);
