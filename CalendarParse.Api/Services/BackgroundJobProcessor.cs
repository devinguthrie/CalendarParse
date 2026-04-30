using System.Text.Json;
using CalendarParse.Api.Data;
using CalendarParse.Models;
using CalendarParse.Parsing.Services;
using CalendarParse.Services;
using Microsoft.EntityFrameworkCore;

namespace CalendarParse.Api.Services;

/// <summary>
/// Hosted background service that polls for Submitted jobs and processes them via
/// the GLM-OCR pipeline. Provides exponential-backoff retries for transient Ollama
/// failures (cold-start, GPU memory pressure, connectivity blips).
///
/// Retry schedule (up to MaxRetries=5):
///   Attempt 1 → wait 15s → Attempt 2 → wait 30s → Attempt 3 → wait 1m
///            → Attempt 4 → wait 2m  → Attempt 5 → wait 5m  → permanent Error
///
/// Cancellation handling:
///   - Host shutdown (stoppingToken) → reset job to Submitted so next startup picks it up.
///   - Per-job timeout               → schedule retry as a transient failure.
/// </summary>
public class BackgroundJobProcessor : BackgroundService
{
    private readonly IDbContextFactory<JobDbContext> _dbFactory;
    private readonly string _ollamaBaseUrl;
    private readonly string _ollamaModel;
    private readonly bool _debugMode;
    private readonly ILogger<BackgroundJobProcessor> _logger;
    private readonly Func<ICalendarParseService> _serviceFactory;
    private readonly TimeSpan _jobTimeout;
    private readonly TimeSpan _debugDelay;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    private const int MaxRetries = 5;

    public BackgroundJobProcessor(
        IDbContextFactory<JobDbContext> dbFactory,
        string ollamaBaseUrl,
        string ollamaModel,
        bool debugMode,
        ILogger<BackgroundJobProcessor> logger,
        Func<ICalendarParseService>? serviceFactory = null,
        TimeSpan? jobTimeout = null,
        TimeSpan? debugDelay = null)
    {
        _dbFactory      = dbFactory;
        _ollamaBaseUrl  = ollamaBaseUrl;
        _ollamaModel    = ollamaModel;
        _debugMode      = debugMode;
        _logger         = logger;
        _serviceFactory = serviceFactory ?? (() => new GlmOcrCalendarService(_ollamaBaseUrl, _ollamaModel));
        _jobTimeout     = jobTimeout ?? TimeSpan.FromMinutes(5);
        _debugDelay     = debugDelay ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reset any jobs stuck in Processing from a previous crash/restart.
        await ResetStalledJobsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ProcessNextJobAsync(stoppingToken);
    }

    internal async Task ResetStalledJobsAsync(CancellationToken stoppingToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
        var stalled = await db.Jobs
            .Where(j => j.Status == JobStatus.Processing)
            .ToListAsync(stoppingToken);

        foreach (var job in stalled)
        {
            job.Status      = JobStatus.Submitted;
            job.NextRetryAt = null;
            _logger.LogWarning("Resetting stalled job {JobId} to Submitted on startup.", job.Id);
        }

        if (stalled.Count > 0)
            await db.SaveChangesAsync(CancellationToken.None);
    }

    internal async Task ProcessNextJobAsync(CancellationToken stoppingToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);

        var now = DateTime.UtcNow;
        var job = await db.Jobs
            .Where(j => j.Status == JobStatus.Submitted
                        && (j.NextRetryAt == null || j.NextRetryAt <= now))
            .OrderBy(j => j.SubmittedAt)
            .FirstOrDefaultAsync(stoppingToken);

        if (job is null) return;

        job.Status = JobStatus.Processing;
        await db.SaveChangesAsync(CancellationToken.None);

        _logger.LogInformation("Processing job {JobId} (attempt {Attempt}).", job.Id, job.RetryCount + 1);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        jobCts.CancelAfter(_jobTimeout);
        var jobCt = jobCts.Token;

        try
        {
            if (_debugMode)
            {
                await Task.Delay(_debugDelay, jobCt);
                job.ResultJson  = JsonSerializer.Serialize(BuildMockJobResultResponse(job.EmployeeName));
                job.Status      = JobStatus.Done;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("[DEBUG] Job {JobId} completed with mock result.", job.Id);
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }

            using var stream = File.OpenRead(job.ImagePath);
            var service = _serviceFactory();
            var rawJson = await service.ProcessAsync(stream, job.EmployeeName, jobCt);

            if (rawJson.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                if (IsTransientError(rawJson))
                    ScheduleRetry(job, rawJson);
                else
                {
                    job.Status      = JobStatus.Error;
                    job.Error       = rawJson;
                    job.CompletedAt = DateTime.UtcNow;
                }
            }
            else
            {
                var calendarData = JsonSerializer.Deserialize<CalendarData>(rawJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var shifts = calendarData.FlattenToShiftData();

                job.ResultJson  = JsonSerializer.Serialize(new JobResultResponse { Shifts = shifts });
                job.Status      = JobStatus.Done;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("Job {JobId} done — {ShiftCount} shift(s).", job.Id, shifts.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — preserve the job so the next startup picks it up.
            job.Status      = JobStatus.Submitted;
            job.NextRetryAt = null;
            _logger.LogInformation("Job {JobId} reset to Submitted (host shutdown).", job.Id);
        }
        catch (OperationCanceledException)
        {
            // Per-job timeout.
            _logger.LogWarning("Job {JobId} timed out after {Timeout}.", job.Id, _jobTimeout);
            ScheduleRetry(job, $"Job timed out after {_jobTimeout}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Job {JobId} — Ollama connectivity error: {Message}", job.Id, ex.Message);
            ScheduleRetry(job, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} — unexpected error.", job.Id);
            job.Status      = JobStatus.Error;
            job.Error       = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private void ScheduleRetry(Job job, string reason)
    {
        if (job.RetryCount >= MaxRetries)
        {
            job.Status      = JobStatus.Error;
            job.Error       = $"Max retries exceeded. Last error: {reason}";
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogError("Job {JobId} permanently failed after {MaxRetries} retries.", job.Id, MaxRetries);
            return;
        }

        var delay = RetryDelays[Math.Min(job.RetryCount, RetryDelays.Length - 1)];
        job.Status      = JobStatus.Submitted;
        job.NextRetryAt = DateTime.UtcNow + delay;
        job.RetryCount++;
        _logger.LogWarning("Job {JobId} scheduled for retry {Attempt} in {Delay}. Reason: {Reason}",
            job.Id, job.RetryCount, delay, reason);
    }

    private static bool IsTransientError(string errorMessage) =>
        errorMessage.Contains("connect",     StringComparison.OrdinalIgnoreCase) ||
        errorMessage.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        errorMessage.Contains("timeout",     StringComparison.OrdinalIgnoreCase);

    // ── Debug mode helpers ────────────────────────────────────────────────────

    private static List<ShiftData> BuildMockShifts(string employeeName)
    {
        var name = string.IsNullOrWhiteSpace(employeeName) ? "Franny" : employeeName;
        return
        [
            new ShiftData { Employee = name, Date = "2025-11-02", TimeRange = "10:00-6:30" },
            new ShiftData { Employee = name, Date = "2025-11-03", TimeRange = "2:00-6:30"  },
            new ShiftData { Employee = name, Date = "2025-11-04", TimeRange = "2:00-6:30"  },
            new ShiftData { Employee = name, Date = "2025-11-05", TimeRange = "2:00-6:30"  },
            new ShiftData { Employee = name, Date = "2025-11-06", TimeRange = "2:00-6:30"  },
            new ShiftData { Employee = name, Date = "2025-11-07", TimeRange = "4:00-6:30"  },
            new ShiftData { Employee = name, Date = "2025-11-08", TimeRange = "4:00-6:30"  },
        ];
    }

    private static JobResultResponse BuildMockJobResultResponse(string employeeName) => new()
    {
        Shifts = BuildMockShifts(employeeName),
    };
}
