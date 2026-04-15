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

    // ── Y-axis lock: same math, vertical scroll ──────────────────────────────
    // CaptureMarkerColumn is used for both X and Y with symmetric arguments.
    // The Y-lock is captured once at first confirmation and compared on every
    // subsequent confirmation.  Drift here means bubbles land on the wrong row.

    private const double ViewH   = 600.0; // simulated viewport height
    private const double RowTol  = 1.5;   // slightly looser: Y has label/debug padding

    [Fact]
    public void CaptureMarkerRow_ViewportAtTop_ReturnsCorrectRow()
    {
        // rendH=2400 > natH*aspect → height-constrained, offsetY=0
        var (scale, _, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, NatW, NatH);
        // scrollY=0, marker is at viewport centre Y = 300
        var row = ZoomScrollMath.CaptureMarkerColumn(0, ViewH, scale, offsetY);
        var expectedRow = (0 + ViewH / 2.0 - offsetY) / scale;
        Assert.Equal(expectedRow, row, RowTol);
    }

    [Fact]
    public void CaptureMarkerRow_ScrolledToRow1000_ReturnsApprox1000()
    {
        // Use a rendered size equal to natural size → scale=1, offsetY=0
        var (scale, _, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, NatW, NatH);
        // containerCY = scrollY + viewH/2  =>  scrollY = 1000 - 300 = 700
        var scrollY = 700.0;
        var row = ZoomScrollMath.CaptureMarkerColumn(scrollY, ViewH, scale, offsetY);
        Assert.Equal(1000.0, row, RowTol);
    }

    [Fact]
    public void ComputeScrollY_RoundTrips_SameZoom()
    {
        var (scale, _, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, NatW, NatH * 2);
        var originalScrollY = 400.0;
        var row   = ZoomScrollMath.CaptureMarkerColumn(originalScrollY, ViewH, scale, offsetY);
        var backY = ZoomScrollMath.ComputeScrollXForMarkerColumn(row, scale, offsetY, ViewH);
        Assert.Equal(originalScrollY, backY, RowTol);
    }

    [Fact]
    public void ComputeScrollY_NeverNegative()
    {
        var (scale, _, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, NatW, NatH * 2);
        var scrollY = ZoomScrollMath.ComputeScrollXForMarkerColumn(0, scale, offsetY, ViewH);
        Assert.True(scrollY >= 0, $"Expected scrollY >= 0 but got {scrollY}");
    }

    // Lock label: if user first confirms at row R, every subsequent bubble must
    // stay within RowYTolerance of R.  The math must survive zoom changes.
    [Theory]
    [InlineData(1.0, 2.0,  1000.0)]
    [InlineData(2.0, 1.0,  1000.0)]
    [InlineData(1.5, 3.0,  1000.0)]
    [InlineData(3.0, 1.5,  1000.0)]
    public void ZoomChange_LockedRow_IsPreserved(
        double zoom1, double zoom2, double imgRow)
    {
        // Use taller rendered sizes so row 1000 is in the reachable scrollable range
        double baseRendW = 1000;
        double baseRendH = 3000;

        var (s1, _, oy1) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom1, baseRendH * zoom1);
        var scrollY1 = ZoomScrollMath.ComputeScrollXForMarkerColumn(imgRow, s1, oy1, ViewH);

        var (s2, _, oy2) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom2, baseRendH * zoom2);

        // Capture using PRE-zoom transform
        var capturedRow = ZoomScrollMath.CaptureMarkerColumn(scrollY1, ViewH, s1, oy1);

        // New scroll using POST-zoom transform
        var scrollY2 = ZoomScrollMath.ComputeScrollXForMarkerColumn(capturedRow, s2, oy2, ViewH);

        var rowAfter = ZoomScrollMath.CaptureMarkerColumn(scrollY2, ViewH, s2, oy2);
        Assert.Equal(imgRow, rowAfter, RowTol);
    }

    [Fact]
    public void ZoomChange_RapidMultipleSteps_RowPreserved()
    {
        double baseRendW = 1000;
        double baseRendH = 3000;
        double targetRow = 1000.0;

        double zoom = 1.0;
        var (s, _, oy) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom, baseRendH * zoom);
        double intendedScrollY = ZoomScrollMath.ComputeScrollXForMarkerColumn(targetRow, s, oy, ViewH);

        for (int i = 0; i < 10; i++)
        {
            zoom += 0.2;
            var captured = ZoomScrollMath.CaptureMarkerColumn(intendedScrollY, ViewH, s, oy);
            var (sNew, _, oyNew) = ZoomScrollMath.GetImageTransform(NatW, NatH, baseRendW * zoom, baseRendH * zoom);
            intendedScrollY = ZoomScrollMath.ComputeScrollXForMarkerColumn(captured, sNew, oyNew, ViewH);
            s  = sNew;
            oy = oyNew;
        }

        var rowFinal = ZoomScrollMath.CaptureMarkerColumn(intendedScrollY, ViewH, s, oy);
        Assert.Equal(targetRow, rowFinal, RowTol);
    }

    // Extra-padding applied during active zoom gesture must be subtracted before
    // capturing.  Simulate ExtraPad=100 subtracted from scrollY in the page.
    [Fact]
    public void CaptureMarkerRow_WithExtraPad_SubtractPadBeforeCapture()
    {
        const double pad = 100.0;
        var (scale, _, offsetY) = ZoomScrollMath.GetImageTransform(NatW, NatH, NatW, NatH);

        // Raw scrollY = 700; after subtracting pad → 600
        var rowWithPad    = ZoomScrollMath.CaptureMarkerColumn(700.0,       ViewH, scale, offsetY);
        var rowWithoutPad = ZoomScrollMath.CaptureMarkerColumn(700.0 - pad, ViewH, scale, offsetY);

        Assert.NotEqual(rowWithPad, rowWithoutPad);  // pad must make a difference
        // Without pad the row is lower (smaller scrollY → smaller row index)
        Assert.True(rowWithoutPad < rowWithPad);
    }
}
