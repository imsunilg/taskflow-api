namespace TaskManager.Domain.Common;

public abstract class AuditableEntity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public abstract class UserOwnedAuditableEntity : AuditableEntity
{
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
}
