using CalendarParse.Models;

namespace CalendarParse.Core.Services;

/// <summary>
/// Pure logic for sizing the PositionTargetRect overlay.
///
/// Two modes:
///   Calibrated   — a prior confirmed bounding box is available; the rect is
///                  sized to match the confirmed row's pixel dimensions at the
///                  current zoom level.
///   Uncalibrated — no prior confirmation (first bubble in the session); the
///                  rect uses a viewport-fraction fallback with a fixed aspect
///                  ratio that approximates a typical schedule row.
///
/// Used by <c>ConfirmationPage.ShowPositionTargetRect</c> and independently
/// testable without any MAUI dependencies.
/// </summary>
public static class PositionTargetSizer
{
    // Calibrated mode minimums (device points).
    private const double MinCalibratedW = 60d;
    private const double MinCalibratedH = 20d;

    // Uncalibrated mode minimums and aspect ratio.
    private const double MinUncalibratedW    = 100d;
    private const double MinUncalibratedH    = 40d;
    private const double UncalibratedAspect  = 4.5;   // W / H — typical schedule-row ratio

    /// <summary>
    /// Computes the width and height (device points) of the position target rectangle.
    /// </summary>
    /// <param name="prevBounds">
    /// The last confirmed image bounding box, or <c>null</c> when no position has
    /// been confirmed yet in the current session (first bubble / uncalibrated mode).
    /// </param>
    /// <param name="scaleX">Horizontal image-to-screen scale factor.</param>
    /// <param name="scaleY">Vertical image-to-screen scale factor.</param>
    /// <param name="viewportW">Width of the visible scroll-view viewport in device points.</param>
    /// <returns>(W, H) sizing tuple in device points.</returns>
    public static (double W, double H) Compute(
        BoundingBox? prevBounds,
        double scaleX,
        double scaleY,
        double viewportW)
    {
        if (prevBounds is { Width: > 0 } prev)
        {
            // Calibrated: scale the confirmed pixel dimensions to screen space.
            // GetImageTransform already bakes in _zoomScale, so no extra factor.
            return (
                Math.Max(MinCalibratedW,   prev.Width  * scaleX),
                Math.Max(MinCalibratedH,   prev.Height * scaleY));
        }

        // Uncalibrated: fall back to viewport-fraction width and fixed aspect ratio.
        var w = Math.Max(MinUncalibratedW, viewportW / 3.0);
        var h = Math.Max(MinUncalibratedH, w / UncalibratedAspect);
        return (w, h);
    }
}
