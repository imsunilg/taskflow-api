namespace TaskManager.Domain.Entities;

public class TagEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public UserEntity? User { get; set; }
    public ICollection<TaskTagEntity> TaskTags { get; set; } = new List<TaskTagEntity>();
}
