using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Notifications;
using TaskManager.Domain.Entities;

namespace TaskManager.Api.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? Context.User?.FindFirstValue("sub");
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
        }
        await base.OnConnectedAsync();
    }
}

public class SignalRNotificationDispatcher : INotificationDispatcher
{
    private readonly IHubContext<NotificationHub> _hub;

    public SignalRNotificationDispatcher(IHubContext<NotificationHub> hub) => _hub = hub;

    public Task DispatchAsync(NotificationEntity notification, CancellationToken ct)
    {
        var payload = notification.ToResponse();
        return _hub.Clients.Group($"user-{notification.UserId}")
            .SendAsync("ReceiveNotification", payload, ct);
    }
}
