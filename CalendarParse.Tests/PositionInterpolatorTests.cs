using CalendarParse.Core.Services;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for PositionInterpolator.Interpolate — the pure math that seeds the
/// X position for unconfirmed bubbles based on calibrated anchor confirmations.
///
/// Failure here means bubbles will jump to the wrong column when the user moves
/// between days in the position-edit flow.
/// </summary>
public class PositionInterpolatorTests
{
    // helper alias for brevity
    private static int? Interp(
        int targetIndex,
        params (int Index, int ImageX)[] anchors)
        => PositionInterpolator.Interpolate(anchors, targetIndex);

    // ── Zero / one anchor → null ──────────────────────────────────────────────

    [Fact]
    public void ZeroAnchors_ReturnsNull()
        => Assert.Null(Interp(0));

    [Fact]
    public void OneAnchor_ReturnsNull()
        => Assert.Null(Interp(0, (0, 100)));

    [Fact]
    public void TwoAnchors_SameIndex_ReturnsNull()
        => Assert.Null(Interp(1, (2, 100), (2, 200)));

    // ── Exact endpoints ───────────────────────────────────────────────────────

    [Fact]
    public void TwoAnchors_TargetAtFirstIndex_ReturnsFirstX()
    {
        var result = Interp(0, (0, 100), (7, 800));
        Assert.Equal(100, result);
    }

    [Fact]
    public void TwoAnchors_TargetAtLastIndex_ReturnsLastX()
    {
        var result = Interp(7, (0, 100), (7, 800));
        Assert.Equal(800, result);
    }

    // ── Linear interpolation ──────────────────────────────────────────────────

    [Fact]
    public void TwoAnchors_TargetAtMidpoint_ReturnsMidpointX()
    {
        // Indices 0→8 span 9 positions; midpoint is index 4
        // X: 0→800, slope = 100/index
        var result = Interp(4, (0, 0), (8, 800));
        Assert.Equal(400, result);
    }

    [Fact]
    public void TwoAnchors_Interpolation_IsLinear()
    {
        // 3 evenly-spaced indices with known X values
        var x0 = Interp(0, (0, 0), (6, 600));  // 0
        var x2 = Interp(2, (0, 0), (6, 600));  // 200
        var x4 = Interp(4, (0, 0), (6, 600));  // 400
        var x6 = Interp(6, (0, 0), (6, 600));  // 600

        Assert.Equal(0,   x0);
        Assert.Equal(200, x2);
        Assert.Equal(400, x4);
        Assert.Equal(600, x6);
    }

    // ── Extrapolation (target outside anchor range) ───────────────────────────

    [Fact]
    public void TwoAnchors_TargetBeyondLast_Extrapolates()
    {
        // Anchors at index 0 (X=0) and 4 (X=400); step = 100/index
        // Target index 6 → expected X = 600
        var result = Interp(6, (0, 0), (4, 400));
        Assert.Equal(600, result);
    }

    [Fact]
    public void TwoAnchors_TargetBeforeFirst_ExtrapolatesLeft()
    {
        // Anchors at indices 2 and 6; slope = 100/index
        // Target index 0 → expected X = 0
        var result = Interp(0, (2, 200), (6, 600));
        Assert.Equal(0, result);
    }

    // ── Extra anchors: Interpolate uses first + last ──────────────────────────

    [Fact]
    public void ThreeAnchors_UsesOutermost()
    {
        // First=(0,0), Last=(8,800), middle=(4,999) ignored
        var result = Interp(4, (0, 0), (4, 999), (8, 800));
        // Uses first=0/0 and last=8/800 → t=0.5 → 400
        Assert.Equal(400, result);
    }

    // ── Non-zero based index range ────────────────────────────────────────────

    [Fact]
    public void NonZeroIndexRange_InterpolatesCorrectly()
    {
        // Anchors: index 3 → X=300, index 9 → X=900
        // Target index 6 → t = (6-3)/(9-3) = 0.5 → X = 300 + 0.5*600 = 600
        var result = Interp(6, (3, 300), (9, 900));
        Assert.Equal(600, result);
    }

    // ── Negative X (offset-based layouts) ────────────────────────────────────

    [Fact]
    public void NegativeXAnchors_HandleCorrectly()
    {
        // Declining X across the schedule (right-to-left calendar)
        var result = Interp(2, (0, 800), (4, 400));
        Assert.Equal(600, result);
    }

    // ── Rounding ──────────────────────────────────────────────────────────────

    [Fact]
    public void FractionalResult_IsRounded()
    {
        // Anchors: index 0 → X=0, index 3 → X=100
        // Target index 1 → t = 1/3 → X = 33.33... → rounds to 33
        var result = Interp(1, (0, 0), (3, 100));
        Assert.Equal(33, result);
    }
}
