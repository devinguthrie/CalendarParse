#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarParse.Platforms.Android;

/// <summary>
/// Foreground service that polls GET /jobs/{id}/status every 10 seconds until
/// the server finishes processing, then fires a local notification.
///
/// Android allows foreground services to run while the app is in the background,
/// as long as they display a persistent notification — which we use as a
/// "Processing schedule…" indicator.
/// </summary>
[Service(
    ForegroundServiceType = ForegroundService.TypeDataSync,
    Exported = false)]
public class JobPollingForegroundService : Service
{
    internal const string ExtraRunId = "run_id";
    internal const string ExtraJobId = "job_id";

    internal const string ChannelProcessing = "CalendarParse_Processing";
    internal const string ChannelReady      = "CalendarParse_Ready";
    private  const int    NotifProcessing   = 2001;
    private  const int    NotifReadyBase    = 3000; // +runId for uniqueness

    private CancellationTokenSource? _cts;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var runId = intent?.GetIntExtra(ExtraRunId, -1) ?? -1;
        var jobId = intent?.GetStringExtra(ExtraJobId);

        if (runId < 0 || string.IsNullOrEmpty(jobId))
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotifProcessing, BuildProcessingNotification(), ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(NotifProcessing, BuildProcessingNotification());
        }

        _cts = new CancellationTokenSource();
        _ = PollAsync(runId, jobId, _cts.Token);

        return StartCommandResult.Sticky;
    }

    private Notification BuildProcessingNotification()
    {
        var tapIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        var pi = PendingIntent.GetActivity(
            this, 0, tapIntent,
            GetPendingIntentFlags());

        return CreateNotificationBuilder(ChannelProcessing)
            .SetContentTitle("Processing schedule…")
            .SetContentText("This will take a couple minutes.")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(pi)
            .SetOngoing(true)
            .Build()!;
    }

    private async Task PollAsync(int runId, string jobId, CancellationToken ct)
    {
        var services  = IPlatformApplication.Current!.Services;
        var apiClient = services.GetRequiredService<ApiClient>();
        var db        = services.GetRequiredService<ScheduleHistoryDb>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);

                var status = await apiClient.GetJobStatusAsync(jobId, ct);
                if (status is null) continue; // network hiccup — keep trying

                if (status.Status == "done")
                {
                    var result = await apiClient.GetJobResultAsync(jobId, ct);
                    System.Diagnostics.Debug.WriteLine(
                        $"[PollAsync] jobId={jobId} result={(result is null ? "NULL" : $"{result.Shifts.Count} shifts")}");

                    if (result is not null)
                    {
                        // Read position opt-in so we set the right PositionState in BubblePersist format
                        var prefs    = await db.GetPreferencesAsync(ct);
                        var posState = prefs.PositionOptIn == true ? 0 : 2; // 0=Pending 2=Skipped

                        // Must match BubblePersist record layout so ConfirmationPage.DeserializeBubbles works
                        var shiftsJson = System.Text.Json.JsonSerializer.Serialize(
                            result.Shifts.Select(s => new
                            {
                                Employee          = s.Employee,
                                Date              = s.Date,
                                OriginalTimeRange = s.TimeRange,
                                DisplayTime       = s.TimeRange,
                                TimeState         = 0,       // Pending
                                PositionState     = posState,
                                BoundsX           = s.EstimatedBounds?.X,
                                BoundsY           = s.EstimatedBounds?.Y,
                                BoundsWidth       = s.EstimatedBounds?.Width,
                                BoundsHeight      = s.EstimatedBounds?.Height,
                            }));

                        System.Diagnostics.Debug.WriteLine(
                            $"[PollAsync] saving shiftsJson len={shiftsJson.Length} for runId={runId}");

                        await db.UpdateRunWithResultAsync(
                            runId, shiftsJson, result.ImageWidth, result.ImageHeight, ct);
                    }
                    CalendarParse.Services.JobEvents.NotifyJobFinished();
                    FireReadyNotification(runId);
                    break;
                }

                if (status.Status == "error")
                {
                    await db.UpdateRunStatusAsync(
                        runId,
                        RunStatus.Error,
                        errorMessage: status.Error,
                        ct: ct);
                    CalendarParse.Services.JobEvents.NotifyJobFinished();
                    FireErrorNotification(runId);
                    break;
                }
            }
        }
        catch (System.OperationCanceledException)
        {
            // Service stopped — normal shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[JobPollingForegroundService] Unhandled error: {ex.Message}");
            try
            {
                var errorDb = IPlatformApplication.Current!.Services
                    .GetRequiredService<ScheduleHistoryDb>();
                await errorDb.UpdateRunStatusAsync(runId, RunStatus.Error, ex.Message);
            }
            catch { /* best-effort */ }
            FireErrorNotification(runId);
        }
        finally
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                StopForeground(StopForegroundFlags.Remove);
            }
            else
            {
#pragma warning disable CA1422
                StopForeground(true);
#pragma warning restore CA1422
            }
            StopSelf();
        }
    }

    private void FireReadyNotification(int runId)
    {
        var tapIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        var pi = PendingIntent.GetActivity(
            this, runId, tapIntent,
            GetPendingIntentFlags());

        var notif = CreateNotificationBuilder(ChannelReady)
            .SetContentTitle("Schedule ready!")
            .SetContentText("Tap to review your shifts.")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(pi)
            .SetAutoCancel(true)
            .Build()!;

        var mgr = GetSystemService(NotificationService) as NotificationManager;
        mgr?.Notify(NotifReadyBase + runId, notif);
    }

    private void FireErrorNotification(int runId)
    {
        var notif = CreateNotificationBuilder(ChannelReady)
            .SetContentTitle("Schedule processing failed")
            .SetContentText("Open History to retry.")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetAutoCancel(true)
            .Build()!;

        var mgr = GetSystemService(NotificationService) as NotificationManager;
        mgr?.Notify(NotifReadyBase + runId, notif);
    }

    public override void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnDestroy();
    }

    private Notification.Builder CreateNotificationBuilder(string channelId)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            return new Notification.Builder(this, channelId);

        return new Notification.Builder(this);
    }

    private static PendingIntentFlags GetPendingIntentFlags()
    {
        var flags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
            flags |= PendingIntentFlags.Immutable;

        return flags;
    }
}

/// <summary>
/// MAUI-side <see cref="IJobPollingService"/> implementation that starts/stops
/// the <see cref="JobPollingForegroundService"/> via Android Intents.
/// </summary>
public class AndroidJobPollingService : IJobPollingService
{
    public void StartPolling(int runId, string jobId)
    {
        var ctx    = global::Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(JobPollingForegroundService));
        intent.PutExtra(JobPollingForegroundService.ExtraRunId, runId);
        intent.PutExtra(JobPollingForegroundService.ExtraJobId, jobId);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            ctx.StartForegroundService(intent);
        }
        else
        {
            ctx.StartService(intent);
        }
    }

    public void StopAll()
    {
        var ctx    = global::Android.App.Application.Context;
        var intent = new Intent(ctx, typeof(JobPollingForegroundService));
        ctx.StopService(intent);
    }
}
#endif
