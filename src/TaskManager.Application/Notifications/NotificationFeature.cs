using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Notifications;

public record NotificationResponse(Guid Id, Guid UserId, string Type, string Title, DateTime? ReadAt, DateTime CreatedAt);

public static class NotificationMapper
{
    public static NotificationResponse ToResponse(this NotificationEntity e) =>
        new(e.Id, e.UserId, e.Type.ToString(), e.Title, e.ReadAt, e.CreatedAt);
}

public record GetNotificationsQuery(int Page, int PageSize) : IRequest<Result<PagedResult<NotificationResponse>>>;
public record MarkNotificationReadCommand(Guid Id) : IRequest<Result<bool>>;
public record MarkAllNotificationsReadCommand : IRequest<Result<bool>>;

public class NotificationHandlers :
    IRequestHandler<GetNotificationsQuery, Result<PagedResult<NotificationResponse>>>,
    IRequestHandler<MarkNotificationReadCommand, Result<bool>>,
    IRequestHandler<MarkAllNotificationsReadCommand, Result<bool>>
{
    private readonly INotificationRepository _repo;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public NotificationHandlers(INotificationRepository repo, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _repo = repo;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<PagedResult<NotificationResponse>>> Handle(GetNotificationsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize <= 0 ? 25 : q.PageSize, 1, 100);
        var paged = await _repo.GetPagedAsync(_currentUser.UserId, page, size, ct);
        var mapped = new PagedResult<NotificationResponse>(
            paged.Items.Select(n => n.ToResponse()).ToList(), paged.Page, paged.PageSize, paged.Total);
        return Result<PagedResult<NotificationResponse>>.Success(mapped);
    }

    public async Task<Result<bool>> Handle(MarkNotificationReadCommand c, CancellationToken ct)
    {
        var entity = await _repo.GetByIdAsync(_currentUser.UserId, c.Id, ct)
                     ?? throw new NotFoundException("Notification", c.Id);
        entity.ReadAt ??= DateTime.UtcNow;
        await _uow.CommitAsync(ct);
        return Result<bool>.Success(true, "Marked read");
    }

    public async Task<Result<bool>> Handle(MarkAllNotificationsReadCommand c, CancellationToken ct)
    {
        var unread = await _repo.GetUnreadAsync(_currentUser.UserId, ct);
        foreach (var n in unread)
        {
            n.ReadAt = DateTime.UtcNow;
        }
        await _uow.CommitAsync(ct);
        return Result<bool>.Success(true, "All marked read");
    }
}
