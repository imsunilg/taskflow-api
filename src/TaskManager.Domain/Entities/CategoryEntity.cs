using TaskManager.Domain.Common;

namespace TaskManager.Domain.Entities;

public class CategoryEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#3b82f6";
    public string? Icon { get; set; }

    public UserEntity? User { get; set; }
    public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
}
