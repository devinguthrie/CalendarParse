using System.Text.Json;
using CalendarParse.Api;
using CalendarParse.Api.Data;
using CalendarParse.Api.Services;
using CalendarParse.Models;
using CalendarParse.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CalendarParse.Tests.Api;

public class BackgroundJobProcessorTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TestDbContextFactory _dbFactory;
    private readonly List<string> _tempFiles = [];

    public BackgroundJobProcessorTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<JobDbContext>()
            .UseSqlite(_conn)
            .Options;
        _dbFactory = new TestDbContextFactory(opts);
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private BackgroundJobProcessor CreateProcessor(
        ICalendarParseService? service = null,
        bool debugMode = false,
        TimeSpan? jobTimeout = null,
        TimeSpan? debugDelay = null)
    {
        service ??= BuildOkService().Object;
        return new BackgroundJobProcessor(
            _dbFactory,
            ollamaBaseUrl:  "http://localhost:11434",
            ollamaModel:    "test-model",
            debugMode:      debugMode,
            logger:         NullLogger<BackgroundJobProcessor>.Instance,
            serviceFactory: () => service,
            jobTimeout:     jobTimeout ?? TimeSpan.FromSeconds(30),
            debugDelay:     debugDelay ?? TimeSpan.FromMilliseconds(1));
    }

    private Mock<ICalendarParseService> BuildOkService(string? response = null)
    {
        var svc = new Mock<ICalendarParseService>();
        svc.Setup(s => s.ProcessAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response ?? MakeCalendarJson());
        return svc;
    }

    private async Task<Job> AddJobAsync(
        string employeeName = "Alice",
        JobStatus status = JobStatus.Submitted,
        DateTime? nextRetryAt = null,
        int retryCount = 0,
        DateTime? submittedAt = null)
    {
        var job = new Job
        {
            ImagePath    = CreateTempImageFile(),
            EmployeeName = employeeName,
            Status       = status,
            NextRetryAt  = nextRetryAt,
            RetryCount   = retryCount,
            SubmittedAt  = submittedAt ?? DateTime.UtcNow,
        };
        await using var db = _dbFactory.CreateDbContext();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<Job> RefreshJobAsync(string id)
    {
        await using var db = _dbFactory.CreateDbContext();
        return await db.Jobs.FindAsync(id)
            ?? throw new InvalidOperationException($"Job {id} not found.");
    }

    private string CreateTempImageFile()
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".jpg");
        // Minimal valid JPEG (SOI + EOI markers)
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        _tempFiles.Add(path);
        return path;
    }

    private static string MakeCalendarJson(
        string employeeName = "Alice",
        string date         = "2025-11-03",
        string shift        = "9am-5pm")
    {
        var data = new CalendarData
        {
            Month     = "November",
            Year      = 2025,
            Employees =
            [
                new EmployeeSchedule
                {
                    Name   = employeeName,
                    Shifts = [new ShiftEntry { Date = date, Shift = shift }],
                }
            ],
        };
        return JsonSerializer.Serialize(data);
    }

    // ── ResetStalledJobs ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetStalledJobs_ProcessingJobs_AreResetToSubmitted()
    {
        var job = await AddJobAsync(status: JobStatus.Processing);
        var processor = CreateProcessor();

        await processor.ResetStalledJobsAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Submitted, updated.Status);
        Assert.Null(updated.NextRetryAt);
    }

    [Fact]
    public async Task ResetStalledJobs_NonProcessingJobs_AreUntouched()
    {
        var done  = await AddJobAsync(status: JobStatus.Done);
        var error = await AddJobAsync(status: JobStatus.Error);
        var processor = CreateProcessor();

        await processor.ResetStalledJobsAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Done,  (await RefreshJobAsync(done.Id)).Status);
        Assert.Equal(JobStatus.Error, (await RefreshJobAsync(error.Id)).Status);
    }

    // ── ProcessNextJob — no work ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessNextJob_WhenNoSubmittedJobs_CompletesWithoutError()
    {
        var processor = CreateProcessor();
        await processor.ProcessNextJobAsync(CancellationToken.None);
    }

    // ── ProcessNextJob — success ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessNextJob_SubmittedJob_BecomesProcessingThenDone()
    {
        var job = await AddJobAsync();
        var processor = CreateProcessor();

        await processor.ProcessNextJobAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Done, (await RefreshJobAsync(job.Id)).Status);
    }

    [Fact]
    public async Task ProcessNextJob_Success_FullStateAsserted()
    {
        var job = await AddJobAsync();
        var processor = CreateProcessor();

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Done, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.NotNull(updated.ResultJson);
        Assert.Null(updated.Error);
        Assert.Null(updated.NextRetryAt);
    }

    [Fact]
    public async Task ProcessNextJob_Success_ResultJsonContainsCorrectShifts()
    {
        var job = await AddJobAsync();
        var processor = CreateProcessor(
            BuildOkService(MakeCalendarJson("Alice", "2025-11-03", "9am-5pm")).Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        var result = JsonSerializer.Deserialize<JobResultResponse>(
            updated.ResultJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var row = Assert.Single(result!.Shifts);
        Assert.Equal("Alice",      row.Employee);
        Assert.Equal("2025-11-03", row.Date);
        Assert.Equal("9am-5pm",    row.TimeRange);
    }

    [Fact]
    public async Task ProcessNextJob_EmployeeNameForwardedToService()
    {
        var job = await AddJobAsync(employeeName: "SpecificName");
        var svcMock = BuildOkService(MakeCalendarJson("SpecificName"));
        var processor = CreateProcessor(svcMock.Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        svcMock.Verify(
            s => s.ProcessAsync(It.IsAny<Stream>(), "SpecificName", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessNextJob_PicksOldestSubmittedJobFirst()
    {
        var older = await AddJobAsync(submittedAt: DateTime.UtcNow.AddMinutes(-10));
        var newer = await AddJobAsync(submittedAt: DateTime.UtcNow);
        var processor = CreateProcessor();

        await processor.ProcessNextJobAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Done,      (await RefreshJobAsync(older.Id)).Status);
        Assert.Equal(JobStatus.Submitted, (await RefreshJobAsync(newer.Id)).Status);
    }

    [Fact]
    public async Task ProcessNextJob_FutureNextRetryAt_IsSkipped()
    {
        var job = await AddJobAsync(nextRetryAt: DateTime.UtcNow.AddMinutes(5), retryCount: 1);
        var processor = CreateProcessor();

        await processor.ProcessNextJobAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Submitted, (await RefreshJobAsync(job.Id)).Status);
    }

    // ── ProcessNextJob — error paths ──────────────────────────────────────────────

    [Fact]
    public async Task ProcessNextJob_ErrorPrefixTransient_SchedulesRetry()
    {
        var job = await AddJobAsync();
        var processor = CreateProcessor(BuildOkService("ERROR: connection refused").Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Submitted, updated.Status);
        Assert.Equal(1, updated.RetryCount);
        Assert.NotNull(updated.NextRetryAt);
        Assert.Null(updated.CompletedAt);
    }

    [Fact]
    public async Task ProcessNextJob_ErrorPrefixNonTransient_SetsPermanentError()
    {
        var job = await AddJobAsync();
        var processor = CreateProcessor(
            BuildOkService("ERROR: GLM-OCR returned no parseable table headers").Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Error, updated.Status);
        Assert.NotNull(updated.Error);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task ProcessNextJob_HttpRequestException_SchedulesRetry()
    {
        var svcMock = new Mock<ICalendarParseService>();
        svcMock.Setup(s => s.ProcessAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        var job = await AddJobAsync();
        var processor = CreateProcessor(svcMock.Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Submitted, updated.Status);
        Assert.Equal(1, updated.RetryCount);
        Assert.NotNull(updated.NextRetryAt);
    }

    [Fact]
    public async Task ProcessNextJob_UnexpectedException_SetsPermanentError()
    {
        var svcMock = new Mock<ICalendarParseService>();
        svcMock.Setup(s => s.ProcessAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected failure"));
        var job = await AddJobAsync();
        var processor = CreateProcessor(svcMock.Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Error, updated.Status);
        Assert.NotNull(updated.Error);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task ProcessNextJob_MissingImageFile_SetsPermanentError_ServiceNotCalled()
    {
        var svcMock = new Mock<ICalendarParseService>();
        var job = await AddJobAsync();
        File.Delete(job.ImagePath);
        var processor = CreateProcessor(svcMock.Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Error, updated.Status);
        Assert.NotNull(updated.Error);
        svcMock.Verify(
            s => s.ProcessAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessNextJob_MalformedJsonFromService_SetsPermanentError()
    {
        var job = await AddJobAsync();
        var processor = CreateProcessor(BuildOkService("{ not valid json {{").Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Error, updated.Status);
        Assert.NotNull(updated.Error);
    }

    [Fact]
    public async Task ProcessNextJob_MaxRetriesExceeded_SetsPermanentErrorWithMessage()
    {
        var job = await AddJobAsync(retryCount: 5);
        var processor = CreateProcessor(BuildOkService("ERROR: connection refused").Object);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Error, updated.Status);
        Assert.Contains("Max retries exceeded", updated.Error ?? "");
        Assert.NotNull(updated.CompletedAt);
    }

    // ── ProcessNextJob — cancellation ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessNextJob_HostShutdown_ResetsJobToSubmitted_NoRetryIncrement()
    {
        var job = await AddJobAsync();
        var hanging = new HangingCalendarService();
        var processor = CreateProcessor(hanging);
        var cts = new CancellationTokenSource();

        var processTask = processor.ProcessNextJobAsync(cts.Token);
        // Wait deterministically until service has started processing
        await hanging.WhenStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await processTask;

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Submitted, updated.Status);
        Assert.Null(updated.NextRetryAt);
        Assert.Equal(0, updated.RetryCount); // host shutdown should NOT count as a retry
    }

    [Fact]
    public async Task ProcessNextJob_JobTimeout_SchedulesRetry()
    {
        var job = await AddJobAsync();
        var hanging = new HangingCalendarService();
        var processor = CreateProcessor(hanging, jobTimeout: TimeSpan.FromMilliseconds(50));

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Submitted, updated.Status);
        Assert.Equal(1, updated.RetryCount);
        Assert.NotNull(updated.NextRetryAt);
    }

    // ── ScheduleRetry — retry delays ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessNextJob_FirstRetry_NextRetryAtIsApprox15Seconds()
    {
        var job = await AddJobAsync(retryCount: 0);
        var processor = CreateProcessor(BuildOkService("ERROR: connection refused").Object);
        var before = DateTime.UtcNow;

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.NotNull(updated.NextRetryAt);
        var delta = updated.NextRetryAt!.Value - before;
        Assert.True(delta >= TimeSpan.FromSeconds(14) && delta <= TimeSpan.FromSeconds(20),
            $"Expected ~15s delay but got {delta}");
    }

    [Fact]
    public async Task ProcessNextJob_SecondRetry_NextRetryAtIsApprox30Seconds()
    {
        var job = await AddJobAsync(retryCount: 1);
        var processor = CreateProcessor(BuildOkService("ERROR: connection refused").Object);
        var before = DateTime.UtcNow;

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.NotNull(updated.NextRetryAt);
        var delta = updated.NextRetryAt!.Value - before;
        Assert.True(delta >= TimeSpan.FromSeconds(29) && delta <= TimeSpan.FromSeconds(35),
            $"Expected ~30s delay but got {delta}");
    }

    // ── Debug mode ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessNextJob_DebugMode_ReturnsMockResult_ServiceNotCalled()
    {
        var svcMock = BuildOkService();
        var job = await AddJobAsync();
        var processor = CreateProcessor(svcMock.Object, debugMode: true);

        await processor.ProcessNextJobAsync(CancellationToken.None);

        var updated = await RefreshJobAsync(job.Id);
        Assert.Equal(JobStatus.Done, updated.Status);
        Assert.NotNull(updated.ResultJson);
        svcMock.Verify(
            s => s.ProcessAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Inner helpers ─────────────────────────────────────────────────────────────

    private sealed class TestDbContextFactory : IDbContextFactory<JobDbContext>
    {
        private readonly DbContextOptions<JobDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<JobDbContext> opts) => _opts = opts;
        public JobDbContext CreateDbContext() => new(_opts);
    }

    /// <summary>
    /// Signals <see cref="WhenStarted"/> as soon as <see cref="ProcessAsync"/> is called,
    /// then hangs until the cancellation token is triggered.
    /// Use this to test cancellation deterministically without <c>Task.Delay</c> races.
    /// </summary>
    private sealed class HangingCalendarService : ICalendarParseService
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhenStarted => _started.Task;

        public async Task<string> ProcessAsync(
            Stream imageStream, string nameFilter, CancellationToken ct = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ""; // unreachable
        }
    }
}
