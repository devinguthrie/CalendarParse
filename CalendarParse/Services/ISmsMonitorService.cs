namespace CalendarParse.Services;

public class NotificationReceivedEventArgs : EventArgs
{
    /// <summary>Display name of the sender (contact or group name), as reported by the messaging app.</summary>
    public string SenderName { get; init; } = string.Empty;

    /// <summary>Package name of the app that sent the notification, e.g. "com.google.android.apps.messaging".</summary>
    public string SourcePackage { get; init; } = string.Empty;

    /// <summary>Timestamp the notification was received.</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// Abstraction over Android NotificationListenerService.
/// Raises <see cref="NotificationReceived"/> when a notification arrives from the
/// configured app + contact/group filter.
///
/// Future hook: swap implementation for different sources (Telegram, WhatsApp, etc.)
/// See TODOS.md — "Deeper NotifListener image extraction".
/// </summary>
public interface ISmsMonitorService
{
    /// <summary>Raised (on UI thread) when a matching notification is detected.</summary>
    event EventHandler<NotificationReceivedEventArgs> NotificationReceived;

    /// <summary>App package to monitor (e.g. "com.google.android.apps.messaging").</summary>
    string? WatchedPackage { get; set; }

    /// <summary>Sender display name to filter on. Null = any sender from the watched package.</summary>
    string? WatchedSenderName { get; set; }

    /// <summary>Whether the service is currently listening.</summary>
    bool IsListening { get; }
}
