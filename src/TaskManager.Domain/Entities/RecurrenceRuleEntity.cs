using TaskManager.Domain.Enums;

namespace TaskManager.Domain.Entities;

public class RecurrenceRuleEntity
{
    public Guid Id { get; set; }
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; } = 1;
    public int[]? DaysOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public DateOnly? Until { get; set; }
    public DateTime CreatedAt { get; set; }
}
