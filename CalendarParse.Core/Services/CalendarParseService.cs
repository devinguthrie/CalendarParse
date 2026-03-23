using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

                // ── 2. OCR on original image ──────────────────────────────────────
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

                // ── 2b. Rotation check using aspect-ratio-guided candidate angles ──
                {
                    // Determine text orientation from bounding box aspect ratios.
                    // Horizontal text (correct or 180°): width > height (ratio > 1).
                    // Vertical text (90° or 270° rotated): width < height (ratio < 1).
                    var aspects = ocrElements
                        .Where(e => e.Confidence > 0.1f && e.Bounds.Height > 0 && e.Bounds.Width > 0)
                        .Select(e => (float)e.Bounds.Width / e.Bounds.Height)
                        .OrderBy(r => r)
                        .ToList();

                    float medianAspect = aspects.Count > 0 ? aspects[aspects.Count / 2] : 1.0f;

                    // If too few elements, try all angles; otherwise use aspect ratio to guide.
                    int[] candidates = (aspects.Count < 5 || medianAspect < 0.7f)
                        ? new[] { 90, 270 }          // clearly vertical text → try 90°/270°
                        : (medianAspect <= 1.5f)
                        ? new[] { 90, 180, 270 }     // ambiguous aspect → try all rotations
                        : new[] { 180 };             // clearly horizontal text → only check upside-down

                    int bestScore = ReadabilityScore(ocrElements);
                    int bestAngle = 0;
                    byte[] bestBytes = rawBytes;
                    List<OcrElement> bestOcr = ocrElements;
                    debug.AppendLine($"[2b] Median aspect ratio: {medianAspect:F2}  checking angles: [{string.Join(", ", candidates)}°]  score@0°: {bestScore}");

                    foreach (int angle in candidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        var rotated = await _preprocessor.RotateAsync(rawBytes, angle, ct);
                        var rotatedOcr = await _ocrService.RecognizeAsync(rotated, ct);
                        int score = ReadabilityScore(rotatedOcr);
                        debug.AppendLine($"[2b] Score at {angle}°: {score}");
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestAngle = angle;
                            bestBytes = rotated;
                            bestOcr = rotatedOcr;
                        }
                    }

                    if (bestAngle != 0)
                    {
                        debug.AppendLine($"[2b] Rotating image {bestAngle}° (best score: {bestScore})");
                        rawBytes = bestBytes;
                        ocrElements = bestOcr;
                    }
                    else
                    {
                        debug.AppendLine($"[2b] Keeping original orientation (best score: {bestScore})");
                    }
                }

                // ── 3. Preprocess for grid detection ─────────────────────────────
                using var preprocessStream = new MemoryStream(rawBytes);
                var processedPng = await _preprocessor.PreprocessAsync(preprocessStream, ct);
                debug.AppendLine($"[3] Preprocessed PNG: {processedPng.Length} bytes");

                // ── 4. Detect table cell grid ─────────────────────────────────────
                var cells = await _tableDetector.DetectCellsAsync(processedPng, ct);
                int cellMaxRow = cells.Count > 0 ? cells.Max(c => c.Row) : -1;
                int cellMaxCol = cells.Count > 0 ? cells.Max(c => c.Col) : -1;
                debug.AppendLine($"[4] Detected cells: {cells.Count}  grid={cellMaxRow + 1}x{cellMaxCol + 1}");

                // ── 4b. Fallback: synthesize grid from OCR bounding boxes ─────────
                // A valid weekly schedule needs at least 7 columns (SUN–SAT + name).
                // Treat sparse or narrow detected grids as failures and use synthesis.
                bool gridTooSparse = cells.Count < 20 || cellMaxCol < 6;
                if (gridTooSparse && ocrElements.Count > 5)
                {
                    debug.AppendLine("[4b] Grid too sparse — synthesizing cell grid from OCR layout.");
                    cells = SynthesizeCellsFromOcr(ocrElements);
                    debug.AppendLine($"[4b] Synthesized {cells.Count} cells from OCR elements.");
                }

                // ── 5. Analyze structure and assemble CalendarData ────────────────
                debug.AppendLine("[5] Analyzer:");
                var calendarData = _analyzer.Analyze(cells, ocrElements, debug);

                // ── 6. Apply name filter ──────────────────────────────────────────
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    // Bidirectional: "Fran" matches filter "Franny" and vice versa.
                    // Strip spaces from both sides to handle OCR-split names like "F ranny".
                    var filterNoSpace = nameFilter.Replace(" ", "");
                    calendarData.Employees = calendarData.Employees
                        .Where(e =>
                        {
                            var nameNoSpace = e.Name.Replace(" ", "");
                            return nameNoSpace.Contains(filterNoSpace, StringComparison.OrdinalIgnoreCase)
                                || filterNoSpace.Contains(nameNoSpace, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                }

                // ── 7. Serialize ──────────────────────────────────────────────────
                var json = JsonSerializer.Serialize(calendarData, JsonOptions);
                debug.AppendLine("════════════  END DEBUG  ════════════");
                return debug.ToString() + "\n" + json;
            }
            catch (Exception ex)
            {
                debug.AppendLine($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                debug.AppendLine(ex.StackTrace);
                return debug.ToString();
            }
        }

        // Day/month names and time ranges are strong signals of correct orientation.
        private static readonly Regex DayNameRx = new(
            @"^(sun|mon|tue|tues|wed|thu|thur|thurs|fri|sat)(day)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MonthNameRx = new(
            @"^(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)(uary|ruary|ch|il|e|y|ust|tember|ober|ember)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TimeRangeRx = new(
            @"^\d{1,2}:\d{2}-\d{1,2}:\d{2}$",
            RegexOptions.Compiled);
        private static readonly Regex CalendarAbbrevRx = new(
            @"^(pto|rto|off|vac|otr)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static int ReadabilityScore(List<OcrElement> elements)
        {
            var confident = elements.Where(e => e.Confidence >= 0.4f).ToList();

            int baseScore = confident.Sum(e =>
            {
                var text = e.Text.Trim();
                if (DayNameRx.IsMatch(text) || MonthNameRx.IsMatch(text)) return 10;
                if (TimeRangeRx.IsMatch(text) || CalendarAbbrevRx.IsMatch(text)) return 5;
                if (Regex.IsMatch(text, @"[A-Za-z]{2,}")) return 1;
                return 0;
            });

            // Strong orientation signal: multiple distinct day names in the same horizontal band.
            // In a correctly-oriented schedule, day headers (SUN–SAT) form a single row.
            // When the image is rotated 90°/270°, those day names become a vertical column
            // and spread across very different y-coordinates.
            var dayNameElems = confident
                .Where(e => DayNameRx.IsMatch(e.Text.Trim()))
                .ToList();
            if (dayNameElems.Count >= 2)
            {
                int colinearMax = dayNameElems
                    .GroupBy(e => (e.Bounds.Y + e.Bounds.Height / 2) / 60)
                    .Select(g => g
                        .Select(e => e.Text.Trim().ToUpperInvariant()[..Math.Min(3, e.Text.Trim().Length)])
                        .Distinct()
                        .Count())
                    .Max();
                baseScore += colinearMax * 100;
            }

            return baseScore;
        }

        private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }

        /// <summary>
        /// Fallback: when explicit grid detection finds no cells, infer a virtual grid
        /// from the spatial layout of OCR bounding boxes.
        /// Uses high-confidence multi-char elements to define cluster centers,
        /// then assigns all elements to the nearest cluster.
        /// </summary>
        private static List<TableCell> SynthesizeCellsFromOcr(List<OcrElement> elements)
        {
            var all = elements
                .Where(e => e.Text.Trim().Length > 0 && e.Bounds.Width > 0 && e.Bounds.Height > 0)
                .OrderBy(e => e.Bounds.CenterY)
                .ToList();

            if (all.Count == 0) return new List<TableCell>();

            // Use only reliable elements to define cluster centers.
            // This prevents noise chars like "|", "[", "8" from creating spurious clusters.
            var anchors = all.Where(e => e.Confidence >= 0.3f && e.Text.Trim().Length >= 2).ToList();
            if (anchors.Count < 3) anchors = all;

            int avgH = anchors.Sum(e => e.Bounds.Height) / anchors.Count;
            int avgW = anchors.Sum(e => e.Bounds.Width) / anchors.Count;
            int rowTol = Math.Max(avgH, 10);
            int colTol = Math.Max(avgW, 30);

            // ── Row centers from anchors only (fixed, no drift) ───────────────
            var rowCenters = new List<int>();
            foreach (var elem in anchors.OrderBy(e => e.Bounds.CenterY))
            {
                bool placed = false;
                for (int i = 0; i < rowCenters.Count; i++)
                {
                    if (Math.Abs(rowCenters[i] - elem.Bounds.CenterY) <= rowTol)
                    { placed = true; break; }
                }
                if (!placed) rowCenters.Add(elem.Bounds.CenterY);
            }
            rowCenters.Sort();

            // ── Column centers from anchors only (fixed, no drift) ────────────
            var colCenters = new List<int>();
            foreach (int x in anchors.Select(e => e.Bounds.CenterX).OrderBy(x => x))
            {
                bool placed = false;
                for (int i = 0; i < colCenters.Count; i++)
                {
                    if (Math.Abs(colCenters[i] - x) <= colTol)
                    { placed = true; break; }
                }
                if (!placed) colCenters.Add(x);
            }
            colCenters.Sort();

            if (rowCenters.Count == 0 || colCenters.Count == 0) return new List<TableCell>();

            // ── Assign ALL elements to nearest row/col center ─────────────────
            var groups = new Dictionary<(int row, int col), List<OcrElement>>();
            foreach (var elem in all)
            {
                int rowIdx = rowCenters
                    .Select((cy, i) => (dist: Math.Abs(cy - elem.Bounds.CenterY), idx: i))
                    .OrderBy(t => t.dist).First().idx;
                int colIdx = colCenters
                    .Select((cx, i) => (dist: Math.Abs(cx - elem.Bounds.CenterX), idx: i))
                    .OrderBy(t => t.dist).First().idx;

                var key = (rowIdx, colIdx);
                if (!groups.TryGetValue(key, out var grp))
                    groups[key] = grp = new List<OcrElement>();
                grp.Add(elem);
            }

            // ── Build TableCell for each occupied (row, col) position ─────────
            var cells = new List<TableCell>();
            foreach (var ((row, col), group) in groups.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
            {
                int minX = group.Min(e => e.Bounds.X);
                int minY = group.Min(e => e.Bounds.Y);
                int maxX = group.Max(e => e.Bounds.X + e.Bounds.Width);
                int maxY = group.Max(e => e.Bounds.Y + e.Bounds.Height);

                // Leave Text empty — MapOcrToCells in the analyzer will populate it
                cells.Add(new TableCell
                {
                    Row = row,
                    Col = col,
                    Bounds = new Models.Rect(minX, minY, maxX - minX, maxY - minY)
                });
            }

            return cells;
        }
    }
}
