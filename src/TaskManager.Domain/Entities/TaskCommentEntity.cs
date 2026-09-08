namespace TaskManager.Domain.Entities;

public class TaskCommentEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }

    public TaskEntity? Task { get; set; }
}
