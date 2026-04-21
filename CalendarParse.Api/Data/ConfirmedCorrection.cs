namespace CalendarParse.Api.Data;

/// <summary>
/// One user-confirmed truth payload captured from the mobile confirmation flow.
/// This is the authoritative benchmark source for real-world app usage.
/// </summary>
public class ConfirmedCorrection
{
    public int      Id            { get; set; }
    public string?  JobId         { get; set; }
    public string?  ImagePath     { get; set; }
    public string   EmployeeName  { get; set; } = string.Empty;
    public string   ShiftsJson    { get; set; } = "[]";
    public DateTime ConfirmedAtUtc { get; set; } = DateTime.UtcNow;
}
