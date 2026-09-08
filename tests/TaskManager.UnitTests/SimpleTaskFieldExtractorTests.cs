using FluentAssertions;
using TaskManager.Domain.Enums;
using TaskManager.Infrastructure.NLP;
using Xunit;

namespace TaskManager.UnitTests;

public class SimpleTaskFieldExtractorTests
{
    private readonly SimpleTaskFieldExtractor _sut = new();

    [Fact]
    public void Extracts_priority_from_urgent_keyword()
    {
        _sut.Extract("call the client asap").Priority.Should().Be(Priority.Urgent);
    }

    [Fact]
    public void Extracts_tomorrow_as_a_relative_date()
    {
        var result = _sut.Extract("submit the report tomorrow");
        result.DueDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));
    }

    [Fact]
    public void Extracts_explicit_time()
    {
        _sut.Extract("meeting at 3:30 pm").DueTime.Should().Be(new TimeOnly(15, 30));
    }

    [Fact]
    public void Extracts_reminder_offset()
    {
        _sut.Extract("do the thing, remind me an hour before").ReminderOffsetMinutes.Should().Be(60);
    }

    [Fact]
    public void Returns_nulls_when_nothing_is_present()
    {
        var result = _sut.Extract("just a plain task");
        result.DueDate.Should().BeNull();
        result.DueTime.Should().BeNull();
        result.Priority.Should().BeNull();
        result.ReminderOffsetMinutes.Should().BeNull();
        result.Title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Builds_a_clean_title_without_the_date_phrase()
    {
        _sut.Extract("buy milk tomorrow").Title.Should().Be("Buy milk");
    }

    [Fact]
    public void Recognizes_in_n_days()
    {
        _sut.Extract("pay rent in 3 days").DueDate
            .Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3));
    }

    [Fact]
    public void Recognizes_next_week()
    {
        _sut.Extract("plan the offsite next week").DueDate
            .Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7));
    }

    [Fact]
    public void Recognizes_end_of_month()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var last = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        _sut.Extract("close the books end of month").DueDate.Should().Be(last);
    }

    [Fact]
    public void Recognizes_by_noon()
    {
        _sut.Extract("ship the build by noon").DueTime.Should().Be(new TimeOnly(12, 0));
    }

    [Fact]
    public void Recognizes_low_priority_phrase()
    {
        _sut.Extract("tidy the desk, low priority").Priority.Should().Be(Priority.Low);
    }

    [Fact]
    public void Recognizes_day_before_reminder()
    {
        _sut.Extract("renew passport, remind me the day before").ReminderOffsetMinutes.Should().Be(1440);
    }

    [Fact]
    public void Quarterly_report_example_extracts_clean_fields()
    {
        var r = _sut.Extract("Remind me to submit the quarterly report tomorrow at 3 PM, high priority.");

        r.Title.Should().Be("Submit the quarterly report");
        r.Title.Should().NotContainEquivalentOf("priority");
        r.Title.Should().NotContainEquivalentOf("remind me");
        r.Priority.Should().Be(Priority.High);
        r.DueDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));
        r.DueTime.Should().Be(new TimeOnly(15, 0));
        r.ReminderOffsetMinutes.Should().BeNull();
    }

    [Fact]
    public void Only_priority_present_leaves_date_null()
    {
        var r = _sut.Extract("urgent thing");
        r.Priority.Should().Be(Priority.Urgent);
        r.DueDate.Should().BeNull();
    }
}
