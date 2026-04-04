namespace CalendarParse.Models
{
    /// <summary>
    /// Absolute pixel coordinates in the original (post-EXIF-rotation) image.
    /// (0,0) = top-left corner.
    /// </summary>
    public class BoundingBox
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
