namespace CalendarParse.Services;

// NotificationReceivedEventArgs lives in CalendarParse.Core (NotificationReceivedEventArgs.cs)
// so it is accessible from unit tests without taking a MAUI project reference.

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
