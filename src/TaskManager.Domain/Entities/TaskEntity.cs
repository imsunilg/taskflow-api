using TaskManager.Domain.Common;
using TaskManager.Domain.Enums;
using TaskManager.Domain.Events;

namespace TaskManager.Domain.Entities;

public class TaskEntity : UserOwnedAuditableEntity
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public Guid? CategoryId { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Important { get; set; }
    public string? Notes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? RecurrenceRuleId { get; set; }
    public bool IsVoiceCreated { get; set; }

    public UserEntity? User { get; set; }
    public CategoryEntity? Category { get; set; }
    public RecurrenceRuleEntity? RecurrenceRule { get; set; }
    public ReminderEntity? Reminder { get; set; }
    public ICollection<SubtaskEntity> Subtasks { get; set; } = new List<SubtaskEntity>();
    public ICollection<TaskTagEntity> TaskTags { get; set; } = new List<TaskTagEntity>();
    public ICollection<TaskHistoryEntity> History { get; set; } = new List<TaskHistoryEntity>();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();
    private void Raise(IDomainEvent e) => _domainEvents.Add(e);

    /// <summary>Domain invariant: a task cannot be completed twice.</summary>
    public void MarkComplete(Guid actorId)
    {
        if (Completed)
        {
            throw new DomainException("Task is already completed.");
        }

        Completed = true;
        CompletedAt = DateTime.UtcNow;

        if (RecurrenceRuleId is not null)
        {
            Raise(new TaskCompletedEvent(Id, RecurrenceRuleId.Value, actorId));
        }
    }

    public void Reopen()
    {
        Completed = false;
        CompletedAt = null;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
    }
}
