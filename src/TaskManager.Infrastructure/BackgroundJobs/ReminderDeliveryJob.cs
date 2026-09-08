using Microsoft.Extensions.Logging;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Infrastructure.BackgroundJobs;

public class ReminderDeliveryJob
{
    private readonly IReminderRepository _reminders;
    private readonly INotificationRepository _notifications;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ReminderDeliveryJob> _logger;

    public ReminderDeliveryJob(
        IReminderRepository reminders,
        INotificationRepository notifications,
        INotificationDispatcher dispatcher,
        IUnitOfWork uow,
        ILogger<ReminderDeliveryJob> logger)
    {
        _reminders = reminders;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _uow = uow;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var pending = await _reminders.GetPendingAsync(DateTime.UtcNow, ct);
        if (pending.Count == 0) return;

        foreach (var reminder in pending)
        {
            try
            {
                var task = reminder.Task;
                if (task is null) continue;

                var notification = new NotificationEntity
                {
                    UserId = task.UserId,
                    Type = NotificationType.Reminder,
                    Title = $"Reminder: {task.Title}",
                };
                await _notifications.AddAsync(notification, ct);
                _reminders.MarkDelivered(reminder);
                await _uow.CommitAsync(ct);

                await _dispatcher.DispatchAsync(notification, ct);
                _logger.LogInformation("Delivered reminder {ReminderId} for task {TaskId}", reminder.Id, task.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver reminder {ReminderId}", reminder.Id);
            }
        }
    }
}
