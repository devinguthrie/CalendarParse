namespace CalendarParse.Data;

/// <summary>Persisted user preferences. One row, Id=1.</summary>
public class AppPreferences
{
    public int Id { get; set; } = 1;
    public string EmployeeName { get; set; } = string.Empty;
    public string ServerUrl    { get; set; } = string.Empty;
    public string ServerKey    { get; set; } = string.Empty;

    /// <summary>Whether quiet hours suppression is enabled.</summary>
    public bool QuietHoursEnabled { get; set; }

    /// <summary>Quiet hours window start (local time-of-day, e.g. 22:00).</summary>
    public TimeOnly QuietHoursStart { get; set; } = new TimeOnly(22, 0);

    /// <summary>Quiet hours window end (local time-of-day, e.g. 07:00).</summary>
    public TimeOnly QuietHoursEnd { get; set; } = new TimeOnly(7, 0);

    /// <summary>
    /// Whether the user opted in to position training data collection.
    /// Null = never asked; true = opted in; false = opted out (never ask again).
    /// </summary>
    public bool? PositionOptIn { get; set; }
}
