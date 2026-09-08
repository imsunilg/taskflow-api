using TaskManager.Domain.Common;

namespace TaskManager.Domain.Events;

public record TaskCompletedEvent(Guid TaskId, Guid RecurrenceRuleId, Guid ActorId) : IDomainEvent;
