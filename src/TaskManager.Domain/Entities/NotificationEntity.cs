using TaskManager.Domain.Enums;

namespace TaskManager.Domain.Entities;

public class NotificationEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public UserEntity? User { get; set; }
}
