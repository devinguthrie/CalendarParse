namespace CalendarParse.Services;

/// <summary>Installed app entry returned by the app picker.</summary>
public sealed record MessagingAppInfo(
    string PackageName,
    string Label,
    byte[]? IconPng,            // Raw PNG bytes for the launcher icon; null if unavailable
    bool   SupportsSmsThreads); // True when the app handles the native Android SMS stack

/// <summary>One SMS/MMS conversation thread.</summary>
public sealed record SmsThreadInfo(
    long   ThreadId,
    string DisplayName, // Contact display name, or raw phone number if contacts unavailable
    string Snippet,     // Last message preview (truncated to 80 chars)
    string Address);    // Raw phone number / address

/// <summary>Contact entry returned by the contact autocomplete search.</summary>
public sealed record ContactInfo(
    string DisplayName,
    string PhoneNumber);

/// <summary>
/// Provides data for the messaging-app picker, SMS thread picker, and contact autocomplete.
/// Implemented only on Android; all call sites guard with #if ANDROID.
/// </summary>
public interface IMessagingAppPickerService
{
    /// <summary>
    /// Returns all messaging apps installed on the device, sorted by label.
    /// Includes native SMS apps and popular third-party apps (WhatsApp, Telegram, etc.).
    /// </summary>
    Task<IReadOnlyList<MessagingAppInfo>> GetMessagingAppsAsync();

    /// <summary>
    /// Returns recent SMS/MMS threads from the native message store (up to 50).
    /// Caller must ensure READ_SMS and READ_CONTACTS permissions are granted.
    /// </summary>
    Task<IReadOnlyList<SmsThreadInfo>> GetSmsThreadsAsync();

    /// <summary>
    /// Returns up to 20 contacts whose display name contains <paramref name="query"/>.
    /// Caller must ensure READ_CONTACTS permission is granted.
    /// </summary>
    Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(string query, CancellationToken ct = default);
}
