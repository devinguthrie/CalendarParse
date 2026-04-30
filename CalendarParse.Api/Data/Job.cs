namespace CalendarParse.Api.Data;

public enum JobStatus { Submitted, Processing, Done, Error }

/// <summary>
/// Server-side record for one async schedule-processing job.
///
/// State machine:
///   Submitted ──► Processing ──► Done
///         ▲                  ↘── Error (transient: re-queued with NextRetryAt)
///         └──── retry (up to MaxRetries) ──────────┘
/// </summary>
public class Job
{
    public string    Id           { get; set; } = Guid.NewGuid().ToString("N");
    public JobStatus Status       { get; set; } = JobStatus.Submitted;

    /// <summary>Absolute path to the image file on the server filesystem.</summary>
    public string    ImagePath    { get; set; } = string.Empty;

    /// <summary>Employee name filter passed with the request.</summary>
    public string    EmployeeName { get; set; } = string.Empty;

    /// <summary>Auth0 'sub' claim of the user who submitted this job. Null for CLI-submitted jobs.</summary>
    public string?   UserId       { get; set; }

    /// <summary>JSON result (JobResultResponse) — populated when Status == Done.</summary>
    public string?   ResultJson   { get; set; }

    /// <summary>Error message — populated when Status == Error.</summary>
    public string?   Error        { get; set; }

    public DateTime  SubmittedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt  { get; set; }

    /// <summary>Number of times this job has been retried after a transient failure.</summary>
    public int       RetryCount   { get; set; } = 0;

    /// <summary>When set, BackgroundJobProcessor skips this job until the time elapses (exponential backoff).</summary>
    public DateTime? NextRetryAt  { get; set; }
}
