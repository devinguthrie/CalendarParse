using CalendarParse.Core.Services;

namespace CalendarParse.Tests;

public class ConfirmedShiftSanitizerTests
{
    [Theory]
    [InlineData("", "Franny", "")]
    [InlineData("   ", "Franny", "")]
    [InlineData("Franny", "Franny", "")]
    [InlineData(" franny ", "Franny", "")]
    [InlineData("x", "Franny", "x")]
    [InlineData("12:00-8:30", "Franny", "12:00-8:30")]
    public void NormalizeTimeRange_ReturnsExpectedValue(string displayTime, string employeeName, string expected)
    {
        var actual = ConfirmedShiftSanitizer.NormalizeTimeRange(displayTime, employeeName);

        Assert.Equal(expected, actual);
    }
}