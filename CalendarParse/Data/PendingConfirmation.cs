namespace CalendarParse.Data;

/// <summary>
/// Shifts queued for retry when POST /confirm failed.
/// Cleared on successful re-submit.
/// </summary>
public class PendingConfirmation
{
    public int    Id        { get; set; }
    public string ShiftsJson { get; set; } = string.Empty;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}
