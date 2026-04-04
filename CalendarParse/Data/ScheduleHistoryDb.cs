using CalendarParse.Models;
using Microsoft.EntityFrameworkCore;

namespace CalendarParse.Data;

public class ScheduleHistoryDb : DbContext
{
    public DbSet<AppPreferences>      Preferences          { get; set; } = null!;
    public DbSet<ScheduleRun>         ScheduleRuns         { get; set; } = null!;
    public DbSet<PendingConfirmation> PendingConfirmations { get; set; } = null!;

    public ScheduleHistoryDb(DbContextOptions<ScheduleHistoryDb> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppPreferences>().HasKey(p => p.Id);
        b.Entity<ScheduleRun>().HasKey(r => r.Id);
        b.Entity<PendingConfirmation>().HasKey(p => p.Id);

        // Store TimeOnly as TEXT ("HH:mm")
        b.Entity<AppPreferences>()
            .Property(p => p.QuietHoursStart)
            .HasConversion(t => t.ToString("HH:mm"), s => TimeOnly.Parse(s));
        b.Entity<AppPreferences>()
            .Property(p => p.QuietHoursEnd)
            .HasConversion(t => t.ToString("HH:mm"), s => TimeOnly.Parse(s));
    }

    // ── Preferences ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the singleton preferences row, creating it with defaults if absent.
    /// </summary>
    public async Task<AppPreferences> GetPreferencesAsync(CancellationToken ct = default)
    {
        var prefs = await Preferences.FindAsync([1], ct);
        if (prefs is null)
        {
            prefs = new AppPreferences { Id = 1 };
            Preferences.Add(prefs);
            await SaveChangesWithRetryAsync(ct);
        }
        return prefs;
    }

    // ── Schedule runs ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new run tracking an async server job (Status=Processing).
    /// Image dims are unknown at submit time — populated later when the result arrives.
    /// </summary>
    public async Task<int> CreateProcessingRunAsync(
        string remoteJobId, string? imagePath,
        CancellationToken ct = default)
    {
        var run = new ScheduleRun
        {
            Status      = RunStatus.Processing,
            RemoteJobId = remoteJobId,
            ImagePath   = imagePath,
            ProcessedAt = DateTime.UtcNow,
        };
        ScheduleRuns.Add(run);
        await SaveChangesWithRetryAsync(ct);
        return run.Id;
    }

    /// <summary>
    /// Creates a new in-progress run (legacy: image already processed, dims known).
    /// Call this when processing begins — before the API result comes back.
    /// </summary>
    public async Task<int> CreateRunAsync(
        string? imagePath, int imageWidth, int imageHeight,
        CancellationToken ct = default)
    {
        var run = new ScheduleRun
        {
            ImagePath   = imagePath,
            ImageWidth  = imageWidth,
            ImageHeight = imageHeight,
            ProcessedAt = DateTime.UtcNow,
        };
        ScheduleRuns.Add(run);
        await SaveChangesWithRetryAsync(ct);
        return run.Id;
    }

    /// <summary>
    /// Updates the in-progress bubble state after a confirmation action.
    /// Projected write — does not re-load the entity if already tracked.
    /// </summary>
    public async Task UpdateRunProgressAsync(
        int runId, string shiftsJson, int confirmedCount, int totalCount,
        CancellationToken ct = default)
    {
        var run = await ScheduleRuns.FindAsync([runId], ct);
        if (run is null) return;

        run.ShiftsJson     = shiftsJson;
        run.ConfirmedCount = confirmedCount;
        run.TotalCount     = totalCount;
        await SaveChangesWithRetryAsync(ct);
    }

    /// <summary>
    /// Marks a run as complete and stores the final summary fields.
    /// </summary>
    public async Task MarkRunCompleteAsync(
        int runId, string shiftsJson, int totalMinutes, string weekStart,
        CancellationToken ct = default)
    {
        var run = await ScheduleRuns.FindAsync([runId], ct);
        if (run is null) return;

        run.ShiftsJson     = shiftsJson;
        run.IsComplete     = true;
        run.ConfirmedCount = run.TotalCount;
        run.TotalMinutes   = totalMinutes;
        run.WeekStart      = weekStart;
        await SaveChangesWithRetryAsync(ct);
    }

    /// <summary>
    /// Called by the polling service when the server finishes processing.
    /// Populates shifts + image dims and advances Status to CorrectionInProgress.
    /// </summary>
    public async Task UpdateRunWithResultAsync(
        int runId, string shiftsJson, int imageWidth, int imageHeight,
        CancellationToken ct = default)
    {
        // Use a fresh DB read instead of the change-tracker cache: the singleton DbContext
        // is shared across threads (polling service + UI), so FindAsync might return a
        // stale tracked entity. AsNoTracking + Attach ensures we write the real data.
        var run = await ScheduleRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        if (run is null)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateRunWithResultAsync] runId={runId} NOT FOUND in DB");
            return;
        }

        run.ShiftsJson   = shiftsJson;
        run.ImageWidth   = imageWidth;
        run.ImageHeight  = imageHeight;
        run.Status       = RunStatus.CorrectionInProgress;
        run.ErrorMessage = null;

        // Detach any tracked copy before attaching the updated one
        var tracked = ScheduleRuns.Local.FirstOrDefault(r => r.Id == runId);
        if (tracked is not null)
            Entry(tracked).State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        ScheduleRuns.Update(run);
        await SaveChangesWithRetryAsync(ct);

        System.Diagnostics.Debug.WriteLine(
            $"[UpdateRunWithResultAsync] runId={runId} saved shiftsLen={shiftsJson.Length} status=CorrectionInProgress");
    }

    /// <summary>Updates run Status and optionally sets an error message (for Error state).</summary>
    public async Task UpdateRunStatusAsync(
        int runId,
        RunStatus status,
        string? errorMessage = null,
        string? remoteJobId = null,
        CancellationToken ct = default)
    {
        var run = await ScheduleRuns.FindAsync([runId], ct);
        if (run is null) return;

        run.Status       = status;
        run.ErrorMessage = errorMessage;
        if (remoteJobId is not null)
            run.RemoteJobId = remoteJobId;
        await SaveChangesWithRetryAsync(ct);
    }

    /// <summary>Returns all runs currently in Processing state (for resuming polls on app restart).</summary>
    public async Task<List<ScheduleRun>> GetProcessingRunsAsync(CancellationToken ct = default)
        => await ScheduleRuns
            .Where(r => r.Status == RunStatus.Processing && r.RemoteJobId != null)
            .ToListAsync(ct);

    /// <summary>
    /// Returns the 50 most recent runs (newest first), projected — does NOT load ShiftsJson.
    /// Safe to call from the history list without loading image-sized blobs.
    /// </summary>
    public async Task<List<ScheduleRunSummary>> GetRecentRunsAsync(CancellationToken ct = default)
    {
        return await ScheduleRuns
            .OrderByDescending(r => r.ProcessedAt)
            .Take(50)
            .Select(r => new ScheduleRunSummary
            {
                Id             = r.Id,
                ProcessedAt    = r.ProcessedAt,
                IsComplete     = r.IsComplete,
                ConfirmedCount = r.ConfirmedCount,
                TotalCount     = r.TotalCount,
                WeekStart      = r.WeekStart,
                TotalMinutes   = r.TotalMinutes,
                Status         = r.Status,
                RemoteJobId    = r.RemoteJobId,
                ErrorMessage   = r.ErrorMessage,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Loads a full run including ShiftsJson for resume. Returns null if not found.
    /// Always reads fresh from the database (AsNoTracking) to avoid stale data from
    /// the singleton change tracker when the polling service updates from a background thread.
    /// </summary>
    public async Task<ScheduleRun?> GetRunForResumeAsync(int runId, CancellationToken ct = default)
    {
        var run = await ScheduleRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        System.Diagnostics.Debug.WriteLine(
            $"[GetRunForResumeAsync] runId={runId} status={run?.Status} " +
            $"shiftsLen={run?.ShiftsJson?.Length ?? 0} imageW={run?.ImageWidth}");

        return run;
    }

    // ── SaveChanges ───────────────────────────────────────────────────────────

    /// <summary>
    /// SaveChanges with 3× / 100ms exponential backoff on SQLite locked errors.
    /// </summary>
    public async Task SaveChangesWithRetryAsync(CancellationToken ct = default)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await SaveChangesAsync(ct);
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
                when (ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ && attempt < 3)
            {
                await Task.Delay(100 * attempt, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
                when (ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */)
            {
                // Corrupt DB — delete and recreate (user sees "History reset" toast via caller)
                throw new ScheduleDbCorruptException(ex);
            }
        }
    }
}

/// <summary>Thrown when the SQLite database is corrupt and has been reset.</summary>
public class ScheduleDbCorruptException(Exception inner)
    : Exception("Schedule history database is corrupt and must be reset.", inner);
