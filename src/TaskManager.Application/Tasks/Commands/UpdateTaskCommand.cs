using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Tasks.Commands;

public record UpdateTaskCommand(Guid TaskId, UpdateTaskRequest Request) : IRequest<Result<TaskResponse>>;

public class UpdateTaskCommandHandler : IRequestHandler<UpdateTaskCommand, Result<TaskResponse>>
{
    private readonly ITaskRepository _tasks;
    private readonly ITagRepository _tags;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public UpdateTaskCommandHandler(
        ITaskRepository tasks,
        ITagRepository tags,
        ICurrentUserService currentUser,
        IUnitOfWork uow)
    {
        _tasks = tasks;
        _tags = tags;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<TaskResponse>> Handle(UpdateTaskCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var task = await _tasks.GetByIdAsync(userId, command.TaskId, ct)
                   ?? throw new NotFoundException("Task", command.TaskId);

        var r = command.Request;
        var changes = new List<string>();

        if (r.Title is not null && r.Title != task.Title)
        {
            changes.Add($"Title changed to \"{r.Title}\"");
            task.Title = r.Title.Trim();
        }

        if (r.Description is not null && r.Description != task.Description)
        {
            changes.Add("Description updated");
            task.Description = r.Description;
        }

        if (r.DueDate.HasValue && r.DueDate != task.DueDate)
        {
            changes.Add($"Due date changed to {r.DueDate:yyyy-MM-dd}");
            task.DueDate = r.DueDate;
        }

        if (r.DueTime.HasValue && r.DueTime != task.DueTime)
        {
            changes.Add($"Due time changed to {r.DueTime}");
            task.DueTime = r.DueTime;
        }

        if (r.Priority.HasValue && r.Priority != task.Priority)
        {
            changes.Add($"Priority changed to {r.Priority}");
            task.Priority = r.Priority.Value;
        }

        if (r.CategoryId.HasValue && r.CategoryId != task.CategoryId)
        {
            changes.Add("Category changed");
            task.CategoryId = r.CategoryId;
        }

        if (r.Notes is not null && r.Notes != task.Notes)
        {
            changes.Add("Notes updated");
            task.Notes = r.Notes;
        }

        if (r.Important.HasValue && r.Important != task.Important)
        {
            changes.Add(r.Important.Value ? "Marked important" : "Unmarked important");
            task.Important = r.Important.Value;
        }

        if (r.Completed.HasValue && r.Completed != task.Completed)
        {
            if (r.Completed.Value)
            {
                task.MarkComplete(userId);
                changes.Add("Marked complete");
            }
            else
            {
                task.Reopen();
                changes.Add("Reopened");
            }
        }

        if (r.TagNames is not null)
        {
            task.TaskTags.Clear();
            var tags = await _tags.GetOrCreateByNamesAsync(userId, r.TagNames, ct);
            foreach (var tag in tags)
            {
                task.TaskTags.Add(new TaskTagEntity { TaskId = task.Id, TagId = tag.Id });
            }
            changes.Add("Tags updated");
        }

        foreach (var change in changes)
        {
            task.History.Add(new TaskHistoryEntity
            {
                TaskId = task.Id,
                ChangeText = change,
                ChangedAt = DateTime.UtcNow,
                ChangedBy = userId,
            });
        }

        await _uow.CommitAsync(ct);

        var saved = await _tasks.GetByIdAsync(userId, task.Id, ct);
        return Result<TaskResponse>.Success(saved!.ToResponse(), "Task updated successfully");
    }
}
