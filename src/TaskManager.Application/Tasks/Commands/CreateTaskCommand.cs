using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Application.Tasks.Commands;

public record CreateTaskCommand(CreateTaskRequest Request) : IRequest<Result<TaskResponse>>;

public class CreateTaskCommandHandler : IRequestHandler<CreateTaskCommand, Result<TaskResponse>>
{
    private readonly ITaskRepository _tasks;
    private readonly ITagRepository _tags;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILookupCache _cache;

    public CreateTaskCommandHandler(
        ITaskRepository tasks,
        ITagRepository tags,
        ICurrentUserService currentUser,
        IUnitOfWork uow,
        ILookupCache cache)
    {
        _tasks = tasks;
        _tags = tags;
        _currentUser = currentUser;
        _uow = uow;
        _cache = cache;
    }

    public async Task<Result<TaskResponse>> Handle(CreateTaskCommand command, CancellationToken ct)
    {
        var r = command.Request;
        var userId = _currentUser.UserId;

        var task = new TaskEntity
        {
            UserId = userId,
            Title = r.Title.Trim(),
            Description = r.Description,
            DueDate = r.DueDate,
            DueTime = r.DueTime,
            Priority = r.Priority ?? Priority.Medium,
            CategoryId = r.CategoryId,
            Notes = r.Notes,
        };

        if (r.SubtaskTexts is { Length: > 0 })
        {
            var i = 0;
            foreach (var text in r.SubtaskTexts.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                task.Subtasks.Add(new SubtaskEntity { Text = text.Trim(), SortOrder = i++ });
            }
        }

        if (r.TagNames is { Length: > 0 })
        {
            await _cache.RemoveAsync(ILookupCache.TagsKey(userId), ct);
            var tags = await _tags.GetOrCreateByNamesAsync(userId, r.TagNames, ct);
            foreach (var tag in tags)
            {
                task.TaskTags.Add(new TaskTagEntity { TagId = tag.Id });
            }
        }

        if (r.ReminderOffsetMinutes is { } offset && r.DueDate is { } dd && r.DueTime is { } dt)
        {
            var firesAt = DateTime.SpecifyKind(dd.ToDateTime(dt), DateTimeKind.Utc).AddMinutes(-offset);
            task.Reminder = new ReminderEntity { OffsetMinutes = offset, FiresAt = firesAt };
        }

        if (r.RecurrenceFrequency is { } freq)
        {
            task.RecurrenceRule = new RecurrenceRuleEntity
            {
                Frequency = freq,
                Interval = r.RecurrenceInterval ?? 1,
            };
        }

        task.History.Add(new TaskHistoryEntity
        {
            ChangeText = "Created",
            ChangedAt = DateTime.UtcNow,
            ChangedBy = userId,
        });

        await _tasks.AddAsync(task, ct);
        await _uow.CommitAsync(ct);

        var saved = await _tasks.GetByIdAsync(userId, task.Id, ct);
        return Result<TaskResponse>.Success(saved!.ToResponse(), "Task created successfully");
    }
}
