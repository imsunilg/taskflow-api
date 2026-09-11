using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Reminders;
using TaskManager.Application.Tasks;
using TaskManager.Application.Tasks.Commands;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Application.AI;

public sealed record CreateTaskPreview(TaskDraftDto Task);
public sealed record RescheduleChange(Guid TaskId, string Title, string From, string To);
public sealed record RescheduleDiffPreview(List<RescheduleChange> Changes);
public sealed record GenericActionPreview(string ToolName, string Summary);

public sealed record ToolExecutionResult(
    bool Success,
    string Message,
    List<TaskSummaryDto>? Tasks = null,
    object? Preview = null);

/// <summary>
/// Executes exactly the tools declared in <see cref="AssistantToolCatalog"/> — nothing else can
/// reach this class's switch statements. Read-only tools run immediately. Mutating tools are
/// first turned into a <see cref="ToolExecutionResult.Preview"/> via <see cref="BuildPreviewAsync"/>
/// (no database write), and only actually applied from <see cref="ApplyAsync"/>, which the AI
/// Assistant controller calls exclusively from the explicit user-confirmed /assistant/confirm
/// endpoint. Every task lookup goes through <see cref="ITaskRepository"/>, which is always scoped
/// to <c>ICurrentUserService.UserId</c> (from the JWT) — there is no code path here that can read
/// or write another user's task.
/// </summary>
public class ToolDispatcher
{
    private readonly ISender _mediator;
    private readonly ITaskRepository _tasks;
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;
    private readonly IAIService _ai;

    public ToolDispatcher(
        ISender mediator, ITaskRepository tasks, ICategoryRepository categories,
        ICurrentUserService currentUser, IAIService ai)
    {
        _mediator = mediator;
        _tasks = tasks;
        _categories = categories;
        _currentUser = currentUser;
        _ai = ai;
    }

    public static bool IsKnownTool(string toolName) => AssistantToolCatalog.Find(toolName) is not null;
    public static bool IsMutating(string toolName) => AssistantToolCatalog.Find(toolName)?.IsMutating ?? false;

    // ---------------------------------------------------------------- read-only

    public async Task<ToolExecutionResult> ExecuteReadOnlyAsync(
        string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var spec = AssistantToolCatalog.Find(toolName)
            ?? throw new AIServiceException($"'{toolName}' is not a supported TaskFlow action.");
        if (spec.IsMutating)
        {
            throw new InvalidOperationException($"'{toolName}' is a mutating tool — use BuildPreviewAsync/ApplyAsync.");
        }

        var userId = _currentUser.UserId;
        switch (toolName)
        {
            case "GetTodayTasks":
            {
                var items = await _tasks.GetTodayAsync(userId, ct);
                return Ok(items, $"You have {items.Count} task(s) due today.");
            }
            case "GetOverdueTasks":
            {
                var items = await _tasks.GetOverdueAsync(userId, ct);
                return Ok(items, items.Count == 0 ? "Nothing overdue." : $"{items.Count} task(s) are overdue.");
            }
            case "GetTasks":
            {
                var status = Str(args, "status");
                var page = await _tasks.GetPagedAsync(userId, new TaskQueryParameters { Status = status, PageSize = 50 }, ct);
                return Ok(page.Items, $"Found {page.Items.Count} task(s).");
            }
            case "GetTask":
            {
                var query = Str(args, "titleQuery") ?? "";
                var match = await ResolveTaskAsync(query, ct);
                return match is null
                    ? new ToolExecutionResult(false, $"I couldn't find a task matching \"{query}\".")
                    : Ok(new[] { match }, $"Found: {match.Title}");
            }
            case "SearchTasks":
            {
                var raw = Str(args, "query") ?? "";
                var filter = await _ai.ParseSearchQueryAsync(raw, ct);
                var results = await RunSearchFilterAsync(filter, ct);
                return new ToolExecutionResult(true,
                    results.Count == 0 ? "No matching tasks." : $"Found {results.Count} matching task(s).", results);
            }
            default:
                throw new AIServiceException($"'{toolName}' is not implemented.");
        }

        ToolExecutionResult Ok(IReadOnlyList<TaskEntity> items, string message) =>
            new(true, message, items.Select(ToSummary).ToList());
    }

    public async Task<List<TaskSummaryDto>> RunSearchFilterAsync(SearchFilterDto filter, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var qp = new TaskQueryParameters
        {
            PageSize = 100,
            Status = filter.Status,
            DueDate = filter.Due,
            Search = filter.Search,
        };
        var page = await _tasks.GetPagedAsync(userId, qp, ct);
        IEnumerable<TaskEntity> items = page.Items;
        if (filter.Priority is { Length: > 0 })
        {
            items = items.Where(t => filter.Priority.Contains(t.Priority.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            items = items.Where(t => string.Equals(t.Category?.Name, filter.Category, StringComparison.OrdinalIgnoreCase));
        }
        return items.Select(ToSummary).ToList();
    }

    // ---------------------------------------------------------------- preview (no writes)

    public async Task<ToolExecutionResult> BuildPreviewAsync(
        string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var spec = AssistantToolCatalog.Find(toolName)
            ?? throw new AIServiceException($"'{toolName}' is not a supported TaskFlow action.");
        if (!spec.IsMutating)
        {
            throw new InvalidOperationException($"'{toolName}' is read-only — call ExecuteReadOnlyAsync instead.");
        }

        switch (toolName)
        {
            case "CreateTask":
            {
                var draft = new TaskDraftDto(
                    Str(args, "title")?.Trim() is { Length: > 0 } t ? t : "New task",
                    null,
                    ParseDate(args, "dueDate"),
                    ParseTime(args, "dueTime"),
                    Str(args, "priority"),
                    Str(args, "category"),
                    Array.Empty<string>());
                return new ToolExecutionResult(true, "Here's what I understood — review before I create it.",
                    Preview: new CreateTaskPreview(draft));
            }
            case "RescheduleTask":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null)
                {
                    return new ToolExecutionResult(false, "I couldn't find a task matching that. Try opening it directly instead.");
                }
                var newDate = ParseDate(args, "newDueDate");
                if (newDate is null)
                {
                    return new ToolExecutionResult(false, "I need a target date to reschedule to.");
                }
                var change = new RescheduleChange(task.Id, task.Title, DescribeDate(task.DueDate), newDate.Value.ToString("yyyy-MM-dd"));
                return new ToolExecutionResult(true, "AI wants to update 1 task.",
                    Preview: new RescheduleDiffPreview(new List<RescheduleChange> { change }));
            }
            case "RescheduleBulk":
            {
                var newDate = ParseDate(args, "newDueDate");
                if (newDate is null)
                {
                    return new ToolExecutionResult(false, "I need a target date to reschedule to.");
                }
                var scope = Str(args, "scope") ?? "overdue";
                var targets = scope == "low_priority"
                    ? (await _tasks.GetPagedAsync(_currentUser.UserId, new TaskQueryParameters { PageSize = 200 }, ct))
                        .Items.Where(t => t.Priority == Priority.Low && !t.Completed).ToList()
                    : (await _tasks.GetOverdueAsync(_currentUser.UserId, ct)).ToList();
                if (targets.Count == 0)
                {
                    return new ToolExecutionResult(false, "No matching tasks found.");
                }
                var changes = targets.Select(t => new RescheduleChange(
                    t.Id, t.Title, DescribeDate(t.DueDate), newDate.Value.ToString("yyyy-MM-dd"))).ToList();
                return new ToolExecutionResult(true, $"AI wants to update {changes.Count} task(s).",
                    Preview: new RescheduleDiffPreview(changes));
            }
            case "UpdateTask" or "CompleteTask" or "DeleteTask" or "CreateSubtask" or "CreateReminder":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null)
                {
                    return new ToolExecutionResult(false, "I couldn't find a task matching that.");
                }
                var summary = DescribeGeneric(toolName, task, args);
                return new ToolExecutionResult(true, summary, Preview: new GenericActionPreview(toolName, summary));
            }
            default:
                throw new AIServiceException($"'{toolName}' is not implemented.");
        }
    }

    // ---------------------------------------------------------------- apply (real writes — confirm only)

    public async Task<ToolExecutionResult> ApplyAsync(
        string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var spec = AssistantToolCatalog.Find(toolName)
            ?? throw new AIServiceException($"'{toolName}' is not a supported TaskFlow action.");
        if (!spec.IsMutating)
        {
            throw new InvalidOperationException($"'{toolName}' is read-only — nothing to apply.");
        }

        switch (toolName)
        {
            case "CreateTask":
            {
                var categoryId = await ResolveCategoryIdAsync(Str(args, "category"), ct);
                var req = new CreateTaskRequest(
                    Str(args, "title")?.Trim() is { Length: > 0 } t ? t : "New task",
                    null, ParseDate(args, "dueDate"), ParseTime(args, "dueTime"),
                    ParsePriority(Str(args, "priority")), categoryId, null, null, null, null, null, null);
                var r = await _mediator.Send(new CreateTaskCommand(req), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "Task created successfully with AI.")
                    : new ToolExecutionResult(false, r.Message);
            }
            case "UpdateTask":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null) return NotFound();
                var req = new UpdateTaskRequest(
                    Str(args, "newTitle"), null, null, null, ParsePriority(Str(args, "priority")),
                    null, null, null, null, null, null, null, null);
                var r = await _mediator.Send(new UpdateTaskCommand(task.Id, req), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "Task updated.") : new ToolExecutionResult(false, r.Message);
            }
            case "CompleteTask":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null) return NotFound();
                var r = await _mediator.Send(new CompleteTaskCommand(task.Id), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "Task completed.") : new ToolExecutionResult(false, r.Message);
            }
            case "DeleteTask":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null) return NotFound();
                var r = await _mediator.Send(new DeleteTaskCommand(task.Id), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "Task deleted.") : new ToolExecutionResult(false, r.Message);
            }
            case "CreateSubtask":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null) return NotFound();
                var text = Str(args, "subtaskTitle");
                if (string.IsNullOrWhiteSpace(text)) return new ToolExecutionResult(false, "I need subtask text.");
                var r = await _mediator.Send(new CreateSubtaskCommand(task.Id, new CreateSubtaskRequest(text)), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "Subtask added.") : new ToolExecutionResult(false, r.Message);
            }
            case "CreateReminder":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null) return NotFound();
                if (task.DueDate is null || task.DueTime is null)
                {
                    return new ToolExecutionResult(false, "That task needs a due date and time before I can add a reminder.");
                }
                var offset = ParseInt(args, "offsetMinutes") ?? 30;
                var firesAt = DateTime.SpecifyKind(task.DueDate.Value.ToDateTime(task.DueTime.Value), DateTimeKind.Utc)
                    .AddMinutes(-offset);
                var r = await _mediator.Send(new CreateReminderCommand(new CreateReminderRequest(task.Id, offset, firesAt)), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "Reminder added.") : new ToolExecutionResult(false, r.Message);
            }
            case "RescheduleTask":
            {
                var task = await ResolveTaskAsync(Str(args, "titleQuery") ?? "", ct);
                if (task is null) return NotFound();
                var newDate = ParseDate(args, "newDueDate");
                if (newDate is null) return new ToolExecutionResult(false, "I need a target date.");
                var req = new UpdateTaskRequest(null, null, newDate, null, null, null, null, null, null, null, null, null, null);
                var r = await _mediator.Send(new UpdateTaskCommand(task.Id, req), ct);
                return r.IsSuccess ? new ToolExecutionResult(true, "1 task rescheduled.") : new ToolExecutionResult(false, r.Message);
            }
            case "RescheduleBulk":
            {
                var newDate = ParseDate(args, "newDueDate");
                if (newDate is null) return new ToolExecutionResult(false, "I need a target date.");
                var scope = Str(args, "scope") ?? "overdue";
                var targets = scope == "low_priority"
                    ? (await _tasks.GetPagedAsync(_currentUser.UserId, new TaskQueryParameters { PageSize = 200 }, ct))
                        .Items.Where(t => t.Priority == Priority.Low && !t.Completed).ToList()
                    : (await _tasks.GetOverdueAsync(_currentUser.UserId, ct)).ToList();
                var count = 0;
                foreach (var t in targets)
                {
                    var req = new UpdateTaskRequest(null, null, newDate, null, null, null, null, null, null, null, null, null, null);
                    var r = await _mediator.Send(new UpdateTaskCommand(t.Id, req), ct);
                    if (r.IsSuccess) count++;
                }
                return new ToolExecutionResult(true, $"{count} task(s) rescheduled.");
            }
            default:
                throw new AIServiceException($"'{toolName}' is not implemented.");
        }

        static ToolExecutionResult NotFound() => new(false, "I couldn't find a task matching that.");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<TaskEntity?> ResolveTaskAsync(string titleQuery, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(titleQuery))
        {
            return null;
        }
        var page = await _tasks.SearchAsync(_currentUser.UserId, titleQuery.Trim(), 1, 3, ct);
        return page.Items.FirstOrDefault();
    }

    private async Task<Guid?> ResolveCategoryIdAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var all = await _categories.GetAllAsync(_currentUser.UserId, ct);
        return all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static TaskSummaryDto ToSummary(TaskEntity t) => new(
        t.Id, t.Title, t.Priority.ToString(), t.DueDate, t.DueTime, t.Completed, t.Important,
        t.Category?.Name, t.Subtasks.Count, t.Subtasks.Count(s => s.IsDone), t.RecurrenceRuleId is not null);

    private static string DescribeDate(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "No date";

    private static string DescribeGeneric(string toolName, TaskEntity task, IReadOnlyDictionary<string, object?> args) => toolName switch
    {
        "UpdateTask" => $"Update \"{task.Title}\"" +
            (Str(args, "newTitle") is { } nt ? $" — title → \"{nt}\"" : "") +
            (Str(args, "priority") is { } p ? $", priority → {p}" : ""),
        "CompleteTask" => $"Mark \"{task.Title}\" complete",
        "DeleteTask" => $"Delete \"{task.Title}\" (soft delete — recoverable by an admin)",
        "CreateSubtask" => $"Add subtask \"{Str(args, "subtaskTitle")}\" to \"{task.Title}\"",
        "CreateReminder" => $"Add a reminder to \"{task.Title}\"",
        _ => $"{toolName} on \"{task.Title}\"",
    };

    private static string? Str(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? v?.ToString() is { Length: > 0 } s ? s : null : null;

    private static int? ParseInt(IReadOnlyDictionary<string, object?> args, string key) =>
        int.TryParse(Str(args, key), out var i) ? i : null;

    private static DateOnly? ParseDate(IReadOnlyDictionary<string, object?> args, string key) =>
        DateOnly.TryParse(Str(args, key), out var d) ? d : null;

    private static TimeOnly? ParseTime(IReadOnlyDictionary<string, object?> args, string key) =>
        TimeOnly.TryParse(Str(args, key), out var t) ? t : null;

    private static Priority? ParsePriority(string? value) =>
        Enum.TryParse<Priority>(value, true, out var p) ? p : null;
}
