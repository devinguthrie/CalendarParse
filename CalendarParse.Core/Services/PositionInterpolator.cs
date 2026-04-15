namespace CalendarParse.Core.Services;

/// <summary>
/// Interpolates (or extrapolates) an image-space X coordinate for a target bubble
/// using the two outermost confirmed positions as anchors.
///
/// Extracted from ConfirmationPage so the pure math can be unit-tested
/// without instantiating a MAUI page.
/// </summary>
public static class PositionInterpolator
{
    /// <summary>
    /// Linearly interpolates (or extrapolates) an image-pixel X coordinate for
    /// <paramref name="targetIndex"/> based on the list of confirmed anchor positions.
    ///
    /// Returns <see langword="null"/> when fewer than two positions have been confirmed
    /// (not enough information to fit a line).
    ///
    /// The anchors list must be sorted by Index ascending; call sites are responsible
    /// for maintaining that invariant.
    /// </summary>
    public static int? Interpolate(
        IReadOnlyList<(int Index, int ImageX)> confirmedPositions,
        int targetIndex)
    {
        if (confirmedPositions.Count < 2) return null;

        var first = confirmedPositions[0];
        var last  = confirmedPositions[^1];

        if (first.Index == last.Index) return null;

        var t = (double)(targetIndex - first.Index) / (last.Index - first.Index);
        return (int)Math.Round(first.ImageX + (last.ImageX - first.ImageX) * t);
    }
}
