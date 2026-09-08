namespace TaskManager.Domain.Entities;

public class ReminderEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public int OffsetMinutes { get; set; }
    public DateTime FiresAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public TaskEntity? Task { get; set; }
}
