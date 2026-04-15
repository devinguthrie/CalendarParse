using CalendarParse.Core.Services;
using CalendarParse.Models;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for PositionTargetSizer — the pure logic that decides the size of the
/// position target rectangle overlay.
///
/// Key behaviour documented here (Hint 2):
///
///   UNCALIBRATED (no prior confirmed bounds)
///   ─────────────────────────────────────────
///   Width  = max(100, viewportW / 3)
///   Height = max(40,  W / 4.5)          — fixed 4.5 : 1 aspect ratio
///
///   CALIBRATED (at least one position confirmed in this session)
///   ─────────────────────────────────────────────────────────────
///   Width  = max(60, prev.Width  × scaleX)
///   Height = max(20, prev.Height × scaleY)   — actual row proportions
///
/// The FIRST bubble always gets the uncalibrated (viewport-fraction) rectangle,
/// even after the user confirms its time.  The shape only changes to calibrated
/// once the FIRST POSITION has been confirmed (i.e. _lastConfirmedImageBounds is
/// populated in ConfirmationPage).
/// </summary>
public class PositionTargetSizerTests
{
    // ── Uncalibrated (null prevBounds) ────────────────────────────────────────

    [Fact]
    public void NoPriorBounds_UsesViewportThirdFormula()
    {
        var (w, h) = PositionTargetSizer.Compute(null, scaleX: 1, scaleY: 1, viewportW: 300);

        Assert.Equal(100.0, w);          // max(100, 300/3 = 100) = 100
        Assert.Equal(Math.Max(40d, 100d / 4.5), h, precision: 6);
    }

    [Fact]
    public void NoPriorBounds_WideViewport_UsesViewportThird()
    {
        var (w, h) = PositionTargetSizer.Compute(null, scaleX: 1, scaleY: 1, viewportW: 600);

        Assert.Equal(200.0, w);          // 600 / 3 = 200 > 100 minimum
        Assert.Equal(Math.Max(40d, 200d / 4.5), h, precision: 6);
    }

    [Fact]
    public void NoPriorBounds_NarrowViewport_ClampsToMin100()
    {
        // viewportW=240 → 240/3=80 < 100, so width clamps to 100.
        var (w, _) = PositionTargetSizer.Compute(null, scaleX: 1, scaleY: 1, viewportW: 240);

        Assert.Equal(100.0, w);
    }

    [Fact]
    public void NoPriorBounds_HeightClampsToMin40()
    {
        // Very narrow viewport: w=100 → w/4.5 ≈ 22.2 < 40, so height clamps to 40.
        var (_, h) = PositionTargetSizer.Compute(null, scaleX: 1, scaleY: 1, viewportW: 100);

        Assert.Equal(40.0, h);
    }

    [Fact]
    public void NoPriorBounds_ScaleParametersIgnored()
    {
        // Scale factors should not affect the uncalibrated path.
        var (w1, h1) = PositionTargetSizer.Compute(null, scaleX: 1.0, scaleY: 1.0, viewportW: 390);
        var (w2, h2) = PositionTargetSizer.Compute(null, scaleX: 2.5, scaleY: 0.3, viewportW: 390);

        Assert.Equal(w1, w2);
        Assert.Equal(h1, h2);
    }

    // ── Calibrated (prevBounds has Width > 0) ─────────────────────────────────

    [Fact]
    public void WithPriorBounds_UsesBoundsTimesScale()
    {
        var prev = new BoundingBox { X = 0, Y = 0, Width = 400, Height = 80 };

        var (w, h) = PositionTargetSizer.Compute(prev, scaleX: 0.5, scaleY: 0.5, viewportW: 390);

        Assert.Equal(200.0, w);   // 400 * 0.5 = 200, above 60 minimum
        Assert.Equal(40.0,  h);   // 80  * 0.5 = 40,  above 20 minimum
    }

    [Fact]
    public void WithPriorBounds_SmallBoundsAtHighZoom_StillExceedsMin()
    {
        // Bounds very small in image space but ×scale ≥ minimums.
        var prev = new BoundingBox { Width = 200, Height = 50 };

        var (w, h) = PositionTargetSizer.Compute(prev, scaleX: 0.4, scaleY: 0.4, viewportW: 390);

        Assert.Equal(80.0, w);   // 200 * 0.4 = 80 > 60
        Assert.Equal(20.0, h);   // 50  * 0.4 = 20, exactly at minimum
    }

    [Fact]
    public void WithPriorBounds_VerySmallScaledBounds_ClampsToMinimums()
    {
        // prev.Width * scale < 60 and prev.Height * scale < 20 — both should clamp.
        var prev = new BoundingBox { Width = 50, Height = 10 };

        var (w, h) = PositionTargetSizer.Compute(prev, scaleX: 0.5, scaleY: 0.5, viewportW: 390);

        Assert.Equal(60.0, w);   // max(60, 50*0.5=25) = 60
        Assert.Equal(20.0, h);   // max(20, 10*0.5=5)  = 20
    }

    [Fact]
    public void WithPriorBounds_ZeroWidth_TreatedAsUncalibrated()
    {
        // A BoundingBox with Width == 0 must fall back to uncalibrated mode, because
        // ConfirmationPage uses the guard `is { Width: > 0 }`.
        var prev = new BoundingBox { Width = 0, Height = 80 };

        var (w, _) = PositionTargetSizer.Compute(prev, scaleX: 0.5, scaleY: 0.5, viewportW: 390);

        // Should use uncalibrated path: max(100, 390/3=130) = 130
        Assert.Equal(130.0, w);
    }

    // ── Shape difference: first vs subsequent rects ───────────────────────────

    /// <summary>
    /// The first target rectangle (no prior confirmed bounds) has a fixed
    /// 4.5 : 1 width-to-height aspect ratio.
    /// Subsequent rectangles use the actual confirmed row proportions (which are
    /// typically wider-and-shorter — closer to the real cell shape in the image).
    /// </summary>
    [Fact]
    public void FirstRect_HasFixedAspectRatio_SubsequentUsesRowProportions()
    {
        // First bubble — uncalibrated
        var (firstW, firstH) = PositionTargetSizer.Compute(null, 1, 1, viewportW: 600);
        var firstAspect = firstW / firstH;

        // After first confirm: prev.Width/Height = 4:1 aspect (narrower than 4.5)
        var confirmedBounds = new BoundingBox { Width = 500, Height = 100 };   // 5:1
        var (subsequentW, subsequentH) =
            PositionTargetSizer.Compute(confirmedBounds, scaleX: 0.4, scaleY: 0.4, viewportW: 600);
        var subsequentAspect = subsequentW / subsequentH;

        // The two shapes are different.
        Assert.NotEqual(firstAspect, subsequentAspect, precision: 2);
    }

    /// <summary>
    /// Even after the user confirms TIME on the first day, if no position has been
    /// confirmed the rect is still uncalibrated (viewport-fraction).
    /// Only the position-confirm step updates _lastConfirmedImageBounds.
    /// </summary>
    [Fact]
    public void AfterConfirmingTimeOnly_RectStillUncalibrated()
    {
        // Time confirmed but prevBounds is still null (ConfirmTime does not set
        // _lastConfirmedImageBounds — only ConfirmPositionFromMarker does).
        var (w, _) = PositionTargetSizer.Compute(prevBounds: null, 1, 1, viewportW: 390);

        // Still uses viewport/3 formula, not calibrated.
        var expected = Math.Max(100d, 390.0 / 3.0);
        Assert.Equal(expected, w);
    }

    /// <summary>
    /// Only after the FIRST POSITION is confirmed does the rect switch shape.
    /// </summary>
    [Fact]
    public void AfterFirstPositionConfirmed_RectBecomesCalibrated()
    {
        var prev = new BoundingBox { Width = 420, Height = 90 };
        const double scaleX = 0.45, scaleY = 0.45;

        var (w, h) = PositionTargetSizer.Compute(prev, scaleX, scaleY, viewportW: 390);

        Assert.Equal(Math.Max(60d, 420 * scaleX), w, precision: 6);
        Assert.Equal(Math.Max(20d,  90 * scaleY), h, precision: 6);
    }
}
