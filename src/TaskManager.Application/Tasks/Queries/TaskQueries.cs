using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.DTOs;

namespace TaskManager.Application.Tasks.Queries;

public record GetTasksQuery(TaskQueryParameters Parameters) : IRequest<Result<PagedResult<TaskResponse>>>;

public class GetTasksQueryHandler : IRequestHandler<GetTasksQuery, Result<PagedResult<TaskResponse>>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;

    public GetTasksQueryHandler(ITaskRepository tasks, ICurrentUserService currentUser)
    {
        _tasks = tasks;
        _currentUser = currentUser;
    }

    public async Task<Result<PagedResult<TaskResponse>>> Handle(GetTasksQuery query, CancellationToken ct)
    {
        var p = query.Parameters;
        p.Page = Math.Max(1, p.Page);
        p.PageSize = Math.Clamp(p.PageSize, 1, 100);

        var paged = await _tasks.GetPagedAsync(_currentUser.UserId, p, ct);
        var mapped = new PagedResult<TaskResponse>(
            paged.Items.Select(t => t.ToResponse()).ToList(),
            paged.Page,
            paged.PageSize,
            paged.Total);

        return Result<PagedResult<TaskResponse>>.Success(mapped);
    }
}

public record GetTaskByIdQuery(Guid TaskId) : IRequest<Result<TaskResponse>>;

public class GetTaskByIdQueryHandler : IRequestHandler<GetTaskByIdQuery, Result<TaskResponse>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;

    public GetTaskByIdQueryHandler(ITaskRepository tasks, ICurrentUserService currentUser)
    {
        _tasks = tasks;
        _currentUser = currentUser;
    }

    public async Task<Result<TaskResponse>> Handle(GetTaskByIdQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(_currentUser.UserId, query.TaskId, ct)
                   ?? throw new NotFoundException("Task", query.TaskId);
        return Result<TaskResponse>.Success(task.ToResponse());
    }
}
