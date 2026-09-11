using System.Text.Json;
using Microsoft.Extensions.Logging;
using TaskManager.Application.AI;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Infrastructure.BackgroundJobs;

/// <summary>
/// Recurring (07:00 daily) job: generates and stores a fresh AI daily plan for every active user,
/// with a short delay between users so Gemini is never bursted. One user's Gemini/DB failure is
/// logged and skipped — it never aborts the run for the rest of the users. Runs outside any HTTP
/// request, so it talks to repositories directly rather than through the per-request MediatR
/// pipeline (which depends on the current-request user).
/// </summary>
public class AiMorningPlanJob
{
    private static readonly WorkingHoursDto DefaultWorkingHours = new(new TimeOnly(9, 0), new TimeOnly(18, 0));

    private readonly IUserRepository _users;
    private readonly ITaskRepository _tasks;
    private readonly IAIService _ai;
    private readonly IAiRepository _aiRepo;
    private readonly INotificationRepository _notifications;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<AiMorningPlanJob> _logger;

    public AiMorningPlanJob(
        IUserRepository users, ITaskRepository tasks, IAIService ai, IAiRepository aiRepo,
        INotificationRepository notifications, INotificationDispatcher dispatcher, IUnitOfWork uow,
        ILogger<AiMorningPlanJob> logger)
    {
        _users = users;
        _tasks = tasks;
        _ai = ai;
        _aiRepo = aiRepo;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _uow = uow;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var userIds = await _users.GetActiveUserIdsAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var userId in userIds)
        {
            try
            {
                var todaysTasks = await _tasks.GetTodayAsync(userId, ct);
                var pool = todaysTasks.Count > 0
                    ? todaysTasks
                    : (await _tasks.GetPagedAsync(userId, new TaskQueryParameters { Status = "active", Sort = "priority", PageSize = 6 }, ct)).Items;

                if (pool.Count == 0)
                {
                    continue; // nothing to plan today
                }

                var summaries = pool.Select(ToSummary).ToList();
                var plan = await _ai.GenerateDailyPlanAsync(summaries, DefaultWorkingHours, today, ct);
                if (plan.Blocks.Count == 0)
                {
                    continue;
                }

                await _aiRepo.UpsertDailyPlanAsync(new AiDailyPlanEntity
                {
                    UserId = userId,
                    PlanDate = plan.Date,
                    Summary = $"{plan.Blocks.Count} block(s) planned",
                    BlocksJson = JsonSerializer.Serialize(plan.Blocks),
                }, ct);

                var notification = new NotificationEntity
                {
                    UserId = userId,
                    Type = NotificationType.Reminder,
                    Title = $"Your AI daily plan is ready — {plan.Blocks.Count} block(s) scheduled",
                };
                await _notifications.AddAsync(notification, ct);
                await _uow.CommitAsync(ct);
                await _dispatcher.DispatchAsync(notification, ct);

                _logger.LogInformation("AI morning plan generated for user {UserId}: {Blocks} block(s)", userId, plan.Blocks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI morning plan failed for user {UserId} — skipping", userId);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct); // throttle between users
        }
    }

    private static TaskSummaryDto ToSummary(TaskEntity t) => new(
        t.Id, t.Title, t.Priority.ToString(), t.DueDate, t.DueTime, t.Completed, t.Important,
        t.Category?.Name, t.Subtasks.Count, t.Subtasks.Count(s => s.IsDone), t.RecurrenceRuleId is not null);
}
