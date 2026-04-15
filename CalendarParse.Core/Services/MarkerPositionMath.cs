namespace CalendarParse.Core.Services;

/// <summary>
/// Pure math for converting the center of a fixed viewport marker rectangle
/// into image-pixel coordinates.  Extracted from ConfirmationPage so the
/// coordinate conversion logic can be unit-tested without a live MAUI page.
///
/// Layout model
/// ─────────────
/// The marker stack lives in the centre of the ScrollView viewport:
///
///   ┌─────────────────────── viewport (viewportW × viewportH) ──────────────┐
///   │                                                                        │
///   │       ┌──────── PositionTargetLabel (labelH) ──────────┐              │
///   │       │                                                 │              │
///   │       ├──────── PositionTargetRect  (markerH) ──────────┤  ← target   │
///   │       │                                                 │              │
///   │       └──────── PositionDebugLabel  (debugH)  ──────────┘              │
///   │                                                                        │
///   └────────────────────────────────────────────────────────────────────────┘
///
/// The rect is centred on the *stack*, not the whole viewport, so the label
/// above it causes a downward bias equal to (labelH − debugH) / 2 relative
/// to a naive centre-of-viewport calculation.
/// </summary>
public static class MarkerPositionMath
{
    /// <summary>
    /// Converts the centre of the PositionTargetRect (as it appears in the
    /// viewport) into image-pixel coordinates.
    ///
    /// Parameters
    /// ──────────
    /// <paramref name="scrollX"/> / <paramref name="scrollY"/>
    ///     Current ScrollView scroll offsets (content-space).
    /// <paramref name="viewportW"/> / <paramref name="viewportH"/>
    ///     Dimensions of the visible viewport rectangle.
    /// <paramref name="markerW"/> / <paramref name="markerH"/>
    ///     Requested dimensions of PositionTargetRect.
    /// <paramref name="labelH"/>
    ///     Height of the label shown <em>above</em> the rect (0 when hidden).
    /// <paramref name="debugH"/>
    ///     Height of the debug label shown <em>below</em> the rect (0 when hidden).
    /// <paramref name="scaleX"/> / <paramref name="scaleY"/>
    ///     Image-to-container scale factors (from <see cref="ZoomScrollMath.GetImageTransform"/>).
    /// <paramref name="offsetX"/> / <paramref name="offsetY"/>
    ///     Container offset from AspectFit letterboxing.
    ///
    /// Returns
    /// ───────
    /// The top-left image-pixel corner and dimensions of the target rect.
    /// Values are rounded to the nearest integer; <em>no</em> clamping is
    /// applied — callers should clamp to [0, imageWidth/Height] if needed.
    /// </returns>
    public static (int ImgX, int ImgY, int ImgW, int ImgH) ComputeImageCoords(
        double scrollX,
        double scrollY,
        double viewportW,
        double viewportH,
        double markerW,
        double markerH,
        double labelH,
        double debugH,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY,
        double imagePad = 0.0)
    {
        // Total stack height; Spacing=0 assumed.
        var totalStackH = labelH + markerH + debugH;

        // Viewport-space top-left of the rect (centred within its stack, then within viewport).
        var viewX = (viewportW - markerW)    / 2.0;
        var viewY = (viewportH - totalStackH) / 2.0 + labelH;

        // Shift from viewport-space to container-space by adding scroll offset.
        // Subtract imagePad: when zoom is active the ScrollContent has a Padding of imagePad,
        // meaning ImageContainer starts at (imagePad, imagePad) inside ScrollContent.
        // scrollX/Y are in ScrollContent space; offsetX/Y are in ImageContainer space,
        // so we must adjust to a common space before dividing by scale.
        var containerX = viewX + scrollX - imagePad;
        var containerY = viewY + scrollY - imagePad;

        // Convert to image-pixel space via inverse of GetImageTransform.
        var imgX = (int)Math.Round((containerX - offsetX) / scaleX);
        var imgY = (int)Math.Round((containerY - offsetY) / scaleY);
        var imgW = (int)Math.Round(markerW / scaleX);
        var imgH = (int)Math.Round(markerH / scaleY);

        return (imgX, imgY, imgW, imgH);
    }
}
