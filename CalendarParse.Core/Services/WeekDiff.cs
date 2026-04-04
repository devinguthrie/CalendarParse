namespace CalendarParse.Services;

/// <summary>Pure helper for week-over-week shift hour comparison.</summary>
public static class WeekDiff
{
    /// <summary>Returns the Monday of the week containing <paramref name="date"/>.</summary>
    public static DateTime MondayOf(DateTime date)
    {
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.Date.AddDays(-diff);
    }

    /// <summary>Formats a diff string: "+1.5 hrs vs last Mon–Sun" or "—" when no prior data.</summary>
    public static string Format(int currentMinutes, int? priorMinutes)
    {
        if (priorMinutes is null) return "—";
        var diff = currentMinutes - priorMinutes.Value;
        var sign = diff >= 0 ? "+" : "";
        return $"{sign}{diff / 60.0:F1} hrs vs last Mon–Sun";
    }

    /// <summary>
    /// Returns true when <paramref name="time"/> falls inside the quiet-hours window.
    /// Handles midnight-spanning windows (e.g. 22:00–07:00).
    /// </summary>
    public static bool IsInQuietWindow(TimeOnly time, TimeOnly start, TimeOnly end)
    {
        return start <= end
            ? time >= start && time < end        // same-day window (e.g. 08:00–12:00)
            : time >= start || time < end;       // midnight-spanning (e.g. 22:00–07:00)
    }
}
