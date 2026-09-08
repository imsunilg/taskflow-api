using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Tasks;

public static class TaskMapper
{
    public static TaskResponse ToResponse(this TaskEntity e)
    {
        return new TaskResponse(
            e.Id,
            e.UserId,
            e.Title,
            e.Description,
            e.DueDate,
            e.DueTime,
            e.Priority.ToString(),
            e.CategoryId,
            e.Category?.Name,
            e.Category?.Color,
            e.Completed,
            e.CompletedAt,
            e.Important,
            e.Notes,
            e.IsVoiceCreated,
            e.TaskTags.Select(tt => tt.Tag?.Name ?? string.Empty).Where(n => n.Length > 0).ToArray(),
            e.Subtasks.OrderBy(s => s.SortOrder)
                .Select(s => new SubtaskResponse(s.Id, s.TaskId, s.Text, s.IsDone, s.SortOrder)).ToArray(),
            e.Reminder is null
                ? null
                : new ReminderResponse(e.Reminder.Id, e.Reminder.TaskId, e.Reminder.OffsetMinutes,
                    e.Reminder.FiresAt, e.Reminder.DeliveredAt),
            e.RecurrenceRule is null
                ? null
                : new RecurrenceRuleResponse(e.RecurrenceRule.Id, e.RecurrenceRule.Frequency.ToString(),
                    e.RecurrenceRule.Interval, e.RecurrenceRule.DaysOfWeek, e.RecurrenceRule.DayOfMonth,
                    e.RecurrenceRule.Until),
            e.History.OrderByDescending(h => h.ChangedAt)
                .Select(h => new TaskHistoryResponse(h.Id, h.TaskId, h.ChangeText, h.ChangedAt, h.ChangedBy)).ToArray(),
            e.CreatedAt,
            e.UpdatedAt);
    }
}
