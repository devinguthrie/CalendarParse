using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.Notification;
using CalendarParse.Data;
using CalendarParse.Services;

namespace CalendarParse.Platforms.Android;

/// <summary>
/// NotificationListenerService implementation that watches notifications from a
/// configured app + contact and raises <see cref="ISmsMonitorService.NotificationReceived"/>
/// when a match is found.
///
/// Flow:
///   Boss posts image in messaging app
///     → Android delivers notification
///     → OnNotificationPosted fires here
///     → Filter: package + sender name match
///     → Quiet hours check (hold if inside window)
///     → Raise NotificationReceived on UI thread
///     → App shows "Schedule image from Boss — ready to process?" notification
/// </summary>
[Service(
    Label     = "CalendarParse Notification Monitor",
    Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
    Exported  = true)]
[IntentFilter(["android.service.notification.NotificationListenerService"])]
public class AndroidNotificationMonitor
    : NotificationListenerService, ISmsMonitorService
{
    // Singleton instance — Android binds this service; MAUI DI grabs the reference.
    public static AndroidNotificationMonitor? Instance { get; private set; }

    /// <summary>
    /// Fires on the UI thread when Android binds the service (i.e. when Instance becomes non-null).
    /// Subscribe to this to wire up NotificationReceived without a polling loop.
    /// </summary>
    public static event Action<AndroidNotificationMonitor>? InstanceReady;

    public event EventHandler<NotificationReceivedEventArgs>? NotificationReceived;

    public string? WatchedPackage    { get; set; }
    public string? WatchedSenderName { get; set; }
    public bool    IsListening       { get; private set; }

    // Injected after binding via SetDependencies()
    private ScheduleHistoryDb? _db;

    public void SetDependencies(ScheduleHistoryDb db) => _db = db;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        IsListening = true;
        MainThread.BeginInvokeOnMainThread(() => InstanceReady?.Invoke(this));
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        Instance    = null;
        IsListening = false;
    }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        IsListening = true;
    }

    public override void OnListenerDisconnected()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            base.OnListenerDisconnected();
        IsListening = false;
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn is null) return;

        // Package filter
        if (!string.IsNullOrEmpty(WatchedPackage)
            && sbn.PackageName != WatchedPackage)
            return;

        // Sender name filter (match against notification title or text)
        var extras = sbn.Notification?.Extras;
        if (!string.IsNullOrEmpty(WatchedSenderName) && extras is not null)
        {
            var title = extras.GetString(Notification.ExtraTitle) ?? string.Empty;
            var text  = extras.GetString(Notification.ExtraText)  ?? string.Empty;
            if (!title.Contains(WatchedSenderName, StringComparison.OrdinalIgnoreCase)
                && !text.Contains(WatchedSenderName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var args = new NotificationReceivedEventArgs
        {
            SenderName    = extras?.GetString(Notification.ExtraTitle) ?? string.Empty,
            SourcePackage = sbn.PackageName ?? string.Empty,
            ReceivedAt    = DateTime.Now,
        };

        // Quiet hours check — fire and forget; raise on UI thread after delay if needed
        _ = CheckQuietHoursAndRaiseAsync(args);
    }

    private async Task CheckQuietHoursAndRaiseAsync(NotificationReceivedEventArgs args)
    {
        if (_db is not null)
        {
            try
            {
                var prefs = await _db.GetPreferencesAsync();
                if (prefs.QuietHoursEnabled && IsInsideQuietWindow(prefs))
                {
                    // Hold until the quiet window ends, then raise
                    var delay = TimeUntilWindowEnds(prefs);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay);
                }
            }
            catch
            {
                // DB failure — raise immediately rather than drop the event
            }
        }

        MainThread.BeginInvokeOnMainThread(() =>
            NotificationReceived?.Invoke(this, args));
    }

    private static bool IsInsideQuietWindow(AppPreferences prefs)
    {
        var now   = TimeOnly.FromDateTime(DateTime.Now);
        var start = prefs.QuietHoursStart;
        var end   = prefs.QuietHoursEnd;

        // Window may span midnight (e.g. 22:00–07:00)
        return start <= end
            ? now >= start && now < end          // same-day window
            : now >= start || now < end;         // midnight-spanning window
    }

    private static TimeSpan TimeUntilWindowEnds(AppPreferences prefs)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var end = prefs.QuietHoursEnd;
        var endDt = DateTime.Today.Add(end.ToTimeSpan());
        if (endDt <= DateTime.Now) endDt = endDt.AddDays(1);
        return endDt - DateTime.Now;
    }
}
