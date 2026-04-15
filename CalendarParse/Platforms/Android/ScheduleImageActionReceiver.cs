#if ANDROID
using Android.App;
using Android.Content;
using CalendarParse;  // MainActivity lives in the root CalendarParse namespace

namespace CalendarParse.Platforms.Android;

/// <summary>
/// Handles the Yes / No action buttons on the "Schedule image received" notification.
///
/// Yes → dismisses the notification, then re-launches the app with an auto-process intent
///       so MainActivity can load the image bytes into MainPage and submit automatically.
/// No  → dismisses the notification and deletes the temp image file.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public class ScheduleImageActionReceiver : BroadcastReceiver
{
    internal const string ActionYes    = "calendarparse.action.MONITOR_YES";
    internal const string ActionNo     = "calendarparse.action.MONITOR_NO";
    internal const string ExtraNotifId = "notif_id";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;

        var notifId  = intent.GetIntExtra(ExtraNotifId, -1);
        var tempPath = intent.GetStringExtra(MainActivity.ExtraMonitorImagePath);

        System.Diagnostics.Debug.WriteLine(
            $"[ScheduleImageReceiver] action='{intent.Action}' notifId={notifId} tempPath='{tempPath}'");

        // Dismiss the prompt notification
        if (notifId >= 0)
        {
            var mgr = context.GetSystemService(Context.NotificationService) as NotificationManager;
            mgr?.Cancel(notifId);
        }

        if (intent.Action == ActionNo)
        {
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                File.Delete(tempPath);
            System.Diagnostics.Debug.WriteLine("[ScheduleImageReceiver] No — dismissed and cleaned up");
            return;
        }

        if (intent.Action == ActionYes)
        {
            System.Diagnostics.Debug.WriteLine("[ScheduleImageReceiver] Yes — launching auto-process");
            var launchIntent = context.PackageManager
                ?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
            if (launchIntent is null) return;

            launchIntent.SetAction(MainActivity.ActionAutoProcessMonitorImage);
            launchIntent.PutExtra(MainActivity.ExtraMonitorImagePath, tempPath);
            launchIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask);
            context.StartActivity(launchIntent);
        }
    }
}
#endif
