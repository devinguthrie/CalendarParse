using CalendarParse.Core.Services;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for TimeFormatHelper — formatting and parsing shift times
/// and the seeding logic for the start/end TimePicker controls.
/// </summary>
public class TimeFormatHelperTests
{
    // ── FormatTimeRange ───────────────────────────────────────────────────────

    [Fact]
    public void FormatTimeRange_Typical_CorrectFormat()
    {
        var result = TimeFormatHelper.FormatTimeRange(
            TimeSpan.FromHours(9), TimeSpan.FromHours(17));
        Assert.Equal("9:00-5:00", result);
    }

    [Fact]
    public void FormatTimeRange_WithMinutes_ZeroPadsMinutes()
    {
        var result = TimeFormatHelper.FormatTimeRange(
            new TimeSpan(9, 5, 0), new TimeSpan(17, 30, 0));
        Assert.Equal("9:05-5:30", result);
    }

    [Fact]
    public void FormatTimeRange_Midnight_ShowsTwelve()
    {
        // 0:00 → 12:00 in 12-hour display
        var result = TimeFormatHelper.FormatTimeRange(
            TimeSpan.Zero, new TimeSpan(12, 0, 0));
        Assert.Equal("12:00-12:00", result);
    }

    [Fact]
    public void FormatTimeRange_NoonStart_ShowsTwelve()
    {
        var result = TimeFormatHelper.FormatTimeRange(
            new TimeSpan(12, 0, 0), new TimeSpan(20, 0, 0));
        Assert.Equal("12:00-8:00", result);
    }

    [Fact]
    public void FormatTimeRange_1pm_ShowsOne()
    {
        var result = TimeFormatHelper.FormatTimeRange(
            new TimeSpan(13, 0, 0), new TimeSpan(21, 0, 0));
        Assert.Equal("1:00-9:00", result);
    }

    [Fact]
    public void FormatTimeRange_SameStartAndEnd_Allowed()
    {
        var t = new TimeSpan(10, 30, 0);
        var result = TimeFormatHelper.FormatTimeRange(t, t);
        Assert.Equal("10:30-10:30", result);
    }

    // ── TryParseShiftTime ─────────────────────────────────────────────────────

    [Fact]
    public void TryParseShiftTime_HHmm_Parses()
    {
        Assert.True(TimeFormatHelper.TryParseShiftTime("9:00", out var result));
        Assert.Equal(new TimeSpan(9, 0, 0), result);
    }

    [Fact]
    public void TryParseShiftTime_TwoDigitHour_Parses()
    {
        Assert.True(TimeFormatHelper.TryParseShiftTime("10:30", out var result));
        Assert.Equal(new TimeSpan(10, 30, 0), result);
    }

    [Fact]
    public void TryParseShiftTime_WithLeadingWhitespace_Parses()
    {
        Assert.True(TimeFormatHelper.TryParseShiftTime("  9:00  ", out var result));
        Assert.Equal(new TimeSpan(9, 0, 0), result);
    }

    [Fact]
    public void TryParseShiftTime_Empty_ReturnsFalse()
    {
        Assert.False(TimeFormatHelper.TryParseShiftTime("", out _));
    }

    [Fact]
    public void TryParseShiftTime_Garbage_ReturnsFalse()
    {
        Assert.False(TimeFormatHelper.TryParseShiftTime("not a time", out _));
    }

    [Fact]
    public void TryParseShiftTime_OnlyHours_Parses()
    {
        // DateTime.TryParse fallback handles "9 AM" and similar inputs
        Assert.True(TimeFormatHelper.TryParseShiftTime("9:00 AM", out var result));
        Assert.Equal(new TimeSpan(9, 0, 0), result);
    }

    // ── SeedTimePickers ───────────────────────────────────────────────────────

    [Fact]
    public void SeedTimePickers_Null_ReturnsDefaults()
    {
        var (start, end) = TimeFormatHelper.SeedTimePickers(null!);
        Assert.Equal(TimeSpan.FromHours(9),  start);
        Assert.Equal(TimeSpan.FromHours(17), end);
    }

    [Fact]
    public void SeedTimePickers_Empty_ReturnsDefaults()
    {
        var (start, end) = TimeFormatHelper.SeedTimePickers(string.Empty);
        Assert.Equal(TimeSpan.FromHours(9),  start);
        Assert.Equal(TimeSpan.FromHours(17), end);
    }

    [Fact]
    public void SeedTimePickers_WhitespaceOnly_ReturnsDefaults()
    {
        var (start, end) = TimeFormatHelper.SeedTimePickers("   ");
        Assert.Equal(TimeSpan.FromHours(9),  start);
        Assert.Equal(TimeSpan.FromHours(17), end);
    }

    [Fact]
    public void SeedTimePickers_StartOnly_SetsStartLeavesEndDefault()
    {
        var (start, end) = TimeFormatHelper.SeedTimePickers("10:30");
        Assert.Equal(new TimeSpan(10, 30, 0), start);
        Assert.Equal(TimeSpan.FromHours(17),  end);
    }

    [Fact]
    public void SeedTimePickers_StartAndEnd_BothSet()
    {
        // Cross-12 detection: start(9) > end(5), both < 12h → apply default AM/PM rule.
        // With crossTwelveStartIsPm=false (default): start stays AM, end gets +12h (PM).
        var (start, end) = TimeFormatHelper.SeedTimePickers("9:00-5:00");
        Assert.Equal(new TimeSpan(9,  0, 0), start);  // 9:00 AM
        Assert.Equal(new TimeSpan(17, 0, 0), end);    // 5:00 PM
    }

    [Fact]
    public void SeedTimePickers_RoundTripsWithFormatTimeRange()
    {
        // FormatTimeRange produces a 12-hour string ("8:30-4:45").
        // Cross-12 detection fires (8 > 4, both < 12) and adds 12h to end → round-trip is correct.
        var originalStart = new TimeSpan(8, 30, 0);
        var originalEnd   = new TimeSpan(16, 45, 0);
        var formatted     = TimeFormatHelper.FormatTimeRange(originalStart, originalEnd);
        var (start, end)  = TimeFormatHelper.SeedTimePickers(formatted);

        // Both start (8:30 AM) and end (4:45 PM) round-trip correctly with cross-12 applied.
        Assert.True(Math.Abs((start - originalStart).TotalMinutes) < 1.0,
            $"Start drift: {start} vs {originalStart}");
        Assert.True(Math.Abs((end - originalEnd).TotalMinutes) < 1.0,
            $"End drift: {end} vs {originalEnd}");
    }

    [Fact]
    public void SeedTimePickers_BlankTime_ReturnsDefaults()
    {
        // Blank/x shift times should fall back to picker defaults, not crash.
        var (start, end) = TimeFormatHelper.SeedTimePickers("x");
        Assert.Equal(TimeSpan.FromHours(9),  start);
        Assert.Equal(TimeSpan.FromHours(17), end);
    }

    [Fact]
    public void SeedTimePickers_WithExtraWhitespace_ParsesSuccessfully()
    {
        // Cross-12 detection: start(8) > end(4), both < 12h → end gets +12h (PM).
        var (start, end) = TimeFormatHelper.SeedTimePickers(" 8:00 - 4:00 ");
        Assert.Equal(new TimeSpan(8,  0, 0), start);  // 8:00 AM
        Assert.Equal(new TimeSpan(16, 0, 0), end);    // 4:00 PM
    }

    // ── FormatTimeRange + SeedTimePickers determinism ─────────────────────────

    [Theory]
    [InlineData(6, 0, 14, 0)]   // 6:00-2:00
    [InlineData(7, 30, 15, 30)] // 7:30-3:30
    [InlineData(12, 0, 20, 0)]  // 12:00-8:00
    [InlineData(0, 0, 8, 0)]    // midnight start
    public void FormatThenSeed_PreservesStartHour(int startH, int startM, int endH, int endM)
    {
        var formatted = TimeFormatHelper.FormatTimeRange(
            new TimeSpan(startH, startM, 0),
            new TimeSpan(endH, endM, 0));
        var (s, _) = TimeFormatHelper.SeedTimePickers(formatted);
        // Start hour must round-trip exactly (mod-12 only, since format is 12-hour)
        var orig12 = startH % 12 == 0 ? 12 : startH % 12;
        var parsed12 = s.Hours % 12 == 0 ? 12 : s.Hours % 12;
        Assert.Equal(orig12, parsed12);
    }
}
