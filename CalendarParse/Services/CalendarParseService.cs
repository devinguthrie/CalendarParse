using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarParse.Models;

namespace CalendarParse.Services
{
    public class CalendarParseService : ICalendarParseService
    {
        private readonly IImagePreprocessor _preprocessor;
        private readonly ITableDetector _tableDetector;
        private readonly IOcrService _ocrService;
        private readonly ICalendarStructureAnalyzer _analyzer;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public CalendarParseService(
            IImagePreprocessor preprocessor,
            ITableDetector tableDetector,
            IOcrService ocrService,
            ICalendarStructureAnalyzer analyzer)
        {
            _preprocessor = preprocessor;
            _tableDetector = tableDetector;
            _ocrService = ocrService;
            _analyzer = analyzer;
        }

        public async Task<string> ProcessAsync(Stream imageStream, string nameFilter, CancellationToken ct = default)
        {
            var debug = new StringBuilder();
            debug.AppendLine("════════════  DEBUG REPORT  ════════════");

            try
            {
                // ── 1. Read raw bytes once (stream can only be read once) ──────────
                var rawBytes = await ReadBytesAsync(imageStream, ct);
                debug.AppendLine($"[1] Raw image bytes: {rawBytes.Length}");

                // ── 2. OCR on original image (preserves color/resolution for ML Kit) 
                var ocrElements = await _ocrService.RecognizeAsync(rawBytes, ct);
                debug.AppendLine($"[2] OCR elements: {ocrElements.Count}");
                int previewCount = Math.Min(ocrElements.Count, 40);
                for (int i = 0; i < previewCount; i++)
                {
                    var e = ocrElements[i];
                    debug.AppendLine($"    [{i:D3}] \"{e.Text}\" conf={e.Confidence:F2} ({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})");
                }
                if (ocrElements.Count > previewCount)
                    debug.AppendLine($"    ... ({ocrElements.Count - previewCount} more elements)");

                // ── 3. Preprocess for grid detection ─────────────────────────────
                using var preprocessStream = new MemoryStream(rawBytes);
                var processedPng = await _preprocessor.PreprocessAsync(preprocessStream, ct);
                debug.AppendLine($"[3] Preprocessed PNG: {processedPng.Length} bytes");

                // ── 4. Detect table cell grid ─────────────────────────────────────
                var cells = await _tableDetector.DetectCellsAsync(processedPng, ct);
                int cellMaxRow = cells.Count > 0 ? cells.Max(c => c.Row) : -1;
                int cellMaxCol = cells.Count > 0 ? cells.Max(c => c.Col) : -1;
                debug.AppendLine($"[4] Detected cells: {cells.Count}  grid={cellMaxRow + 1}x{cellMaxCol + 1}");

                // ── 5. Analyze structure and assemble CalendarData ────────────────
                debug.AppendLine("[5] Analyzer:");
                var calendarData = _analyzer.Analyze(cells, ocrElements, debug);

                // ── 6. Apply name filter ──────────────────────────────────────────
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    calendarData.Employees = calendarData.Employees
                        .Where(e => e.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // ── 7. Serialize ──────────────────────────────────────────────────
                var json = JsonSerializer.Serialize(calendarData, JsonOptions);
                debug.AppendLine("════════════  END DEBUG  ════════════");
                return debug.ToString() + "\n" + json;
            }
            catch (PlatformNotSupportedException ex)
            {
                return $"ERROR: This feature requires Android. ({ex.Message})";
            }
            catch (Exception ex)
            {
                debug.AppendLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                debug.AppendLine(ex.StackTrace);
                return debug.ToString();
            }
        }

        private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }
}
