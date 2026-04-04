namespace CalendarParse.Models
{
    /// <summary>
    /// A single parsed shift for one employee on one date, with optional overlay position.
    /// </summary>
    public class ShiftData
    {
        public string Employee { get; set; } = string.Empty;

        /// <summary>ISO date string, e.g. "2026-01-06".</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>Time range string as returned by the LLM, e.g. "9:00-5:00" or "x" for day-off.</summary>
        public string TimeRange { get; set; } = string.Empty;

        /// <summary>
        /// Estimated bounding box in the original image (absolute pixels, post-EXIF-rotation).
        /// Null when position data is unavailable (e.g. OllamaCalendarService fallback).
        /// </summary>
        public BoundingBox? EstimatedBounds { get; set; }
    }
}
