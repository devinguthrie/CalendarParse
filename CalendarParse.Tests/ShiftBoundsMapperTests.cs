using CalendarParse.Models;
using CalendarParse.Services;

namespace CalendarParse.Tests;

public class ShiftBoundsMapperTests
{
    private static CalendarData OneShift(string date, string shift = "9:00-5:00") => new()
    {
        Month     = "January",
        Year      = 2026,
        Employees = [new() { Name = "Alice", Shifts = [new() { Date = date, Shift = shift }] }]
    };

    [Fact]
    public void Map_DateToTuesday_UsesColumnIndex2()
    {
        // 2026-01-06 is a Tuesday → DayOfWeek.Tuesday == 2
        var data = OneShift("2026-01-06");
        var positions = new CellPositionsSnapshot
        {
            EstimatedRowHeightPx = 40,
            Columns = { [2] = new ColSnapshot { XStart = 100, XEnd = 200 } },
            Rows    = { ["Alice"] = new RowSnapshot { CellCenterY = 300 } },
        };

        var result = ShiftBoundsMapper.Map(data, positions);

        Assert.Single(result);
        var bounds = result[0].EstimatedBounds;
        Assert.NotNull(bounds);
        Assert.Equal(100, bounds.X);
        Assert.Equal(200 - 100, bounds.Width);
    }

    [Fact]
    public void Map_UnknownDay_ProducesNullBounds()
    {
        // No column entry for day index 2 (Tuesday)
        var data = OneShift("2026-01-06");
        var positions = new CellPositionsSnapshot
        {
            EstimatedRowHeightPx = 40,
            Columns = new Dictionary<int, ColSnapshot>(), // empty
            Rows    = { ["Alice"] = new RowSnapshot { CellCenterY = 300 } },
        };

        var result = ShiftBoundsMapper.Map(data, positions);

        Assert.Single(result);
        Assert.Null(result[0].EstimatedBounds);
    }

    [Fact]
    public void Map_NullPositions_ProducesShiftsWithNullBounds()
    {
        var data   = OneShift("2026-01-06");
        var result = ShiftBoundsMapper.Map(data, null);

        Assert.Single(result);
        Assert.Equal("Alice",     result[0].Employee);
        Assert.Equal("9:00-5:00", result[0].TimeRange);
        Assert.Null(result[0].EstimatedBounds);
    }

    [Fact]
    public void Map_CaseInsensitiveEmployeeLookup()
    {
        // Row key "ALICE" should match employee name "Alice"
        var data = OneShift("2026-01-06");
        var positions = new CellPositionsSnapshot
        {
            EstimatedRowHeightPx = 40,
            Columns = { [2] = new ColSnapshot { XStart = 50, XEnd = 150 } },
            Rows    = { ["ALICE"] = new RowSnapshot { CellCenterY = 200 } },
        };

        var result = ShiftBoundsMapper.Map(data, positions);

        Assert.NotNull(result[0].EstimatedBounds);
    }

    [Fact]
    public void Map_BoundsY_ClampedToZero()
    {
        // CellCenterY < halfRow should not produce negative Y
        var data = OneShift("2026-01-06");
        var positions = new CellPositionsSnapshot
        {
            EstimatedRowHeightPx = 100,
            Columns = { [2] = new ColSnapshot { XStart = 0, XEnd = 100 } },
            Rows    = { ["Alice"] = new RowSnapshot { CellCenterY = 10 } }, // 10 < halfRow 50
        };

        var result = ShiftBoundsMapper.Map(data, positions);
        Assert.Equal(0, result[0].EstimatedBounds!.Y);
    }
}
