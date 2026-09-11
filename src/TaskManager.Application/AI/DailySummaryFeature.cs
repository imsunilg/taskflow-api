using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Application.AI;

public record DailySummaryDto(DailyStatsDto Stats, string Insight);

public record GetDailySummaryQuery : IRequest<Result<DailySummaryDto>>, IAiRequest
{
    public string AiRequestType => "daily_summary";
}

public class DailySummaryHandler : IRequestHandler<GetDailySummaryQuery, Result<DailySummaryDto>>
{
    private readonly IAIService _ai;
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;

    public DailySummaryHandler(IAIService ai, ITaskRepository tasks, ICurrentUserService currentUser)
    {
        _ai = ai;
        _tasks = tasks;
        _currentUser = currentUser;
    }

    public async Task<Result<DailySummaryDto>> Handle(GetDailySummaryQuery query, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var all = await _tasks.GetPagedAsync(userId, new TaskQueryParameters { PageSize = 500 }, ct);
        var overdue = await _tasks.GetOverdueAsync(userId, ct);

        var completed = all.Items.Count(t => t.Completed);
        var remaining = all.Items.Count(t => !t.Completed);
        var total = completed + remaining;
        var rate = total == 0 ? 0 : (int)Math.Round(completed * 100.0 / total);
        var highDone = all.Items.Count(t => t.Completed && (t.Priority == Priority.High || t.Priority == Priority.Urgent));

        var stats = new DailyStatsDto(completed, remaining, overdue.Count, rate, highDone);
        var insight = await _ai.SummarizeDayAsync(stats, ct);

        return Result<DailySummaryDto>.Success(new DailySummaryDto(stats, insight));
    }
}
