using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks;
using TaskManager.Application.Tasks.DTOs;

namespace TaskManager.Application.Search;

public record SearchTasksQuery(string Query, int Page = 1, int PageSize = 25)
    : IRequest<Result<PagedResult<TaskResponse>>>;

public class SearchTasksQueryHandler : IRequestHandler<SearchTasksQuery, Result<PagedResult<TaskResponse>>>
{
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;

    public SearchTasksQueryHandler(ITaskRepository tasks, ICurrentUserService currentUser)
    {
        _tasks = tasks;
        _currentUser = currentUser;
    }

    public async Task<Result<PagedResult<TaskResponse>>> Handle(SearchTasksQuery q, CancellationToken ct)
    {
        var term = (q.Query ?? string.Empty).Trim();
        if (term.Length < 2)
        {
            return Result<PagedResult<TaskResponse>>.Success(
                new PagedResult<TaskResponse>(Array.Empty<TaskResponse>(), 1, q.PageSize, 0));
        }

        var paged = await _tasks.SearchAsync(_currentUser.UserId, term,
            Math.Max(1, q.Page), Math.Clamp(q.PageSize, 1, 100), ct);

        var mapped = new PagedResult<TaskResponse>(
            paged.Items.Select(t => t.ToResponse()).ToList(), paged.Page, paged.PageSize, paged.Total);
        return Result<PagedResult<TaskResponse>>.Success(mapped);
    }
}
