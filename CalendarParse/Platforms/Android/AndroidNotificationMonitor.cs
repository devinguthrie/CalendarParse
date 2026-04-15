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

        // Restore persisted config so the filter works immediately after a service rebind
        // or device restart, without needing the user to re-save.
        WatchedPackage    = Preferences.Get("monitor_watched_package", (string?)null);
        var sender        = Preferences.Get("monitor_watched_sender",  (string?)null);
        WatchedSenderName = string.IsNullOrEmpty(sender) ? null : sender;

        System.Diagnostics.Debug.WriteLine(
            $"[NotifMonitor] OnCreate — WatchedPackage='{WatchedPackage ?? "<none>"}'" +
            $" WatchedSenderName='{WatchedSenderName ?? "<none>"}'");

        MainThread.BeginInvokeOnMainThread(() => InstanceReady?.Invoke(this));
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        System.Diagnostics.Debug.WriteLine("[NotifMonitor] OnDestroy — listener unbound");
        Instance    = null;
        IsListening = false;
    }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        IsListening = true;
        System.Diagnostics.Debug.WriteLine("[NotifMonitor] OnListenerConnected — IsListening=true");
    }

    public override void OnListenerDisconnected()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            base.OnListenerDisconnected();
        IsListening = false;
        System.Diagnostics.Debug.WriteLine("[NotifMonitor] OnListenerDisconnected — IsListening=false");
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn is null) return;

        var extras = sbn.Notification?.Extras;
        var rawTitle   = extras?.GetString(Notification.ExtraTitle)       ?? string.Empty;
        var rawText    = extras?.GetString(Notification.ExtraText)        ?? string.Empty;
        var rawSubText = extras?.GetString(Notification.ExtraSubText)     ?? string.Empty;
        var rawInfo    = extras?.GetString(Notification.ExtraInfoText)    ?? string.Empty;
        var rawSummary = extras?.GetString(Notification.ExtraSummaryText) ?? string.Empty;
        var isGroupSummary = (sbn.Notification?.Flags & NotificationFlags.GroupSummary) != 0;
        var hasBigPicture  = extras?.ContainsKey(Notification.ExtraPicture) == true;

        System.Diagnostics.Debug.WriteLine(
            $"[NotifMonitor] OnNotificationPosted" +
            $" pkg='{sbn.PackageName}'" +
            $" key='{sbn.Key}'" +
            $" groupKey='{sbn.Notification?.Group ?? "<none>"}'" +
            $" isGroupSummary={isGroupSummary}" +
            $" hasBigPicture={hasBigPicture}" +
            $" title='{rawTitle}'" +
            $" text='{rawText}'" +
            $" subText='{rawSubText}'" +
            $" infoText='{rawInfo}'" +
            $" summaryText='{rawSummary}'");

        // Dump all extras keys so we can see the full structure of picture notifications
        if (extras is not null)
        {
            var keys = extras.KeySet();
            if (keys is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NotifMonitor]   extras keys: {string.Join(", ", keys)}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[NotifMonitor]   current filter — WatchedPackage='{WatchedPackage ?? "<none>"}'" +
            $" WatchedSenderName='{WatchedSenderName ?? "<none>"}'" +
            $" IsListening={IsListening}");

        // Package filter
        if (!string.IsNullOrEmpty(WatchedPackage)
            && sbn.PackageName != WatchedPackage)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NotifMonitor] SKIP — package '{sbn.PackageName}'" +
                $" != watched '{WatchedPackage}'");
            return;
        }

        // Sender name filter — check title, text, subText, infoText, and summaryText.
        // NotificationSenderMatcher.Matches also handles phone-number normalisation so
        // that "+19514563559" matches "(951) 456-3559" in the notification text.
        if (!string.IsNullOrEmpty(WatchedSenderName) && extras is not null)
        {
            var allText = $"{rawTitle} {rawText} {rawSubText} {rawInfo} {rawSummary}";
            var matched = NotificationSenderMatcher.Matches(allText, WatchedSenderName);

            System.Diagnostics.Debug.WriteLine(
                $"[NotifMonitor] sender check — matched={matched}" +
                $" watchedDigits='{NotificationSenderMatcher.DigitsOnly(WatchedSenderName)}'");

            if (!matched)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NotifMonitor] SKIP — sender '{WatchedSenderName}' not found in any extras field." +
                    $" title='{rawTitle}' text='{rawText}' subText='{rawSubText}'" +
                    $" infoText='{rawInfo}' summaryText='{rawSummary}'");
                return;
            }
        }
        else if (string.IsNullOrEmpty(WatchedSenderName))
        {
            System.Diagnostics.Debug.WriteLine(
                "[NotifMonitor] NOTE — WatchedSenderName is empty, accepting all senders for this package");
        }

        System.Diagnostics.Debug.WriteLine(
            $"[NotifMonitor] MATCH — raising NotificationReceived for sender='{rawTitle}'" +
            $" hasBigPicture={hasBigPicture}" +
            $" subscriberCount={(NotificationReceived is null ? 0 : NotificationReceived.GetInvocationList().Length)}");

        // Try to extract the full MMS image from the MessagingStyle message URIs.
        // android.reduced.images=true only strips embedded Bitmaps — content:// URIs survive.
        var imageBytes = extras is not null ? ExtractLatestMmsImageBytes(extras) : null;
        System.Diagnostics.Debug.WriteLine(
            $"[NotifMonitor] image extraction — bytes={imageBytes?.Length.ToString() ?? "null"}");

        var args = new NotificationReceivedEventArgs
        {
            SenderName    = rawTitle,
            SourcePackage = sbn.PackageName ?? string.Empty,
            ReceivedAt    = DateTime.Now,
            ImageBytes    = imageBytes,
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
                    System.Diagnostics.Debug.WriteLine(
                        $"[NotifMonitor] QUIET HOURS — holding event for {delay.TotalMinutes:F1} min");
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                // DB failure — raise immediately rather than drop the event
                System.Diagnostics.Debug.WriteLine(
                    $"[NotifMonitor] DB error during quiet hours check (raising immediately): {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[NotifMonitor] Invoking NotificationReceived on UI thread for sender='{args.SenderName}'");
        MainThread.BeginInvokeOnMainThread(() =>
            NotificationReceived?.Invoke(this, args));
    }

    private static bool IsInsideQuietWindow(AppPreferences prefs)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return WeekDiff.IsInQuietWindow(now, prefs.QuietHoursStart, prefs.QuietHoursEnd);
    }

    private static TimeSpan TimeUntilWindowEnds(AppPreferences prefs)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var end = prefs.QuietHoursEnd;
        var endDt = DateTime.Today.Add(end.ToTimeSpan());
        if (endDt <= DateTime.Now) endDt = endDt.AddDays(1);
        return endDt - DateTime.Now;
    }

    /// <summary>
    /// Iterates the MessagingStyle <c>android.messages</c> Parcelable array in reverse
    /// (newest first) and returns the bytes of the first image/* attachment whose content
    /// URI can be opened via ContentResolver.
    ///
    /// Each message bundle uses the keys defined by
    /// <c>android.app.Notification.MessagingStyle.Message</c>:
    ///   "type" → MIME type string  (e.g. "image/jpeg")
    ///   "uri"  → Android.Net.Uri pointing to the MMS content store
    ///
    /// Requires READ_SMS (already in the manifest) to open content://mms/part/* URIs.
    /// Returns null when no image is found or on any error.
    /// </summary>
    private byte[]? ExtractLatestMmsImageBytes(Bundle extras)
    {
#pragma warning disable CA1422  // GetParcelableArray(string) deprecated in API 33; typed overload not available in binding
        IParcelable[]? messages;
        try   { messages = extras.GetParcelableArray("android.messages"); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NotifMonitor] GetParcelableArray failed: {ex.Message}");
            return null;
        }
#pragma warning restore CA1422

        if (messages is null || messages.Length == 0)
        {
            System.Diagnostics.Debug.WriteLine("[NotifMonitor] android.messages absent or empty");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[NotifMonitor] android.messages has {messages.Length} entries — scanning for image");

        // Messages are chronological; iterate reverse so we try the newest image first.
        for (int i = messages.Length - 1; i >= 0; i--)
        {
            if (messages[i] is not Bundle msgBundle) continue;

            var mimeType = msgBundle.GetString("type");
            if (string.IsNullOrEmpty(mimeType)
                || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[NotifMonitor]   msg[{i}] type='{mimeType ?? "<none>"}' — not image, skip");
                continue;
            }

#pragma warning disable CA1422  // GetParcelable(string) deprecated in API 33
            var imageUri = msgBundle.GetParcelable("uri") as global::Android.Net.Uri;
#pragma warning restore CA1422

            System.Diagnostics.Debug.WriteLine($"[NotifMonitor]   msg[{i}] type='{mimeType}' uri='{imageUri}'");

            if (imageUri is null) continue;

            try
            {
                using var stream = ContentResolver?.OpenInputStream(imageUri);
                if (stream is null)
                {
                    System.Diagnostics.Debug.WriteLine($"[NotifMonitor]   OpenInputStream returned null for {imageUri}");
                    continue;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                System.Diagnostics.Debug.WriteLine($"[NotifMonitor]   Read {bytes.Length:N0} bytes from {imageUri}");

                // Sanity check — ignore empty / obviously truncated payloads
                if (bytes.Length > 1024) return bytes;

                System.Diagnostics.Debug.WriteLine($"[NotifMonitor]   Payload too small ({bytes.Length} bytes) — skipping");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotifMonitor]   Error reading {imageUri}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine("[NotifMonitor] No readable image found in android.messages");
        return null;
    }
}
