namespace CalendarParse.Core.Services;

/// <summary>
/// Pure-logic helpers for formatting and parsing shift times, and seeding
/// the two TimePicker controls from a display-time string.  Extracted from
/// ConfirmationPage so the logic can be unit-tested without MAUI.
/// </summary>
public static class TimeFormatHelper
{
    /// <summary>
    /// Formats a start/end TimeSpan pair as the canonical "H:mm-H:mm" string.
    /// Hours are 12-hour (1–12, no AM/PM);  minutes are zero-padded.
    /// Example:  09:00–17:00  →  "9:00-5:00"
    /// </summary>
    public static string FormatTimeRange(TimeSpan start, TimeSpan end)
    {
        static string Fmt(TimeSpan t)
        {
            int h = t.Hours % 12;
            if (h == 0) h = 12;
            return $"{h}:{t.Minutes:D2}";
        }
        return $"{Fmt(start)}-{Fmt(end)}";
    }

    /// <summary>
    /// Parses a single time token from a shift display string.
    /// Accepts "H:mm", "HH:mm", "H:mmtt", "HH:mmtt" (invariant culture),
    /// and falls back to DateTime.TryParse for locale-aware inputs.
    /// </summary>
    public static bool TryParseShiftTime(string s, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (TimeSpan.TryParseExact(s.Trim(),
                new[] { @"h\:mm", @"hh\:mm", @"h\:mmtt", @"hh\:mmtt" },
                System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;

        if (DateTime.TryParse(s.Trim(), out var dt))
        {
            result = dt.TimeOfDay;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a display-time string of the form "H:mm-H:mm" (or just "H:mm")
    /// into start/end TimeSpan values suitable for seeding TimePicker controls.
    ///
    /// Returns sensible defaults (09:00 / 17:00) for blank or unparsable input.
    ///
    /// <paramref name="crossTwelveStartIsPm"/> controls how to resolve ambiguous times
    /// where both tokens are in the 12-hour range and start &gt; end (crosses noon/midnight):
    ///  - false (default): start is AM, end is PM  →  e.g. "9:00-1:00" → 09:00 / 13:00
    ///  - true:            start is PM, end is AM  →  e.g. "9:00-1:00" → 21:00 / 01:00
    /// </summary>
    public static (TimeSpan Start, TimeSpan End) SeedTimePickers(
        string displayTime,
        bool   crossTwelveStartIsPm = false)
    {
        var start = TimeSpan.FromHours(9);
        var end   = TimeSpan.FromHours(17);

        if (string.IsNullOrWhiteSpace(displayTime))
            return (start, end);

        var parts = displayTime.Split('-', 2, StringSplitOptions.TrimEntries);
        if (TryParseShiftTime(parts[0], out var parsedStart))
            start = parsedStart;
        if (parts.Length > 1 && TryParseShiftTime(parts[1], out var parsedEnd))
            end = parsedEnd;

        // Cross-12 adjustment: both tokens in the sub-12h range and start > end
        // means the shift straddles noon or midnight.  Apply the caller's preference.
        if (start.TotalHours < 12.0 && end.TotalHours < 12.0
            && start.TotalHours > end.TotalHours)
        {
            if (crossTwelveStartIsPm)
                start = start.Add(TimeSpan.FromHours(12));
            else
                end = end.Add(TimeSpan.FromHours(12));
        }

        return (start, end);
    }
}
