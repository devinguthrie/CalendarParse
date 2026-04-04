using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Services;

namespace CalendarParse.ViewModels;

public class ConfirmationPageViewModel
{
    private readonly ApiClient _api;
    private readonly ScheduleHistoryDb _db;
    private readonly IJobPollingService _pollingService;

    public ConfirmationPageViewModel(ApiClient api, ScheduleHistoryDb db, IJobPollingService pollingService)
    {
        _api = api;
        _db = db;
        _pollingService = pollingService;
    }

    public async Task<string> GetEmployeeNameAsync(CancellationToken ct = default)
        => (await _db.GetPreferencesAsync(ct)).EmployeeName;

    public Task<HealthResult?> CheckHealthAsync(CancellationToken ct = default)
        => _api.CheckHealthAsync(ct);

    public Task<bool> ConfirmAsync(List<ShiftData> shifts, CancellationToken ct = default)
        => _api.ConfirmAsync(shifts, ct);

    public async Task<SubmitRunOutcome> SubmitForProcessingAsync(
        byte[] imageBytes,
        string employeeName,
        CancellationToken ct = default)
    {
        var imagePath = await SaveImageFileAsync(imageBytes);
        var jobId = await _api.SubmitAsync(imageBytes, employeeName, ct);
        if (jobId is null)
            return SubmitRunOutcome.Fail("Could not submit to server.\nCheck connection and try again.");

        var runId = await _db.CreateProcessingRunAsync(jobId, imagePath, ct);
        _pollingService.StartPolling(runId, jobId);
        return SubmitRunOutcome.Success(runId, jobId);
    }

    private static async Task<string?> SaveImageFileAsync(byte[] imageBytes)
    {
        try
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "schedules");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(path, imageBytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed record SubmitRunOutcome(bool IsSuccess, int RunId, string? JobId, string? ErrorMessage)
{
    public static SubmitRunOutcome Success(int runId, string jobId)
        => new(true, runId, jobId, null);

    public static SubmitRunOutcome Fail(string message)
        => new(false, -1, null, message);
}
