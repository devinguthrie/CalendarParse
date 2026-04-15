using CalendarParse.Services;

namespace CalendarParse.Tests;

public class NotificationSenderMatcherTests
{
    // ── DigitsOnly ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("+19514563559", "19514563559")]
    [InlineData("(951) 456-3559", "9514563559")]
    [InlineData("John Smith",    "")]
    [InlineData("",              "")]
    [InlineData("555-867-5309",  "5558675309")]
    [InlineData("1 (800) 555-0100", "18005550100")]
    public void DigitsOnly_StripsNonDigits(string input, string expected)
    {
        Assert.Equal(expected, NotificationSenderMatcher.DigitsOnly(input));
    }

    // ── Matches — plain text ──────────────────────────────────────────────────

    [Fact]
    public void Matches_ExactSubstring_ReturnsTrue()
    {
        Assert.True(NotificationSenderMatcher.Matches("Boss sent a photo", "Boss"));
    }

    [Fact]
    public void Matches_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(NotificationSenderMatcher.Matches("BOSS sent a photo", "boss"));
    }

    [Fact]
    public void Matches_NotPresent_ReturnsFalse()
    {
        Assert.False(NotificationSenderMatcher.Matches("Alice sent a photo", "Bob"));
    }

    [Fact]
    public void Matches_NullWatched_AlwaysTrue()
    {
        Assert.True(NotificationSenderMatcher.Matches("anything here", null));
    }

    [Fact]
    public void Matches_EmptyWatched_AlwaysTrue()
    {
        Assert.True(NotificationSenderMatcher.Matches("anything here", ""));
    }

    [Fact]
    public void Matches_EmptyText_WithNonEmptyWatched_ReturnsFalse()
    {
        Assert.False(NotificationSenderMatcher.Matches("", "Boss"));
    }

    // ── Matches — phone number normalisation ──────────────────────────────────

    [Fact]
    public void Matches_E164_Vs_UsFormatted_ReturnsTrue()
    {
        // Stored as E.164 "+19514563559", displayed as "(951) 456-3559" in notification
        Assert.True(NotificationSenderMatcher.Matches("(951) 456-3559 sent a photo", "+19514563559"));
    }

    [Fact]
    public void Matches_E164_Vs_DashFormatted_ReturnsTrue()
    {
        Assert.True(NotificationSenderMatcher.Matches("951-456-3559 sent a photo", "+19514563559"));
    }

    [Fact]
    public void Matches_Ten_Vs_Eleven_Digits_ReturnsTrue()
    {
        // Stored 10-digit "9514563559" matches 11-digit "+19514563559" after last-10 truncation
        Assert.True(NotificationSenderMatcher.Matches("+1 (951) 456-3559 sent a photo", "9514563559"));
    }

    [Fact]
    public void Matches_WrongNumber_ReturnsFalse()
    {
        Assert.False(NotificationSenderMatcher.Matches("(555) 867-5309 sent a photo", "+19514563559"));
    }

    [Fact]
    public void Matches_ShortDigitString_DoesNotTriggerPhoneMatch()
    {
        // watchedSender "+1 2345" → plain text not in notification; digits "12345" = 5 chars < threshold 7
        // Should NOT match via phone logic
        Assert.False(NotificationSenderMatcher.Matches("order 98765 confirmed", "+1 2345"));
    }

    [Fact]
    public void Matches_ExactlySevenDigits_TriggersPhoneMatch()
    {
        // 7 digits — right at the threshold; should match
        Assert.True(NotificationSenderMatcher.Matches("number is 4563559 here", "4563559"));
    }

    [Fact]
    public void Matches_AllTextEmpty_PhoneWatched_ReturnsFalse()
    {
        Assert.False(NotificationSenderMatcher.Matches("", "+19514563559"));
    }

    // ── Matches — notification text spread across multiple fields ─────────────

    [Fact]
    public void Matches_NameInSubText_ReturnsTrue()
    {
        // allNotificationText is built as "$title $text $subText $infoText $summaryText"
        var combined = " MMS message from Boss";
        Assert.True(NotificationSenderMatcher.Matches(combined, "Boss"));
    }

    [Fact]
    public void Matches_PhoneInTitle_NameNotFound_ReturnsTrueViaPhoneMatch()
    {
        // Title contains formatted phone; stored watched sender is E.164
        var combined = "(951) 456-3559   ";
        Assert.True(NotificationSenderMatcher.Matches(combined, "+19514563559"));
    }
}
