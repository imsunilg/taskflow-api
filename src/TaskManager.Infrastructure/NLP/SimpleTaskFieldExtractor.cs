using System.Globalization;
using System.Text.RegularExpressions;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Enums;

namespace TaskManager.Infrastructure.NLP;

/// <summary>
/// Heuristic, regex-based extractor. Deliberately returns null for any field it is
/// not confident about so an AI-powered extractor can replace it without changing
/// the <see cref="ITaskFieldExtractor"/> contract.
/// </summary>
public class SimpleTaskFieldExtractor : ITaskFieldExtractor
{
    private static readonly string[] Weekdays =
        { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    public ExtractedTaskFields Extract(string transcript)
    {
        var text = (transcript ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        var priority = ExtractPriority(lower);
        var (date, dateMatch) = ExtractDate(lower);
        var (time, timeMatch) = ExtractTime(lower);
        var reminder = ExtractReminder(lower);
        var title = BuildTitle(text, dateMatch, timeMatch);

        return new ExtractedTaskFields(
            string.IsNullOrWhiteSpace(title) ? null : title,
            date,
            time,
            priority,
            reminder);
    }

    private static Priority? ExtractPriority(string lower)
    {
        if (Regex.IsMatch(lower, @"\b(urgent|asap|as soon as possible|right away)\b")) return Priority.Urgent;
        if (Regex.IsMatch(lower, @"\b(high priority|important|critical)\b")) return Priority.High;
        if (Regex.IsMatch(lower, @"\b(low priority|not urgent|whenever)\b")) return Priority.Low;
        return null;
    }

    private static (DateOnly?, string?) ExtractDate(string lower)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (Regex.IsMatch(lower, @"\btoday\b")) return (today, "today");
        if (Regex.IsMatch(lower, @"\btomorrow\b")) return (today.AddDays(1), "tomorrow");

        var inDays = Regex.Match(lower, @"\bin (\d+) days?\b");
        if (inDays.Success) return (today.AddDays(int.Parse(inDays.Groups[1].Value)), inDays.Value);

        if (Regex.IsMatch(lower, @"\bnext week\b")) return (today.AddDays(7), "next week");
        if (Regex.IsMatch(lower, @"\bend of (the )?month\b"))
        {
            var last = DateTime.DaysInMonth(today.Year, today.Month);
            return (new DateOnly(today.Year, today.Month, last), "end of month");
        }

        var nextWeekday = Regex.Match(lower, @"\bnext (sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b");
        if (nextWeekday.Success)
        {
            return (NextDow(today, Array.IndexOf(Weekdays, nextWeekday.Groups[1].Value), true), nextWeekday.Value);
        }

        var weekday = Regex.Match(lower, @"\b(on )?(sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b");
        if (weekday.Success)
        {
            return (NextDow(today, Array.IndexOf(Weekdays, weekday.Groups[2].Value), false), weekday.Value);
        }

        foreach (var fmt in new[] { "MMMM d", "MMM d", "d MMMM", "d MMM", "M/d/yyyy", "yyyy-MM-dd" })
        {
            var m = Regex.Match(lower, @"\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{2}-\d{2}|[a-z]+ \d{1,2}|\d{1,2} [a-z]+)\b");
            if (m.Success && DateTime.TryParseExact(m.Value, fmt,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                var year = parsed.Year < 2000 ? today.Year : parsed.Year;
                return (new DateOnly(year, parsed.Month, parsed.Day), m.Value);
            }
        }

        return (null, null);
    }

    private static DateOnly NextDow(DateOnly from, int targetDow, bool forceNextWeek)
    {
        if (targetDow < 0) return from;
        var diff = (targetDow - (int)from.DayOfWeek + 7) % 7;
        if (diff == 0 || forceNextWeek) diff += 7;
        return from.AddDays(diff);
    }

    private static (TimeOnly?, string?) ExtractTime(string lower)
    {
        var noon = Regex.Match(lower, @"\b(at |by )?noon\b");
        if (noon.Success) return (new TimeOnly(12, 0), noon.Value);
        var midnight = Regex.Match(lower, @"\b(at |by )?midnight\b");
        if (midnight.Success) return (new TimeOnly(0, 0), midnight.Value);

        var m = Regex.Match(lower, @"\b(?:at|by)\s*(\d{1,2})(?::(\d{2}))?\s*(am|pm)\b");
        if (m.Success)
        {
            var hour = int.Parse(m.Groups[1].Value);
            var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var pm = m.Groups[3].Value == "pm";
            if (pm && hour < 12) hour += 12;
            if (!pm && hour == 12) hour = 0;
            if (hour is >= 0 and < 24 && minute is >= 0 and < 60)
            {
                return (new TimeOnly(hour, minute), m.Value);
            }
        }

        return (null, null);
    }

    private static int? ExtractReminder(string lower)
    {
        if (Regex.IsMatch(lower, @"remind me (an|1) hour before")) return 60;
        if (Regex.IsMatch(lower, @"remind me (a|1) day before|remind me the day before")) return 1440;
        var m = Regex.Match(lower, @"remind me (\d+) (minute|minutes|hour|hours) before");
        if (m.Success)
        {
            var value = int.Parse(m.Groups[1].Value);
            return m.Groups[2].Value.StartsWith("hour") ? value * 60 : value;
        }
        return null;
    }

    private static string BuildTitle(string text, string? dateMatch, string? timeMatch)
    {
        var title = text;
        foreach (var phrase in new[] { dateMatch, timeMatch })
        {
            if (!string.IsNullOrEmpty(phrase))
            {
                title = Regex.Replace(title, Regex.Escape(phrase), string.Empty, RegexOptions.IgnoreCase);
            }
        }

        // Drop a trailing "remind me ..." clause (the reminder is captured separately).
        title = Regex.Replace(title, @"[,;]?\s*\bremind me\b[^.]*$", string.Empty, RegexOptions.IgnoreCase);

        // Strip leading command words.
        title = Regex.Replace(
            title,
            @"^\s*(please\s+|can you\s+|could you\s+|remember to\s+|i need to\s+|i have to\s+|don'?t forget to\s+|remind me to\s+|remind me\s+|make sure (to|i)\s+|set (a\s+)?reminder to\s+)+",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Strip priority phrases anywhere (captured separately, must not pollute the title).
        title = Regex.Replace(
            title,
            @"[,;]?\s*\b(high|low|medium|top|highest)\s+priority\b|[,;]?\s*\b(as soon as possible|asap|urgent(ly)?|right away|important|critical|not urgent|whenever)\b",
            string.Empty,
            RegexOptions.IgnoreCase);

        title = Regex.Replace(title, @"\s{2,}", " ");
        // Trim leftover punctuation / dangling conjunctions from both ends.
        title = Regex.Replace(title, @"^[\s,.;:-]+|[\s,.;:-]+$", string.Empty);
        title = Regex.Replace(title, @"\s+(and|but|then|,)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        if (title.Length > 0)
        {
            title = char.ToUpper(title[0]) + title[1..];
        }

        return title.Length > 500 ? title[..500] : title;
    }
}
