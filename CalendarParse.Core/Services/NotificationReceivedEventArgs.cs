namespace CalendarParse.Services;

/// <summary>
/// Data carried when the notification monitor matches a notification.
/// Lives in CalendarParse.Core so it is accessible from both the MAUI platform
/// implementation and the unit-test project.
/// </summary>
public class NotificationReceivedEventArgs : EventArgs
{
    /// <summary>Display name of the sender (contact or group name), as reported by the messaging app.</summary>
    public string SenderName { get; init; } = string.Empty;

    /// <summary>Package name of the app that sent the notification, e.g. "com.google.android.apps.messaging".</summary>
    public string SourcePackage { get; init; } = string.Empty;

    /// <summary>Timestamp the notification was received.</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Full-resolution image bytes extracted from the MMS content URI in the notification's
    /// MessagingStyle messages. Null when the notification contained no image attachment,
    /// when android.reduced.images stripped the URI, or when ContentResolver access failed.
    /// When non-null the caller can skip the share-prompt and go straight to processing.
    /// </summary>
    public byte[]? ImageBytes { get; init; }
}
