using CalendarParse.Core.Services;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for ZoomScrollMath — the pure math behind keeping the position
/// marker's image-pixel column locked as zoom changes.
///
/// Failure of any of these tests means the zoom X-lock on-device will drift.
/// </summary>
public class ZoomScrollMathTests
{
    private const double ViewW  = 400.0;  // simulated viewport width
    private const double NatW   = 1000.0; // natural image width (pixels)
    private const double NatH   = 2000.0; // natural image height
    private const double Tol    = 0.5;    // pixel tolerance for round-trips

    // ── GetImageTransform ────────────────────────────────────────────────────

    [Fact]
    public void GetImageTransform_WidthConstrained_ScaleIsWidthRatio()
    {
        // natW=1000, natH=2000, rendW=400, rendH=1600
        // width ratio = 0.4, height ratio = 0.8  =>  width constrains
        var (scale, offsetX, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, 400, 1600);
        Assert.Equal(400.0 / NatW, scale, precision: 6); // 0.4 — width-constrained
        Assert.Equal(0, offsetX, precision: 6);           // rendered width fills rendW exactly
        // rendered image height = 2000 * 0.4 = 800; rendH=1600 → offsetY = (1600-800)/2 = 400
        Assert.Equal((1600 - NatH * scale) / 2.0, offsetY, precision: 6);
    }

    [Fact]
    public void GetImageTransform_HeightConstrained_ScaleIsHeightRatio()
    {
        // natW=1000, natH=2000, rendW=2000, rendH=800
        // width ratio = 2.0, height ratio = 0.4  =>  height constrains
        var (scale, offsetX, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, 2000, 800);
        Assert.Equal(800.0 / NatH, scale, precision: 6); // 0.4
        Assert.Equal(0, offsetY, precision: 6);
        Assert.True(offsetX > 0, $"Expected offsetX > 0 but got {offsetX}");
    }

    [Fact]
    public void GetImageTransform_WideImage_ScaleIsWidthConstrained()
    {
        var (scale, _, _) = ZoomScrollMath.GetImageTransform(2000, 500, 400, 1000);
        Assert.Equal(400.0 / 2000.0, scale, precision: 6);
    }

    [Fact]
    public void GetImageTransform_ExactFit_OffsetIsZero()
    {
        var (_, offsetX, offsetY) = ZoomScrollMath.GetImageTransform(400, 800, 400, 800);
        Assert.Equal(0, offsetX, precision: 6);
        Assert.Equal(0, offsetY, precision: 6);
    }

    [Fact]
    public void GetImageTransform_ZeroDimension_ReturnsSafeDefaults()
    {
        var (scale, offsetX, offsetY) = ZoomScrollMath.GetImageTransform(0, NatH, 400, 800);
        Assert.Equal(1, scale);
        Assert.Equal(0, offsetX);
        Assert.Equal(0, offsetY);
    }

    // ── CaptureMarkerColumn ──────────────────────────────────────────────────

    [Fact]
    public void CaptureMarkerColumn_ViewportAtLeft_ReturnsCorrectColumn()
    {
        // rendW = 800 to fit NatW=1000 with some offset
        var (scale, offsetX, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, 800, 1600);
        // scrollX=0, marker is at viewport centre X = 200
        var col = ZoomScrollMath.CaptureMarkerColumn(0, ViewW, scale, offsetX);
        // containerCX = 0 + 200 = 200; imgCX = (200 - offsetX) / scale
        var expectedCX = (200.0 - offsetX) / scale;
        Assert.Equal(expectedCX, col, Tol);
    }

    [Fact]
    public void CaptureMarkerColumn_ScrolledToImage500_ReturnsApprox500()
    {
        // At zoom 1 (rendW == natW), scale=1, offsetX=0, so imgCX == markerContainerX
        var (scale, offsetX, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, NatW, NatH * 2);
        // scrollX such that containerCX = 500
        // containerCX = scrollX + viewW/2  =>  scrollX = 500 - 200 = 300
        var scrollX = 300.0;
        var col = ZoomScrollMath.CaptureMarkerColumn(scrollX, ViewW, scale, offsetX);
        Assert.Equal(500.0, col, Tol);
    }

    // ── ComputeScrollXForMarkerColumn ────────────────────────────────────────

    [Fact]
    public void ComputeScrollX_RoundTrips_SameZoom()
    {
        var (scale, offsetX, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, 800, 1600);
        var originalScrollX = 150.0;

        var col     = ZoomScrollMath.CaptureMarkerColumn(originalScrollX, ViewW, scale, offsetX);
        var backX   = ZoomScrollMath.ComputeScrollXForMarkerColumn(col, scale, offsetX, ViewW);

        Assert.Equal(originalScrollX, backX, Tol);
    }

    [Fact]
    public void ComputeScrollX_NeverNegative()
    {
        var (scale, offsetX, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, 800, 1600);
        // col=0 is at the left edge — scrollX would be negative without clamping
        var scrollX = ZoomScrollMath.ComputeScrollXForMarkerColumn(0, scale, offsetX, ViewW);
        Assert.True(scrollX >= 0, $"Expected scrollX >= 0 but got {scrollX}");
    }

    // ── Full zoom round-trip: lock column across zoom change ─────────────────

    // Renderers wider than viewport give horizontal scroll headroom.
    // baseRendW=600, baseRendH=1200 at zoom=1 → container is 600 wide, viewport 400 → maxScrollX=200.
    // At zoom=1, scale=min(600/1000, 1200/2000)=0.6, offsetX=0.
    // Min reachable column at centre: (0+200)/0.6 = 333.
    // Max reachable column at centre: (200+200)/0.6 = 667.
    // Column 500 is safely in range for all zoom levels tested.
    [Theory]
    [InlineData(1.0,  2.0,  500.0)]   // zoom in
    [InlineData(2.0,  1.0,  500.0)]   // zoom out
    [InlineData(1.5,  3.0,  500.0)]   // aggressive zoom in
    [InlineData(3.0,  1.5,  500.0)]   // aggressive zoom out
    [InlineData(2.0,  4.0,  500.0)]   // fast double zoom
    public void ZoomChange_LockedColumn_IsPreserved(
        double zoom1, double zoom2, double imgColumn)
    {
        double baseRendW = 600;  // wider than viewport → gives horizontal scroll headroom
        double baseRendH = 1200;

        // Pre-zoom transform
        var (s1, ox1, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom1, baseRendH * zoom1);

        // Encode the intended scroll for this column to be centred
        var scrollX1 = ZoomScrollMath.ComputeScrollXForMarkerColumn(imgColumn, s1, ox1, ViewW);

        // --- user drags slider, zoom changes ---

        // Post-zoom transform
        var (s2, ox2, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom2, baseRendH * zoom2);

        // Capture column using PRE-zoom transform + intended scroll (not live ScrollX which may lag)
        var captured = ZoomScrollMath.CaptureMarkerColumn(scrollX1, ViewW, s1, ox1);

        // Compute new scroll using POST-zoom transform
        var scrollX2 = ZoomScrollMath.ComputeScrollXForMarkerColumn(captured, s2, ox2, ViewW);

        // Decode: what column appears under the marker after zoom?
        var colAfter = ZoomScrollMath.CaptureMarkerColumn(scrollX2, ViewW, s2, ox2);

        Assert.Equal(imgColumn, colAfter, Tol);
    }

    [Fact]
    public void ZoomChange_RapidMultipleSteps_ColumnPreserved()
    {
        // Simulates 10 rapid slider steps. Uses baseRendW=600 (wider than viewport)
        // so column 500 has scroll headroom at every zoom level.
        double baseRendW = 600;
        double baseRendH = 1200;
        double targetColumn = 500.0;  // column in the reachable centre range

        double zoom = 1.0;
        var (s, ox, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom, baseRendH * zoom);
        double intendedScrollX = ZoomScrollMath.ComputeScrollXForMarkerColumn(targetColumn, s, ox, ViewW);

        for (int i = 0; i < 10; i++)
        {
            zoom += 0.2;

            // CAPTURE uses intendedScrollX (not live ScrollX)
            var captured = ZoomScrollMath.CaptureMarkerColumn(intendedScrollX, ViewW, s, ox);

            // New transform after layout commits
            var (sNew, oxNew, _) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom, baseRendH * zoom);
            intendedScrollX = ZoomScrollMath.ComputeScrollXForMarkerColumn(captured, sNew, oxNew, ViewW);

            s  = sNew;
            ox = oxNew;
        }

        var colFinal = ZoomScrollMath.CaptureMarkerColumn(intendedScrollX, ViewW, s, ox);
        Assert.Equal(targetColumn, colFinal, Tol);
    }
}
