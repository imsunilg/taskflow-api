using System.Text.Json;
using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.AI;

public record GenerateDailyPlanCommand : IRequest<Result<DailyPlanDto>>, IAiRequest
{
    public string AiRequestType => "daily_plan";
}

public record GetNextTaskQuery(List<Guid>? Dismissed) : IRequest<Result<NextTaskRecommendationDto?>>, IAiRequest
{
    public string AiRequestType => "next_task";
}

public class DailyPlanHandlers :
    IRequestHandler<GenerateDailyPlanCommand, Result<DailyPlanDto>>,
    IRequestHandler<GetNextTaskQuery, Result<NextTaskRecommendationDto?>>
{
    // Fixed default until per-user working-hours preferences are added to UserEntity.
    private static readonly WorkingHoursDto DefaultWorkingHours = new(new TimeOnly(9, 0), new TimeOnly(18, 0));

    private readonly IAIService _ai;
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IAiRepository _aiRepo;
    private readonly IUnitOfWork _uow;

    public DailyPlanHandlers(
        IAIService ai, ITaskRepository tasks, ICurrentUserService currentUser, IAiRepository aiRepo, IUnitOfWork uow)
    {
        _ai = ai;
        _tasks = tasks;
        _currentUser = currentUser;
        _aiRepo = aiRepo;
        _uow = uow;
    }

    public async Task<Result<DailyPlanDto>> Handle(GenerateDailyPlanCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var today = await _tasks.GetTodayAsync(userId, ct);
        var pool = today.Count > 0 ? today : (await Overflow(userId, ct));

        var summaries = pool.Select(ToSummary).ToList();
        var plan = await _ai.GenerateDailyPlanAsync(summaries, DefaultWorkingHours, DateOnly.FromDateTime(DateTime.UtcNow), ct);

        await _aiRepo.UpsertDailyPlanAsync(new AiDailyPlanEntity
        {
            UserId = userId,
            PlanDate = plan.Date,
            Summary = $"{plan.Blocks.Count} block(s) planned",
            BlocksJson = JsonSerializer.Serialize(plan.Blocks),
        }, ct);
        await _uow.CommitAsync(ct);

        return Result<DailyPlanDto>.Success(plan);
    }

    /// <summary>When nothing is due today, fall back to the highest-priority open tasks so the plan isn't empty.</summary>
    private async Task<IReadOnlyList<TaskEntity>> Overflow(Guid userId, CancellationToken ct)
    {
        var page = await _tasks.GetPagedAsync(userId, new TaskQueryParameters { Status = "active", Sort = "priority", PageSize = 6 }, ct);
        return page.Items;
    }

    public async Task<Result<NextTaskRecommendationDto?>> Handle(GetNextTaskQuery query, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var page = await _tasks.GetPagedAsync(userId, new TaskQueryParameters { Status = "active", PageSize = 100 }, ct);
        var dismissed = query.Dismissed ?? new List<Guid>();
        var candidates = page.Items.Where(t => !dismissed.Contains(t.Id)).Select(ToSummary).ToList();

        var rec = await _ai.RecommendNextTaskAsync(candidates, ct);
        return Result<NextTaskRecommendationDto?>.Success(rec);
    }

    private static TaskSummaryDto ToSummary(TaskEntity t) => new(
        t.Id, t.Title, t.Priority.ToString(), t.DueDate, t.DueTime, t.Completed, t.Important,
        t.Category?.Name, t.Subtasks.Count, t.Subtasks.Count(s => s.IsDone), t.RecurrenceRuleId is not null);
}
