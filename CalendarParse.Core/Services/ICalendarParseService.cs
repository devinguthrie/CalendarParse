using CalendarParse.Models;

namespace CalendarParse.Services
{
    public interface ICalendarParseService
    {
        /// <summary>
        /// Full pipeline: preprocess → detect table → OCR → analyze → serialize.
        /// </summary>
        /// <param name="imageStream">Raw image bytes from camera or gallery.</param>
        /// <param name="nameFilter">If non-empty, result only includes the matching employee row.</param>
        /// <returns>JSON string of <see cref="CalendarData"/>, or an error message prefixed with "ERROR:".</returns>
        Task<string> ProcessAsync(Stream imageStream, string nameFilter, CancellationToken ct = default);

        /// <summary>
        /// Full pipeline plus bounding-box positions for overlay rendering.
        /// Default implementation calls <see cref="ProcessAsync"/> and returns empty bounds —
        /// override in <c>HybridCalendarService</c> to populate <see cref="ProcessWithBoundsResult.Shifts"/>.
        /// </summary>
        async Task<ProcessWithBoundsResult> ProcessWithBoundsAsync(
            Stream imageStream, string nameFilter, CancellationToken ct = default)
        {
            var json = await ProcessAsync(imageStream, nameFilter, ct);
            if (json.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                return new ProcessWithBoundsResult { Error = json["ERROR:".Length..].Trim() };

            return new ProcessWithBoundsResult { RawJson = json };
        }
    }

    /// <summary>
    /// Result of <see cref="ICalendarParseService.ProcessWithBoundsAsync"/>.
    /// Either <see cref="Error"/> is set, or <see cref="RawJson"/> + <see cref="Shifts"/> are set.
    /// </summary>
    public class ProcessWithBoundsResult
    {
        /// <summary>Raw JSON from ProcessAsync (CalendarData). Null on error.</summary>
        public string? RawJson { get; set; }

        /// <summary>
        /// Structured shifts with bounding boxes.
        /// Populated by HybridCalendarService; empty list for other implementations.
        /// </summary>
        public List<ShiftData> Shifts { get; set; } = [];

        /// <summary>Non-null when processing failed.</summary>
        public string? Error { get; set; }

        public bool IsError => Error is not null;

        /// <summary>Natural pixel width of the source image. 0 when unavailable.</summary>
        public int ImageWidth  { get; set; }

        /// <summary>Natural pixel height of the source image. 0 when unavailable.</summary>
        public int ImageHeight { get; set; }
    }
}
