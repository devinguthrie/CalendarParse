using CalendarParse.Core.Services;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for MarkerPositionMath.ComputeImageCoords — the pure math that converts
/// the fixed viewport marker rectangle into an image-pixel bounding-box.
///
/// Failure here means confirmed schedule positions will be off by a constant
/// offset, causing the X/Y lock to not anchor to the right cell.
/// </summary>
public class MarkerPositionMathTests
{
    // Shared constants
    private const double ViewportW = 400.0;
    private const double ViewportH = 800.0;
    private const double MarkerW   = 180.0;
    private const double MarkerH   = 50.0;
    private const double ScrollX   = 0.0;
    private const double ScrollY   = 0.0;

    /// <summary>
    /// At scale=1, offset=0 (image exactly fills viewport), the image coords
    /// should equal the container coords directly.
    /// </summary>
    [Fact]
    public void NoOffset_NoScroll_NoLabel_CentresInViewport()
    {
        var (imgX, imgY, imgW, imgH) = MarkerPositionMath.ComputeImageCoords(
            scrollX:   0, scrollY:  0,
            viewportW: ViewportW, viewportH: ViewportH,
            markerW:   MarkerW,  markerH:   MarkerH,
            labelH:    0, debugH: 0,
            scaleX:    1, scaleY: 1,
            offsetX:   0, offsetY: 0);

        // With no label, marker top-left should be centred in viewport
        var expectedX = (int)Math.Round((ViewportW - MarkerW) / 2.0);
        var expectedY = (int)Math.Round((ViewportH - MarkerH) / 2.0);

        Assert.Equal(expectedX, imgX);
        Assert.Equal(expectedY, imgY);
        Assert.Equal((int)Math.Round(MarkerW), imgW);
        Assert.Equal((int)Math.Round(MarkerH), imgH);
    }

    /// <summary>
    /// A label above the rect shifts the rect DOWN by labelH, so imgY must
    /// be larger than the no-label result.
    /// </summary>
    [Fact]
    public void WithLabel_RectShiftsDown_OffsetByLabelHeight()
    {
        const double labelH = 30.0;

        var (_, imgYNoLabel, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH,
            labelH: 0, debugH: 0, scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        var (_, imgYWithLabel, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH,
            labelH: labelH, debugH: 0, scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        Assert.True(imgYWithLabel > imgYNoLabel,
            $"Expected label to shift rect down: noLabel={imgYNoLabel}, withLabel={imgYWithLabel}");
        Assert.Equal(imgYNoLabel + (int)Math.Round(labelH / 2.0), imgYWithLabel);
    }

    /// <summary>
    /// A debug label below the rect shifts the stack up by debugH/2, so imgY
    /// must be smaller than the no-debug result.
    /// </summary>
    [Fact]
    public void WithDebugLabel_RectShiftsUp_OffsetByHalfDebugHeight()
    {
        const double debugH = 40.0;

        var (_, imgYNoDebug, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH,
            labelH: 0, debugH: 0, scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        var (_, imgYWithDebug, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH,
            labelH: 0, debugH: debugH, scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        Assert.True(imgYWithDebug < imgYNoDebug,
            $"Expected debug label to shift rect up: noDebug={imgYNoDebug}, withDebug={imgYWithDebug}");
    }

    /// <summary>
    /// Scroll moves the image under the fixed marker, so the image coords
    /// increase by the scroll amount (at scale=1, offset=0).
    /// </summary>
    [Fact]
    public void ScrollX_TranslatesImageCoordsByScrollAmount()
    {
        var (imgX0, _, _, _) = MarkerPositionMath.ComputeImageCoords(
            scrollX: 0, scrollY: 0, viewportW: ViewportW, viewportH: ViewportH,
            markerW: MarkerW, markerH: MarkerH, labelH: 0, debugH: 0,
            scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        var (imgX200, _, _, _) = MarkerPositionMath.ComputeImageCoords(
            scrollX: 200, scrollY: 0, viewportW: ViewportW, viewportH: ViewportH,
            markerW: MarkerW, markerH: MarkerH, labelH: 0, debugH: 0,
            scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        Assert.Equal(imgX0 + 200, imgX200);
    }

    [Fact]
    public void ScrollY_TranslatesImageCoordsByScrollAmount()
    {
        var (_, imgY0, _, _) = MarkerPositionMath.ComputeImageCoords(
            scrollX: 0, scrollY: 0, viewportW: ViewportW, viewportH: ViewportH,
            markerW: MarkerW, markerH: MarkerH, labelH: 0, debugH: 0,
            scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        var (_, imgY300, _, _) = MarkerPositionMath.ComputeImageCoords(
            scrollX: 0, scrollY: 300, viewportW: ViewportW, viewportH: ViewportH,
            markerW: MarkerW, markerH: MarkerH, labelH: 0, debugH: 0,
            scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        Assert.Equal(imgY0 + 300, imgY300);
    }

    /// <summary>
    /// Scale divides the container coords to get image coords.
    /// At scale=2 the image pixel coords are approximately half the container coords
    /// (within 1px — Math.Round is used, so half-integer values can round either way).
    /// </summary>
    [Fact]
    public void Scale2_HalvesImageCoords()
    {
        var (imgX1, imgY1, imgW1, imgH1) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH, 0, 0,
            scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        var (imgX2, imgY2, imgW2, imgH2) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH, 0, 0,
            scaleX: 2, scaleY: 2, offsetX: 0, offsetY: 0);

        // Use floating-point half to handle midpoint rounding (Math.Round uses banker's rounding).
        Assert.Equal((int)Math.Round(imgX1 / 2.0), imgX2);
        Assert.Equal((int)Math.Round(imgY1 / 2.0), imgY2);
        Assert.Equal((int)Math.Round(imgW1 / 2.0), imgW2);
        Assert.Equal((int)Math.Round(imgH1 / 2.0), imgH2);
    }

    /// <summary>
    /// AspectFit letterboxing adds horizontal or vertical offsets.
    /// The offset is subtracted before dividing by scale.
    /// </summary>
    [Fact]
    public void HorizontalOffset_SubtractedBeforeScaleDivision()
    {
        const double offsetX = 50.0;

        var (imgXNoOffset, _, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH, 0, 0,
            scaleX: 1, scaleY: 1, offsetX: 0, offsetY: 0);

        var (imgXWithOffset, _, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH, 0, 0,
            scaleX: 1, scaleY: 1, offsetX: offsetX, offsetY: 0);

        Assert.Equal(imgXNoOffset - (int)offsetX, imgXWithOffset);
    }

    /// <summary>
    /// Full round-trip: confirm a position at scroll/viewport/label state, then verify that
    /// the resulting image coords match the manually computed expected values.
    /// This exercises all parameters together and is the regression guard for Bug 5.
    /// </summary>
    [Fact]
    public void FullScenario_MatchesManuallyComputedValues()
    {
        // Realistic on-device values
        const double vW      = 390;  // viewport width
        const double vH      = 730;  // viewport height
        const double mW      = 160;  // marker width
        const double mH      = 48;   // marker height
        const double lH      = 28;   // label height (visible)
        const double dH      = 0;    // debug label hidden
        const double sX      = 120;  // scrollX
        const double sY      = 450;  // scrollY
        const double scale   = 0.38; // AspectFit scale at 2× zoom
        const double ox      = 0;    // no letterbox (portrait image fills width)
        const double oy      = 0;

        var (imgX, imgY, imgW, imgH) = MarkerPositionMath.ComputeImageCoords(
            sX, sY, vW, vH, mW, mH, lH, dH, scale, scale, ox, oy);

        // Manual calculation:
        // totalStackH = 28 + 48 + 0 = 76
        // viewX = (390-160)/2 = 115;  viewY = (730-76)/2 + 28 = 327 + 28 = 355
        // containerX = 115 + 120 = 235;  containerY = 355 + 450 = 805
        // imgX = (235-0)/0.38 ≈ 618;  imgY = (805-0)/0.38 ≈ 2118
        // imgW = 160/0.38 ≈ 421;  imgH = 48/0.38 ≈ 126
        Assert.Equal((int)Math.Round(235.0 / scale), imgX);
        Assert.Equal((int)Math.Round(805.0 / scale), imgY);
        Assert.Equal((int)Math.Round(mW    / scale), imgW);
        Assert.Equal((int)Math.Round(mH    / scale), imgH);
    }

    /// <summary>
    /// Symmetry: equal label+debug heights cancel out and the rect is centred
    /// in the viewport exactly as if there were no label.
    /// </summary>
    [Fact]
    public void EqualLabelAndDebug_CancelOut_RectIsCentred()
    {
        const double padding = 20.0;

        var (_, imgYNone, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH, 0, 0,
            1, 1, 0, 0);

        var (_, imgYBoth, _, _) = MarkerPositionMath.ComputeImageCoords(
            0, 0, ViewportW, ViewportH, MarkerW, MarkerH, padding, padding,
            1, 1, 0, 0);

        Assert.Equal(imgYNone, imgYBoth);
    }

    // ── Round-trip (Hint 1): confirmed bubble canvas matches marker position ──

    /// <summary>
    /// After ComputeImageCoords converts the marker position to image-pixel space,
    /// the forward transform (imgX * scaleX + offsetX) must reproduce the original
    /// container-space position within ±1 px (rounding tolerance from Math.Round).
    ///
    /// This guarantees that the confirmed bubble canvas border lands exactly where
    /// the target rectangle was when the user tapped "confirm position".
    /// </summary>
    [Fact]
    public void RoundTrip_ForwardTransform_MatchesOriginalContainerPosition()
    {
        const double sX = 150, sY = 600;
        const double vW = 390, vH = 720;
        const double mW = 160, mH = 48;
        const double lH = 28, dH = 18;
        const double scaleX = 0.45, scaleY = 0.45;
        const double offsetX = 0, offsetY = 0;

        var (imgX, imgY, _, _) = MarkerPositionMath.ComputeImageCoords(
            sX, sY, vW, vH, mW, mH, lH, dH, scaleX, scaleY, offsetX, offsetY);

        // Forward transform mirrors ConfirmationPage's ScreenBounds assignment.
        var screenCX = imgX * scaleX + offsetX;
        var screenCY = imgY * scaleY + offsetY;

        // Expected container-space top-left of the rect.
        var totalStackH = lH + mH + dH;
        var expectedContainerX = (vW - mW) / 2.0 + sX;
        var expectedContainerY = (vH - totalStackH) / 2.0 + lH + sY;

        Assert.InRange(screenCX, expectedContainerX - 1.0, expectedContainerX + 1.0);
        Assert.InRange(screenCY, expectedContainerY - 1.0, expectedContainerY + 1.0);
    }

    /// <summary>
    /// Parameterised round-trip across a range of realistic scroll / scale / offset
    /// combinations.  All must stay within ±1 px after the inverse+forward cycle.
    /// </summary>
    [Theory]
    [InlineData(0,   0,   400, 800, 180, 50, 0.5, 0.5,  0,  0)]
    [InlineData(200, 400, 390, 720, 160, 48, 0.4, 0.4,  0, 15)]
    [InlineData(100, 300, 393, 852, 130, 40, 1.0, 1.0, 20,  0)]
    [InlineData(0,   0,   360, 640, 120, 36, 0.3, 0.3,  0,  0)]
    public void RoundTrip_VariousScrollAndScale_Within1px(
        double sX, double sY, double vW, double vH, double mW, double mH,
        double scaleX, double scaleY, double offsetX, double offsetY)
    {
        var (imgX, imgY, _, _) = MarkerPositionMath.ComputeImageCoords(
            sX, sY, vW, vH, mW, mH, 0, 0, scaleX, scaleY, offsetX, offsetY);

        var screenCX = imgX * scaleX + offsetX;
        var screenCY = imgY * scaleY + offsetY;

        var expectedContainerX = (vW - mW) / 2.0 + sX;
        var expectedContainerY = (vH - mH) / 2.0 + sY;

        Assert.InRange(screenCX, expectedContainerX - 1.0, expectedContainerX + 1.0);
        Assert.InRange(screenCY, expectedContainerY - 1.0, expectedContainerY + 1.0);
    }

    /// <summary>
    /// Round-trip for dimensions: the confirmed bubble width/height in screen space
    /// must match the original marker dimensions within ±1 px.
    /// </summary>
    [Fact]
    public void RoundTrip_MarkerDimensions_ForwardTransformWithin1px()
    {
        const double mW = 160, mH = 48, scaleX = 0.4, scaleY = 0.4;

        var (_, _, imgW, imgH) = MarkerPositionMath.ComputeImageCoords(
            0, 0, 400, 800, mW, mH, 0, 0, scaleX, scaleY, 0, 0);

        var recoveredW = imgW * scaleX;
        var recoveredH = imgH * scaleY;

        Assert.InRange(recoveredW, mW - 1.0, mW + 1.0);
        Assert.InRange(recoveredH, mH - 1.0, mH + 1.0);
    }

    /// <summary>
    /// When zoom is active, ScrollContent has Padding=imagePad (e.g. 100dp) so
    /// ImageContainer starts at imagePad inside ScrollContent.  scrollX/Y are
    /// in ScrollContent space; offsetX/Y are in ImageContainer space.
    /// ComputeImageCoords must subtract imagePad before dividing by scale, otherwise
    /// the confirmed canvas border lands imagePad dp away from the target rect.
    ///
    /// Verification: with imagePad=100, imgX should be 100/scaleX FEWER image pixels
    /// than with imagePad=0, and the screenBounds forward transform must equal the
    /// ImageContainer-space rect position (scrollX - imagePad + viewX + offsetX).
    /// </summary>
    [Fact]
    public void ImagePad_SubtractedFromScrollBeforeScaleDivision()
    {
        const double sX = 500, sY = 1200;
        const double vW = 411, vH = 612;
        const double mW = 137, mH = 40;
        const double lH = 21, dH = 49;
        const double scaleX = 1.709, scaleY = 1.709;
        const double offsetX = 0, offsetY = 931;
        const double pad = 100;

        var (imgXNoPad, imgYNoPad, _, _) = MarkerPositionMath.ComputeImageCoords(
            sX, sY, vW, vH, mW, mH, lH, dH, scaleX, scaleY, offsetX, offsetY, imagePad: 0);

        var (imgXWithPad, imgYWithPad, _, _) = MarkerPositionMath.ComputeImageCoords(
            sX, sY, vW, vH, mW, mH, lH, dH, scaleX, scaleY, offsetX, offsetY, imagePad: pad);

        // With imagePad, raw scrollX is reduced by pad before dividing by scale.
        Assert.Equal((int)Math.Round(imgXNoPad - pad / scaleX), imgXWithPad);
        Assert.Equal((int)Math.Round(imgYNoPad - pad / scaleY), imgYWithPad);

        // Round-trip: screenBounds must equal ImageContainer-space rect position (±1px).
        var totalStackH = lH + mH + dH;
        var expectedContainerX = (sX - pad) + (vW - mW) / 2.0;          // ImageContainer space
        var expectedContainerY = (sY - pad) + (vH - totalStackH) / 2.0 + lH;

        var screenX = imgXWithPad * scaleX + offsetX;
        var screenY = imgYWithPad * scaleY + offsetY;

        Assert.InRange(screenX, expectedContainerX - 1.0, expectedContainerX + 1.0);
        Assert.InRange(screenY, expectedContainerY - 1.0, expectedContainerY + 1.0);
    }
}
