namespace CalendarParse.Models
{
    /// <summary>
    /// Represents a single detected cell in the schedule table grid.
    /// Row and Col are zero-based indices sorted top-to-bottom, left-to-right.
    /// </summary>
    public class TableCell
    {
        public int Row { get; set; }
        public int Col { get; set; }

        /// <summary>Bounding rectangle in the original image coordinate space (pixels).</summary>
        public Rect Bounds { get; set; }

        /// <summary>OCR text extracted from this cell. Empty until OCR is run.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>ML Kit OCR confidence score [0.0, 1.0]. -1 means OCR not yet run.</summary>
        public float Confidence { get; set; } = -1f;
    }

    /// <summary>Platform-agnostic bounding rectangle (avoids importing Android types in shared code).</summary>
    public readonly struct Rect
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public Rect(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public int CenterX => X + Width / 2;
        public int CenterY => Y + Height / 2;
    }
}
