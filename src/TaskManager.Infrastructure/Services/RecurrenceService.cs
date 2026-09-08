using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Infrastructure.Services;

public class RecurrenceService : IRecurrenceService
{
    public DateOnly? ComputeNextDueDate(RecurrenceRuleEntity rule, DateOnly current)
    {
        var interval = Math.Max(1, rule.Interval);

        DateOnly next = rule.Frequency switch
        {
            RecurrenceFrequency.Daily => current.AddDays(interval),
            RecurrenceFrequency.Weekdays => NextWeekday(current),
            RecurrenceFrequency.Weekly => current.AddDays(7 * interval),
            RecurrenceFrequency.Monthly => AdjustDayOfMonth(current.AddMonths(interval), rule.DayOfMonth),
            RecurrenceFrequency.Yearly => current.AddYears(interval),
            RecurrenceFrequency.Custom => current.AddDays(interval),
            _ => current.AddDays(1),
        };

        if (rule.Until is { } until && next > until)
        {
            return null;
        }

        return next;
    }

    private static DateOnly NextWeekday(DateOnly d)
    {
        var next = d.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }
        return next;
    }

    private static DateOnly AdjustDayOfMonth(DateOnly d, int? dayOfMonth)
    {
        if (dayOfMonth is not { } dom)
        {
            return d;
        }

        var daysInMonth = DateTime.DaysInMonth(d.Year, d.Month);
        return new DateOnly(d.Year, d.Month, Math.Min(dom, daysInMonth));
    }
}
