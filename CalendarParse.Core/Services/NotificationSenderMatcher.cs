namespace CalendarParse.Services;

/// <summary>
/// Pure helpers for the notification-monitor sender-name / phone-number filter.
///
/// Extracted from <c>AndroidNotificationMonitor</c> so the matching rules can be
/// unit-tested without Android runtime dependencies.
/// </summary>
public static class NotificationSenderMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="allNotificationText"/> contains
    /// the watched sender name.
    ///
    /// Two strategies are tried in order:
    /// <list type="number">
    ///   <item>Plain case-insensitive substring match.</item>
    ///   <item>
    ///     Phone-number normalisation: strip every non-ASCII-digit from both strings and
    ///     check whether the last 10 digits of <paramref name="watchedSender"/> appear
    ///     somewhere in the digit-only projection of the notification text.
    ///     Requires at least 7 digits after truncation to avoid spurious matches on very
    ///     short strings.
    ///   </item>
    /// </list>
    ///
    /// An empty or null <paramref name="watchedSender"/> is treated as a wildcard
    /// (always returns <see langword="true"/>).
    /// </summary>
    public static bool Matches(string allNotificationText, string? watchedSender)
    {
        if (string.IsNullOrEmpty(watchedSender))
            return true;

        // Strategy 1 — plain text
        if (allNotificationText.Contains(watchedSender, StringComparison.OrdinalIgnoreCase))
            return true;

        // Strategy 2 — phone number digit normalisation
        var watchedDigits = DigitsOnly(watchedSender);
        if (watchedDigits.Length < 7)
            return false; // too short to be a useful phone comparison

        var last10 = watchedDigits.Length >= 10 ? watchedDigits[^10..] : watchedDigits;
        var textDigits = DigitsOnly(allNotificationText);
        return textDigits.Contains(last10);
    }

    /// <summary>
    /// Returns a new string containing only the ASCII digit characters from <paramref name="s"/>.
    /// </summary>
    public static string DigitsOnly(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsAsciiDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
