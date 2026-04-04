namespace CalendarParse.Models;

/// <summary>
/// Lifecycle state of a schedule parse session.
///
/// State machine:
///   Processing ──────────────────────────────► CorrectionInProgress ──► Completed
///               (server done, result fetched)   (user confirmed all)
///               ↘── Error (server returned error or network failure)
/// </summary>
public enum RunStatus
{
    Processing          = 0,   // Submitted to server; waiting for result
    CorrectionInProgress = 1,  // Result received; user reviewing overlays
    Completed           = 2,   // All shifts confirmed and submitted
    Error               = 3,   // Server returned error (retry available)
}

/// <summary>
/// One schedule parse session — created when processing begins, updated as the user
/// confirms shifts, marked complete when all shifts are confirmed and submitted.
///
/// State flow:
///   Created (Status=Processing, RemoteJobId set)
///     → polling detects done → Status=CorrectionInProgress, ShiftsJson populated
///     → updated on each thumbs-up/edit-save (ConfirmedCount++)
///     → completed (Status=Completed, TotalMinutes/WeekStart set)
/// </summary>
public class ScheduleRun
{
    public int      Id          { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    // ── Async job tracking ────────────────────────────────────────────────────

    /// <summary>Status in the async job lifecycle. Default matches old local-only runs.</summary>
    public RunStatus Status { get; set; } = RunStatus.CorrectionInProgress;

    /// <summary>Server-assigned job ID from POST /submit. Null for legacy local-only runs.</summary>
    public string? RemoteJobId { get; set; }

    /// <summary>Error message when Status == Error.</summary>
    public string? ErrorMessage { get; set; }

    // ── Completion state ──────────────────────────────────────────────────────

    public bool IsComplete     { get; set; } = false;
    public int  ConfirmedCount { get; set; } = 0;
    public int  TotalCount     { get; set; } = 0;

    // ── Bubble state (serialized JSON for save/resume) ────────────────────────

    /// <summary>JSON array of BubblePersist records. Updated after each confirmation action.</summary>
    public string ShiftsJson { get; set; } = "[]";

    // ── Image reference ───────────────────────────────────────────────────────

    /// <summary>Path to the schedule image file saved in AppDataDirectory/schedules/. Null if image unavailable.</summary>
    public string? ImagePath   { get; set; }

    /// <summary>Natural pixel dimensions of the source image for correct overlay scale on resume.</summary>
    public int ImageWidth  { get; set; }
    public int ImageHeight { get; set; }

    // ── Summary (set on completion, used by ScheduleSummaryPage) ─────────────

    /// <summary>Total minutes worked during the week of this schedule.</summary>
    public int TotalMinutes { get; set; }

    /// <summary>ISO week start (Monday), e.g. "2026-01-05".</summary>
    public string WeekStart { get; set; } = string.Empty;
}

/// <summary>Lightweight projection used by the history list — no ShiftsJson loaded.</summary>
public class ScheduleRunSummary
{
    public int       Id             { get; set; }
    public DateTime  ProcessedAt    { get; set; }
    public bool      IsComplete     { get; set; }
    public int       ConfirmedCount { get; set; }
    public int       TotalCount     { get; set; }
    public string    WeekStart      { get; set; } = string.Empty;
    public int       TotalMinutes   { get; set; }
    public RunStatus Status         { get; set; } = RunStatus.CorrectionInProgress;
    public string?   RemoteJobId    { get; set; }
    public string?   ErrorMessage   { get; set; }

    public string ProgressText => Status switch
    {
        RunStatus.Processing => "Processing…",
        RunStatus.Error      => "Error — tap to retry",
        _                    => TotalCount == 0 ? "No shifts" : $"{ConfirmedCount}/{TotalCount} confirmed",
    };

    public string DateText =>
        ProcessedAt == default ? "—" : ProcessedAt.ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    public string WeekLabel =>
        string.IsNullOrEmpty(WeekStart) ? string.Empty : $"Week of {WeekStart}";

    public bool HasWeekLabel => !string.IsNullOrEmpty(WeekStart);

    public bool IsProcessing => Status == RunStatus.Processing;
    public bool IsError      => Status == RunStatus.Error;
}
