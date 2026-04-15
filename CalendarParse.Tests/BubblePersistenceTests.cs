using CalendarParse.Core.Models;
using CalendarParse.Core.Services;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for BubblePersistenceService — JSON round-trip for all bubble states
/// and bounds configurations.  Failure here means resumed sessions will lose
/// confirmation progress or revert to wrong positions.
/// </summary>
public class BubblePersistenceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static BubblePersist Make(
        string employee      = "Alice",
        string date          = "2026-01-06",
        string originalTime  = "9:00-5:00",
        string displayTime   = "9:00-5:00",
        int    timeState     = 0,
        int    posState      = 2,   // Skipped
        int?   bx = null, int? by = null, int? bw = null, int? bh = null)
        => new(employee, date, originalTime, displayTime, timeState, posState, bx, by, bw, bh);

    private static void AssertEqual(BubblePersist expected, BubblePersist actual)
    {
        Assert.Equal(expected.Employee,        actual.Employee);
        Assert.Equal(expected.Date,            actual.Date);
        Assert.Equal(expected.OriginalTimeRange, actual.OriginalTimeRange);
        Assert.Equal(expected.DisplayTime,     actual.DisplayTime);
        Assert.Equal(expected.TimeState,       actual.TimeState);
        Assert.Equal(expected.PositionState,   actual.PositionState);
        Assert.Equal(expected.BoundsX,         actual.BoundsX);
        Assert.Equal(expected.BoundsY,         actual.BoundsY);
        Assert.Equal(expected.BoundsWidth,     actual.BoundsWidth);
        Assert.Equal(expected.BoundsHeight,    actual.BoundsHeight);
    }

    // ── Null / empty / malformed input ────────────────────────────────────────

    [Fact]
    public void Deserialize_Null_ReturnsEmptyList()
        => Assert.Empty(BubblePersistenceService.Deserialize(null));

    [Fact]
    public void Deserialize_Empty_ReturnsEmptyList()
        => Assert.Empty(BubblePersistenceService.Deserialize(string.Empty));

    [Fact]
    public void Deserialize_Whitespace_ReturnsEmptyList()
        => Assert.Empty(BubblePersistenceService.Deserialize("   "));

    [Fact]
    public void Deserialize_MalformedJson_ReturnsEmptyList()
        => Assert.Empty(BubblePersistenceService.Deserialize("not json at all {{{{"));

    [Fact]
    public void Deserialize_EmptyJsonArray_ReturnsEmptyList()
        => Assert.Empty(BubblePersistenceService.Deserialize("[]"));

    // ── Single bubble round-trips ─────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PendingBubble_NoBounds()
    {
        var b = Make(timeState: 0 /*Pending*/, posState: 2 /*Skipped*/);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Single(rt);
        AssertEqual(b, rt[0]);
    }

    [Fact]
    public void RoundTrip_ConfirmedBubble_WithBounds()
    {
        var b = Make(timeState: 2 /*Confirmed*/, posState: 1 /*Confirmed*/,
                     bx: 120, by: 340, bw: 200, bh: 55);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Single(rt);
        AssertEqual(b, rt[0]);
    }

    [Fact]
    public void RoundTrip_EditingTimeState_PreservesState()
    {
        var b = Make(timeState: 1 /*Editing*/);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Equal(1, rt[0].TimeState);
    }

    [Fact]
    public void RoundTrip_EditingPositionState_PreservesState()
    {
        var b = Make(posState: 3 /*Editing*/, bx: 50, by: 100, bw: 180, bh: 60);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Equal(3, rt[0].PositionState);
    }

    [Fact]
    public void RoundTrip_BoundsNull_StaysNull()
    {
        var b = Make(bx: null, by: null, bw: null, bh: null);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Null(rt[0].BoundsX);
        Assert.Null(rt[0].BoundsY);
        Assert.Null(rt[0].BoundsWidth);
        Assert.Null(rt[0].BoundsHeight);
    }

    [Fact]
    public void RoundTrip_BlankDisplayTime_PreservesBlank()
    {
        var b = Make(displayTime: string.Empty, timeState: 2);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Equal(string.Empty, rt[0].DisplayTime);
    }

    [Fact]
    public void RoundTrip_XDisplayTime_Preserved()
    {
        var b = Make(displayTime: "x", timeState: 2);
        var rt = BubblePersistenceService.Deserialize(BubblePersistenceService.Serialize([b]));
        Assert.Equal("x", rt[0].DisplayTime);
    }

    // ── Multiple bubbles ──────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_MultipleBubbles_OrderPreserved()
    {
        var bubbles = new[]
        {
            Make("Alice", "Mon", timeState: 2, posState: 1, bx: 50, by: 200, bw: 180, bh: 50),
            Make("Bob",   "Tue", timeState: 0, posState: 0),
            Make("Carol", "Wed", timeState: 2, posState: 2),
        };
        var rt = BubblePersistenceService.Deserialize(
                     BubblePersistenceService.Serialize(bubbles));

        Assert.Equal(3, rt.Count);
        AssertEqual(bubbles[0], rt[0]);
        AssertEqual(bubbles[1], rt[1]);
        AssertEqual(bubbles[2], rt[2]);
    }

    [Fact]
    public void RoundTrip_SevenDaySchedule_AllPreserved()
    {
        // Simulates a typical 7-day work week
        var bubbles = Enumerable.Range(0, 7).Select(i =>
            Make($"Employee{i}", $"2026-01-{i + 6:D2}",
                 timeState: i < 5 ? 2 : 0,
                 posState: i < 5 ? 1 : 2,
                 bx: i * 100, by: 300, bw: 180, bh: 50)).ToArray();

        var rt = BubblePersistenceService.Deserialize(
                     BubblePersistenceService.Serialize(bubbles));

        Assert.Equal(7, rt.Count);
        for (int i = 0; i < 7; i++)
            AssertEqual(bubbles[i], rt[i]);
    }

    // ── Case-insensitive deserialization ─────────────────────────────────────

    [Fact]
    public void Deserialize_UpperCasePropertyNames_StillParses()
    {
        // Server or old app version may have used PascalCase JSON keys.
        const string json = """
            [{
                "Employee": "Dave",
                "Date": "2026-01-10",
                "OriginalTimeRange": "8:00-4:00",
                "DisplayTime": "8:00-4:00",
                "TimeState": 2,
                "PositionState": 2,
                "BoundsX": null,
                "BoundsY": null,
                "BoundsWidth": null,
                "BoundsHeight": null
            }]
            """;
        var rt = BubblePersistenceService.Deserialize(json);
        Assert.Single(rt);
        Assert.Equal("Dave", rt[0].Employee);
        Assert.Equal(2, rt[0].TimeState);
    }

    [Fact]
    public void Deserialize_LowerCasePropertyNames_StillParses()
    {
        const string json = """
            [{
                "employee": "Eve",
                "date": "2026-01-11",
                "originalTimeRange": "7:00-3:00",
                "displayTime": "7:00-3:00",
                "timeState": 0,
                "positionState": 0,
                "boundsX": 100,
                "boundsY": 200,
                "boundsWidth": 180,
                "boundsHeight": 50
            }]
            """;
        var rt = BubblePersistenceService.Deserialize(json);
        Assert.Single(rt);
        Assert.Equal("Eve", rt[0].Employee);
        Assert.Equal(100, rt[0].BoundsX);
    }
}
