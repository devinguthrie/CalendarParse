using CalendarParse.Models;
using Rect = CalendarParse.Models.Rect;
using System.Text;

namespace CalendarParse.Services
{
    /// <summary>Preprocesses a raw image bitmap for table detection using OpenCV (Emgu.CV).</summary>
    public interface IImagePreprocessor
    {
        /// <summary>
        /// Reads <paramref name="imageStream"/>, applies grayscale + blur + adaptive threshold,
        /// and returns the processed image as a PNG byte array.
        /// </summary>
        Task<byte[]> PreprocessAsync(Stream imageStream, CancellationToken ct = default);

        /// <summary>
        /// Rotates <paramref name="imageBytes"/> by <paramref name="degreesClockwise"/> (90, 180, or 270)
        /// and returns the rotated image as a JPEG byte array.
        /// </summary>
        Task<byte[]> RotateAsync(byte[] imageBytes, int degreesClockwise, CancellationToken ct = default);
    }

    /// <summary>Detects grid lines in a preprocessed image and returns an ordered cell matrix.</summary>
    public interface ITableDetector
    {
        /// <summary>
        /// Analyzes <paramref name="preprocessedPng"/> for horizontal and vertical lines,
        /// finds cell bounding boxes, and returns them sorted into rows and columns.
        /// Returns an empty list if no grid is detected.
        /// </summary>
        Task<List<TableCell>> DetectCellsAsync(byte[] preprocessedPng, CancellationToken ct = default);
    }

    /// <summary>Runs OCR and returns text with bounding rectangles.</summary>
    public interface IOcrService
    {
        /// <summary>
        /// Runs text recognition on <paramref name="imageBytes"/> (original, unprocessed PNG/JPEG).
        /// Returns a flat list of recognized text elements with their image-coordinate bounding boxes.
        /// </summary>
        Task<List<OcrElement>> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default);
    }

    /// <summary>Infers calendar structure from OCR'd cells and builds the data model.</summary>
    public interface ICalendarStructureAnalyzer
    {
        /// <summary>
        /// Given a grid of <see cref="TableCell"/> objects (with text populated) and the original
        /// <see cref="OcrElement"/> list, identifies the date header row and employee name column,
        /// extrapolates all dates from an anchor date, and assembles a <see cref="CalendarData"/> object.
        /// Pass a <see cref="StringBuilder"/> as <paramref name="debug"/> to collect step-by-step diagnostics.
        /// </summary>
        CalendarData Analyze(List<TableCell> cells, List<OcrElement> ocrElements, StringBuilder? debug = null);
    }

    /// <summary>A single recognized text element from OCR, with location in image space.</summary>
    public class OcrElement
    {
        public string Text { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public Rect Bounds { get; set; }
    }
}
