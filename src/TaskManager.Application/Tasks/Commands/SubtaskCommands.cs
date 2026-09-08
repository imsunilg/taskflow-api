using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Tasks.Commands;

public record CreateSubtaskCommand(Guid TaskId, CreateSubtaskRequest Request) : IRequest<Result<SubtaskResponse>>;
public record UpdateSubtaskCommand(Guid TaskId, Guid SubtaskId, UpdateSubtaskRequest Request) : IRequest<Result<SubtaskResponse>>;
public record DeleteSubtaskCommand(Guid TaskId, Guid SubtaskId) : IRequest<Result<bool>>;

public class SubtaskCommandHandler :
    IRequestHandler<CreateSubtaskCommand, Result<SubtaskResponse>>,
    IRequestHandler<UpdateSubtaskCommand, Result<SubtaskResponse>>,
    IRequestHandler<DeleteSubtaskCommand, Result<bool>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public SubtaskCommandHandler(ITaskRepository tasks, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _tasks = tasks;
        _currentUser = currentUser;
        _uow = uow;
    }

    private async Task<TaskEntity> LoadAsync(Guid taskId, CancellationToken ct)
    {
        return await _tasks.GetByIdAsync(_currentUser.UserId, taskId, ct)
               ?? throw new NotFoundException("Task", taskId);
    }

    public async Task<Result<SubtaskResponse>> Handle(CreateSubtaskCommand c, CancellationToken ct)
    {
        var task = await LoadAsync(c.TaskId, ct);
        var order = task.Subtasks.Count == 0 ? 0 : task.Subtasks.Max(s => s.SortOrder) + 1;
        var subtask = new SubtaskEntity { TaskId = task.Id, Text = c.Request.Text.Trim(), SortOrder = order };
        task.Subtasks.Add(subtask);
        await _uow.CommitAsync(ct);
        return Result<SubtaskResponse>.Success(
            new SubtaskResponse(subtask.Id, task.Id, subtask.Text, subtask.IsDone, subtask.SortOrder));
    }

    public async Task<Result<SubtaskResponse>> Handle(UpdateSubtaskCommand c, CancellationToken ct)
    {
        var task = await LoadAsync(c.TaskId, ct);
        var subtask = task.Subtasks.FirstOrDefault(s => s.Id == c.SubtaskId)
                      ?? throw new NotFoundException("Subtask", c.SubtaskId);

        if (c.Request.Text is not null) subtask.Text = c.Request.Text.Trim();
        if (c.Request.IsDone.HasValue) subtask.IsDone = c.Request.IsDone.Value;
        if (c.Request.SortOrder.HasValue) subtask.SortOrder = c.Request.SortOrder.Value;

        await _uow.CommitAsync(ct);
        return Result<SubtaskResponse>.Success(
            new SubtaskResponse(subtask.Id, task.Id, subtask.Text, subtask.IsDone, subtask.SortOrder));
    }

    public async Task<Result<bool>> Handle(DeleteSubtaskCommand c, CancellationToken ct)
    {
        var task = await LoadAsync(c.TaskId, ct);
        var subtask = task.Subtasks.FirstOrDefault(s => s.Id == c.SubtaskId)
                      ?? throw new NotFoundException("Subtask", c.SubtaskId);
        task.Subtasks.Remove(subtask);
        await _uow.CommitAsync(ct);
        return Result<bool>.Success(true, "Subtask deleted");
    }
}
