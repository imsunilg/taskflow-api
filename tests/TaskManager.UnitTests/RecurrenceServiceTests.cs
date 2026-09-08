using FluentAssertions;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;
using TaskManager.Infrastructure.Services;
using Xunit;

namespace TaskManager.UnitTests;

public class RecurrenceServiceTests
{
    private readonly RecurrenceService _sut = new();

    [Theory]
    [InlineData(RecurrenceFrequency.Daily, 1, "2026-01-01", "2026-01-02")]
    [InlineData(RecurrenceFrequency.Weekly, 1, "2026-01-01", "2026-01-08")]
    [InlineData(RecurrenceFrequency.Yearly, 1, "2026-01-01", "2027-01-01")]
    [InlineData(RecurrenceFrequency.Custom, 3, "2026-01-01", "2026-01-04")]
    public void ComputeNextDueDate_advances_by_frequency(
        RecurrenceFrequency freq, int interval, string from, string expected)
    {
        var rule = new RecurrenceRuleEntity { Frequency = freq, Interval = interval };

        var next = _sut.ComputeNextDueDate(rule, DateOnly.Parse(from));

        next.Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void Monthly_keeps_the_day_of_month()
    {
        var rule = new RecurrenceRuleEntity
        {
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            DayOfMonth = 15,
        };

        _sut.ComputeNextDueDate(rule, new DateOnly(2026, 1, 15))
            .Should().Be(new DateOnly(2026, 2, 15));
    }

    [Fact]
    public void Weekdays_skips_the_weekend()
    {
        var rule = new RecurrenceRuleEntity { Frequency = RecurrenceFrequency.Weekdays };

        // 2026-01-02 is a Friday -> next weekday is Monday 2026-01-05
        var next = _sut.ComputeNextDueDate(rule, new DateOnly(2026, 1, 2));

        next.Should().Be(new DateOnly(2026, 1, 5));
    }

    [Fact]
    public void Returns_null_past_the_until_date()
    {
        var rule = new RecurrenceRuleEntity
        {
            Frequency = RecurrenceFrequency.Monthly,
            Until = new DateOnly(2026, 1, 15),
        };

        _sut.ComputeNextDueDate(rule, new DateOnly(2026, 1, 1)).Should().BeNull();
    }
}
