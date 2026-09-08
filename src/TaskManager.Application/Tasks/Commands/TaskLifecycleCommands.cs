using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Tasks.Commands;

public record DeleteTaskCommand(Guid TaskId) : IRequest<Result<bool>>;

public class DeleteTaskCommandHandler : IRequestHandler<DeleteTaskCommand, Result<bool>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public DeleteTaskCommandHandler(ITaskRepository tasks, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _tasks = tasks;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<bool>> Handle(DeleteTaskCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var task = await _tasks.GetByIdAsync(userId, command.TaskId, ct)
                   ?? throw new NotFoundException("Task", command.TaskId);

        task.SoftDelete();
        task.History.Add(new TaskHistoryEntity
        {
            TaskId = task.Id,
            ChangeText = "Deleted",
            ChangedAt = DateTime.UtcNow,
            ChangedBy = userId,
        });

        await _uow.CommitAsync(ct);
        return Result<bool>.Success(true, "Task deleted successfully");
    }
}

public record CompleteTaskCommand(Guid TaskId) : IRequest<Result<TaskResponse>>;

public class CompleteTaskCommandHandler : IRequestHandler<CompleteTaskCommand, Result<TaskResponse>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IRecurrenceService _recurrence;
    private readonly IUnitOfWork _uow;

    public CompleteTaskCommandHandler(
        ITaskRepository tasks,
        ICurrentUserService currentUser,
        IRecurrenceService recurrence,
        IUnitOfWork uow)
    {
        _tasks = tasks;
        _currentUser = currentUser;
        _recurrence = recurrence;
        _uow = uow;
    }

    public async Task<Result<TaskResponse>> Handle(CompleteTaskCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var task = await _tasks.GetByIdAsync(userId, command.TaskId, ct)
                   ?? throw new NotFoundException("Task", command.TaskId);

        task.MarkComplete(userId);
        task.History.Add(new TaskHistoryEntity
        {
            TaskId = task.Id,
            ChangeText = "Marked complete",
            ChangedAt = DateTime.UtcNow,
            ChangedBy = userId,
        });

        if (task.RecurrenceRule is { } rule && task.DueDate is { } due)
        {
            var next = _recurrence.ComputeNextDueDate(rule, due);
            if (next is { } nextDate && (rule.Until is null || nextDate <= rule.Until))
            {
                var clone = new TaskEntity
                {
                    UserId = userId,
                    Title = task.Title,
                    Description = task.Description,
                    DueDate = nextDate,
                    DueTime = task.DueTime,
                    Priority = task.Priority,
                    CategoryId = task.CategoryId,
                    Notes = task.Notes,
                    Important = task.Important,
                    RecurrenceRuleId = task.RecurrenceRuleId,
                };
                clone.History.Add(new TaskHistoryEntity
                {
                    ChangeText = "Created from recurrence",
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = userId,
                });
                if (task.Reminder is { } rem)
                {
                    clone.Reminder = new ReminderEntity
                    {
                        OffsetMinutes = rem.OffsetMinutes,
                        FiresAt = DateTime.SpecifyKind(
                            nextDate.ToDateTime(task.DueTime ?? new TimeOnly(9, 0)), DateTimeKind.Utc)
                            .AddMinutes(-rem.OffsetMinutes),
                    };
                }
                await _tasks.AddAsync(clone, ct);
            }
        }

        await _uow.CommitAsync(ct);

        var saved = await _tasks.GetByIdAsync(userId, task.Id, ct);
        return Result<TaskResponse>.Success(saved!.ToResponse(), "Task completed");
    }
}

public record ReopenTaskCommand(Guid TaskId) : IRequest<Result<TaskResponse>>;

public class ReopenTaskCommandHandler : IRequestHandler<ReopenTaskCommand, Result<TaskResponse>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public ReopenTaskCommandHandler(ITaskRepository tasks, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _tasks = tasks;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<TaskResponse>> Handle(ReopenTaskCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var task = await _tasks.GetByIdAsync(userId, command.TaskId, ct)
                   ?? throw new NotFoundException("Task", command.TaskId);

        task.Reopen();
        task.History.Add(new TaskHistoryEntity
        {
            TaskId = task.Id,
            ChangeText = "Reopened",
            ChangedAt = DateTime.UtcNow,
            ChangedBy = userId,
        });

        await _uow.CommitAsync(ct);

        var saved = await _tasks.GetByIdAsync(userId, task.Id, ct);
        return Result<TaskResponse>.Success(saved!.ToResponse(), "Task reopened");
    }
}
