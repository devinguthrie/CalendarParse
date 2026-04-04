namespace CalendarParse.Services;

/// <summary>
/// Starts and stops the background job polling loop.
/// On Android: delegates to a Foreground Service (survives backgrounding).
/// On other platforms: no-op (polling only runs while app is foregrounded).
/// </summary>
public interface IJobPollingService
{
    /// <summary>Begin polling for <paramref name="jobId"/> and update the local run record when done.</summary>
    void StartPolling(int runId, string jobId);

    /// <summary>Cancel all active poll loops (called on app shutdown).</summary>
    void StopAll();
}

/// <summary>
/// Raised by the polling service (or foreground service) when any job finishes (done or error).
/// Pages can subscribe to refresh UI without relying solely on OnAppearing.
/// </summary>
public static class JobEvents
{
    public static event Action? JobFinished;
    public static void NotifyJobFinished() => JobFinished?.Invoke();
}

/// <summary>No-op implementation for non-Android targets.</summary>
public class NoOpJobPollingService : IJobPollingService
{
    public void StartPolling(int runId, string jobId) { }
    public void StopAll() { }
}
