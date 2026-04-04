using CalendarParse.Services;

namespace CalendarParse.Tests;

public class WeekDiffTests
{
    [Fact]
    public void Format_PositiveDiff_ShowsPlusSign()
    {
        var result = WeekDiff.Format(600, 480); // 10 hrs vs 8 hrs
        Assert.StartsWith("+", result);
        Assert.Contains("2.0 hrs", result);
    }

    [Fact]
    public void Format_NullPrior_ReturnsDash()
    {
        Assert.Equal("—", WeekDiff.Format(600, null));
    }

    [Fact]
    public void MondayOf_Wednesday_ReturnsPriorMonday()
    {
        var wednesday = new DateTime(2026, 1, 7); // Wednesday
        var monday    = WeekDiff.MondayOf(wednesday);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.Equal(new DateTime(2026, 1, 5), monday);
    }

    [Fact]
    public void MondayOf_Monday_ReturnsSameDay()
    {
        var monday = new DateTime(2026, 1, 5);
        Assert.Equal(monday, WeekDiff.MondayOf(monday));
    }
}

public class QuietHoursTests
{
    [Theory]
    [InlineData(22, 0, 7, 0,  23, 0, true)]   // 23:00 inside 22:00–07:00
    [InlineData(22, 0, 7, 0,   6, 0, true)]   //  06:00 inside 22:00–07:00
    [InlineData(22, 0, 7, 0,  10, 0, false)]  // 10:00 outside 22:00–07:00
    [InlineData( 8, 0,12, 0,   9, 0, true)]   //  09:00 inside same-day 08:00–12:00
    [InlineData( 8, 0,12, 0,  13, 0, false)]  // 13:00 outside same-day 08:00–12:00
    public void IsInQuietWindow_VariousScenarios(
        int sh, int sm, int eh, int em, int th, int tm, bool expected)
    {
        var start  = new TimeOnly(sh, sm);
        var end    = new TimeOnly(eh, em);
        var time   = new TimeOnly(th, tm);
        Assert.Equal(expected, WeekDiff.IsInQuietWindow(time, start, end));
    }
}
