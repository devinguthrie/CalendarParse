using CalendarParse.Models;
using Xunit;

namespace CalendarParse.Tests;

public class ShiftDataTests
{
    [Fact]
    public void ShiftData_Defaults_AreEmpty()
    {
        var s = new ShiftData();
        Assert.Equal(string.Empty, s.Employee);
        Assert.Equal(string.Empty, s.Date);
        Assert.Equal(string.Empty, s.TimeRange);
        Assert.Null(s.EstimatedBounds);
    }

    [Fact]
    public void ShiftData_WithBounds_SetsProperties()
    {
        var b = new BoundingBox { X = 1, Y = 2, Width = 3, Height = 4 };
        var s = new ShiftData { Employee = "Bob", Date = "2026-04-04", TimeRange = "8-4", EstimatedBounds = b };
        Assert.Equal("Bob", s.Employee);
        Assert.Equal("2026-04-04", s.Date);
        Assert.Equal("8-4", s.TimeRange);
        Assert.NotNull(s.EstimatedBounds);
        Assert.Equal(1, s.EstimatedBounds!.X);
    }

    [Fact]
    public void ShiftData_NullBounds_DoesNotThrow()
    {
        var s = new ShiftData { EstimatedBounds = null };
        Assert.Null(s.EstimatedBounds);
    }
}
