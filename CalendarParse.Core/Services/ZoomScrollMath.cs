namespace CalendarParse.Core.Services;

/// <summary>
/// Pure-math helpers for keeping a fixed viewport marker pinned to the same
/// image-pixel column as the user zooms.  Extracted to be unit-testable
/// independently of MAUI layout machinery.
/// </summary>
public static class ZoomScrollMath
{
    /// <summary>
    /// Returns the AspectFit scale and horizontal offset so that an image of
    /// (natW × natH) natural pixels fills a rendered area of (rendW × rendH)
    /// while respecting aspect ratio.
    ///
    ///   containerX = imgPxX * scale + offsetX
    ///   imgPxX     = (containerX - offsetX) / scale
    /// </summary>
    public static (double Scale, double OffsetX, double OffsetY)
        GetImageTransform(double natW, double natH, double rendW, double rendH)
    {
        if (natW <= 0 || natH <= 0 || rendW <= 0 || rendH <= 0)
            return (1, 0, 0);

        var scale   = Math.Min(rendW / natW, rendH / natH);
        var offsetX = (rendW - natW * scale) / 2.0;
        var offsetY = (rendH - natH * scale) / 2.0;
        return (scale, offsetX, offsetY);
    }

    /// <summary>
    /// Given the current scroll + viewport state, returns the image-pixel column
    /// that is currently centred under the viewport marker.
    ///
    /// The marker is always at viewport centre X, so its container X is:
    ///   containerCX = intendedScrollX + viewportW / 2
    /// </summary>
    public static double CaptureMarkerColumn(
        double intendedScrollX, double viewportW, double scale, double offsetX)
    {
        var containerCX = intendedScrollX + viewportW / 2.0;
        return (containerCX - offsetX) / scale;
    }

    /// <summary>
    /// Given a target image-pixel column that should appear under the viewport
    /// centre marker, returns the scrollX required to achieve that after zoom.
    /// </summary>
    public static double ComputeScrollXForMarkerColumn(
        double imgCX, double scale, double offsetX, double viewportW)
    {
        var containerCX = imgCX * scale + offsetX;
        return Math.Max(0, containerCX - viewportW / 2.0);
    }
}
