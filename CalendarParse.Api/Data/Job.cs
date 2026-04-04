namespace CalendarParse.Api.Data;

public enum JobStatus { Submitted, Processing, Done, Error }

/// <summary>
/// Server-side record for one async schedule-processing job.
///
/// State machine:
///   Submitted ──► Processing ──► Done
///                            ↘── Error
/// </summary>
public class Job
{
    public string    Id           { get; set; } = Guid.NewGuid().ToString("N");
    public JobStatus Status       { get; set; } = JobStatus.Submitted;

    /// <summary>Absolute path to the image file on the server filesystem.</summary>
    public string    ImagePath    { get; set; } = string.Empty;

    /// <summary>Employee name filter passed with the request.</summary>
    public string    EmployeeName { get; set; } = string.Empty;

    /// <summary>JSON result (ProcessResponse) — populated when Status == Done.</summary>
    public string?   ResultJson   { get; set; }

    /// <summary>Error message — populated when Status == Error.</summary>
    public string?   Error        { get; set; }

    public DateTime  SubmittedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt  { get; set; }
}
