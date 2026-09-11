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
/// Recurring (20:00 daily) job: generates an AI end-of-day summary for every active user and
/// delivers it as a notification. Same fan-out/throttle/per-user-failure-isolation pattern as
/// <see cref="AiMorningPlanJob"/>.
/// </summary>
public class AiEveningSummaryJob
{
    private readonly IUserRepository _users;
    private readonly ITaskRepository _tasks;
    private readonly IAIService _ai;
    private readonly INotificationRepository _notifications;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<AiEveningSummaryJob> _logger;

    public AiEveningSummaryJob(
        IUserRepository users, ITaskRepository tasks, IAIService ai,
        INotificationRepository notifications, INotificationDispatcher dispatcher, IUnitOfWork uow,
        ILogger<AiEveningSummaryJob> logger)
    {
        _users = users;
        _tasks = tasks;
        _ai = ai;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _uow = uow;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var userIds = await _users.GetActiveUserIdsAsync(ct);

        foreach (var userId in userIds)
        {
            try
            {
                var all = await _tasks.GetPagedAsync(userId, new TaskQueryParameters { PageSize = 500 }, ct);
                var overdue = await _tasks.GetOverdueAsync(userId, ct);
                var completed = all.Items.Count(t => t.Completed);
                var remaining = all.Items.Count(t => !t.Completed);
                var total = completed + remaining;
                if (total == 0)
                {
                    continue; // nothing to summarize for a user with no tasks yet
                }
                var rate = (int)Math.Round(completed * 100.0 / total);
                var highDone = all.Items.Count(t => t.Completed && (t.Priority == Priority.High || t.Priority == Priority.Urgent));

                var stats = new DailyStatsDto(completed, remaining, overdue.Count, rate, highDone);
                var insight = await _ai.SummarizeDayAsync(stats, ct);

                var notification = new NotificationEntity
                {
                    UserId = userId,
                    Type = NotificationType.Reminder,
                    Title = $"Your AI daily summary: {rate}% complete — {insight}",
                };
                await _notifications.AddAsync(notification, ct);
                await _uow.CommitAsync(ct);
                await _dispatcher.DispatchAsync(notification, ct);

                _logger.LogInformation("AI evening summary generated for user {UserId}: {Rate}% complete", userId, rate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI evening summary failed for user {UserId} — skipping", userId);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }
    }
}
