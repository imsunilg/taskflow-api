namespace TaskManager.Domain.Entities;

public class TaskHistoryEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string ChangeText { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; }
    public Guid ChangedBy { get; set; }

    public TaskEntity? Task { get; set; }
}
