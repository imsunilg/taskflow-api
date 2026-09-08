using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks;
using TaskManager.Application.Tasks.DTOs;

namespace TaskManager.Application.Calendar;

public record GetCalendarTasksQuery(DateOnly From, DateOnly To) : IRequest<Result<List<TaskResponse>>>;

public class GetCalendarTasksQueryHandler : IRequestHandler<GetCalendarTasksQuery, Result<List<TaskResponse>>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;

    public GetCalendarTasksQueryHandler(ITaskRepository tasks, ICurrentUserService currentUser)
    {
        _tasks = tasks;
        _currentUser = currentUser;
    }

    public async Task<Result<List<TaskResponse>>> Handle(GetCalendarTasksQuery q, CancellationToken ct)
    {
        var items = await _tasks.GetCalendarAsync(_currentUser.UserId, q.From, q.To, ct);
        return Result<List<TaskResponse>>.Success(items.Select(t => t.ToResponse()).ToList());
    }
}
