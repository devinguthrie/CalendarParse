namespace CalendarParse.Cli.Services;

/// <summary>
/// Pixel-space layout of day columns and employee rows as computed during a parse run.
/// Written as a sidecar .positions.json alongside .output.json to enable overlay rendering.
///
/// Each position type comes in two flavours:
///   EstimatedCell* — the best guess at where the actual grid cell sits in the image.
///   CompareDisplay* — a point placed at least 2 row-heights above the cell so an overlay
///                     label can be read without obscuring the original cell content.
/// </summary>
public class CellPositions
{
    public int ImageWidth  { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>Estimated pixel height of one employee row. Used to compute CompareDisplay offsets.</summary>
    public int EstimatedRowHeightPx { get; set; }

    /// <summary>
    /// Pixels from the name-token Y (top of OCR text) to the vertical center of the shift cell.
    /// Calibrated from OCR observations (~9 px in typical images).
    /// </summary>
    public int NameCellOffsetPx { get; set; } = 9;

    /// <summary>True when column positions were derived from WinRT OCR day-header tokens; false when from the OpenCV grid detector fallback.</summary>
    public bool ColumnsFromOcr { get; set; } = true;

    /// <summary>Day index (0=Sun..6=Sat) → column pixel bounds in the original image.</summary>
    public Dictionary<int, ColBounds> Columns { get; set; } = new();

    /// <summary>Employee name → row Y positions (estimated cell center and compare-display target).</summary>
    public Dictionary<string, EmployeeRow> Rows { get; set; } = new();

    /// <summary>Employee names in visual top-to-bottom order (matches the JSON output order).</summary>
    public List<string> Names { get; set; } = new();
}

/// <summary>Pixel X-range for one day column in the original image.</summary>
public class ColBounds
{
    /// <summary>Left pixel boundary of the estimated day-column cell.</summary>
    public int EstimatedCellXStart { get; set; }
    /// <summary>Right pixel boundary of the estimated day-column cell.</summary>
    public int EstimatedCellXEnd   { get; set; }
}

/// <summary>
/// Y-axis positions for one employee row.
/// X coordinates come from <see cref="ColBounds"/>; the column center is the same for both position types.
/// </summary>
public class EmployeeRow
{
    /// <summary>Y pixel of the vertical center of the employee's shift cell (name-token Y + NameCellOffsetPx).</summary>
    public int EstimatedCellY { get; set; }

    /// <summary>
    /// Y pixel for overlay label placement — at least 2 estimated row-heights above
    /// <see cref="EstimatedCellY"/>, so the guessed value sits clearly above the original
    /// cell content for easy visual comparison.
    /// </summary>
    public int CompareDisplayY { get; set; }
}
