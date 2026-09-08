namespace TaskManager.Domain.Entities;

public class SubtaskEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }

    public TaskEntity? Task { get; set; }
}
