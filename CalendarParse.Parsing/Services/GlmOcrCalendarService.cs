using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalendarParse.Services;

namespace CalendarParse.Parsing.Services;

/// <summary>
/// ICalendarParseService implementation that uses GLM-OCR (glm-ocr via Ollama) for
/// pure OCR-based table extraction — no LLM reasoning step at all.
///
/// GLM-OCR uses a task-prefix prompt format:
///   prompt: "Table Recognition:"  (with image attached)
/// and outputs an HTML &lt;table&gt; containing the full schedule grid.
/// This service parses that HTML deterministically into the shift schema.
///
/// Architecture contrast with HybridCalendarService:
///   Hybrid: WinRT OCR fragments → LLM reasoning per strip → shift values
///   GlmOcr: GLM-OCR reads full table → deterministic HTML parser → shift values
///
/// Context size: must use num_ctx ≥ ~8192 (4096 causes vision patch dimension mismatch;
/// 262144 global OLLAMA_CONTEXT_LENGTH triggers ggml_nbytes > INT_MAX CUDA crash on
/// Windows/NVIDIA — cpy.cu:396).
/// </summary>
public class GlmOcrCalendarService : ICalendarParseService
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel   = "glm-ocr";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    // Extracts every <tr>...</tr> block
    private static readonly Regex TrRegex =
        new(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Extracts the inner text of every <td> or <th>
    private static readonly Regex TdRegex =
        new(@"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Matches a full M/D/YYYY date in a cell
    private static readonly Regex DateCellRegex =
        new(@"^\d{1,2}/\d{1,2}/\d{4}$", RegexOptions.Compiled);

    // Matches a time range like 9:00-5:30 or 10:00-6:30
    private static readonly Regex TimeRangeRegex =
        new(@"^\d{1,2}:\d{2}\s*[-\u2013]\s*\d{1,2}:\d{2}$", RegexOptions.Compiled);

    // Strip parenthetical annotations from names (e.g. "Athena(train)" → "Athena")
    private static readonly Regex ParenAnnotationRegex =
        new(@"\s*\(.*?\)", RegexOptions.Compiled);

    // Non-employee row identifiers (header/footer rows)
    private static readonly HashSet<string> NonEmployeeKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "BANK", "Volume", "Hours", "SPLH", "UPT", "ADT", "Olls/Vin", "OIL/VIN",
            "Negative inv check", "SUN", "MON", "TUE", "WED", "THURS", "FRI", "SAT",
        };

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly IOcrService? _ocrService;

    public GlmOcrCalendarService(
        string baseUrl      = DefaultBaseUrl,
        string model        = DefaultModel,
        IOcrService? ocrService = null)
    {
        _baseUrl    = baseUrl;
        _model      = model;
        _ocrService = ocrService;
    }

    // Ollama GLM-OCR crashes when the model generates runaway output (200+ rows) that fills
    // the 32768-token context window, triggering a KV-cache shift at a broken MRoPE state.
    // Empirical testing: 1344 and 1345 both produce compact output (28 rows, ~12s) 10/10 runs.
    // At 1400 the model switches to verbose/hallucinated output (238 rows, 68s) -> crash risk.
    // Root cause bug: https://github.com/ollama/ollama/issues/14171 (open).
    // WORKAROUND: pre-resize portrait images to <=1344px height before sending to GLM-OCR.
    // TODO: remove when https://github.com/ollama/ollama/issues/14171 is fixed.
    private const int MaxGlmOcrHeight = 1344;

    public async Task<string> ProcessAsync(Stream imageStream, string nameFilter, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await imageStream.CopyToAsync(ms, ct);
        byte[] imageBytes = ResizeForGlmOcrIfNeeded(ms.ToArray());
        string base64 = Convert.ToBase64String(imageBytes);

        string html = await CallGlmOcrAsync(base64, ct);
        if (html.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            return html;

        if (System.Environment.GetEnvironmentVariable("GLM_OCR_DIAG") == "1")
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetTempPath(), "glm-ocr-debug.html"), html, ct);

        var result = ParseHtmlTable(html);
        if (result is null)
            return "ERROR: GLM-OCR returned no parseable table headers";
        if (result.Employees.Count == 0)
            return "ERROR: GLM-OCR returned no parseable employee rows";

        if (_ocrService is not null)
            await CrossReferenceXMarksAsync(result, imageBytes, ct);

        return BuildJson(result, nameFilter);
    }

    // ── GLM-OCR API call ──────────────────────────────────────────────────────

    private async Task<string> CallGlmOcrAsync(string base64Image, CancellationToken ct)
    {
        // GLM-OCR task prefix format: "Table Recognition:" followed by the image.
        // num_ctx=32768 provides enough context for the full HTML table output.
        // Portrait images are pre-resized to ≤1344px before calling this method.
        // keep_alive=0 forces model unload after each request to prevent GPU/KV-cache
        // state from accumulating across sequential image calls, which otherwise triggers
        // the MRoPE assertion crash on portrait images.
        // num_predict=20000: hard-caps generation before the KV-cache shift fires.
        // The MRoPE assertion (Ollama #14171) triggers at step 32769 (first shift).
        // Valid verbose output for IM(3) is ~9,326 tokens; 20000 allows it to complete
        // while preventing the crash path (32768+ tokens) from reaching the shift.
        var requestBody = new
        {
            model      = _model,
            prompt     = "Table Recognition:",
            images     = new[] { base64Image },
            stream     = false,
            keep_alive = 0,
            options    = new { num_ctx = 32768, num_predict = 20000, temperature = 0 }
        };

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await Http.PostAsJsonAsync($"{_baseUrl}/api/generate", requestBody, ct);
        }
        catch (HttpRequestException ex)
        {
            return $"ERROR: GLM-OCR not reachable at {_baseUrl}.\n{ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "ERROR: GLM-OCR request timed out.";
        }

        string rawBody = await httpResponse.Content.ReadAsStringAsync(ct);

        if (!httpResponse.IsSuccessStatusCode)
            return $"ERROR: GLM-OCR HTTP {(int)httpResponse.StatusCode}: {rawBody[..Math.Min(300, rawBody.Length)]}";

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            return doc.RootElement.GetProperty("response").GetString() ?? "";
        }
        catch
        {
            return $"ERROR: Could not parse GLM-OCR response wrapper: {rawBody[..Math.Min(300, rawBody.Length)]}";
        }
    }

    private static byte[] ResizeForGlmOcrIfNeeded(byte[] raw)
    {
        // GLM_OCR_NO_RESIZE=1 bypasses the cap for experiments (e.g. #14114 num_ctx workaround tests)
        if (Environment.GetEnvironmentVariable("GLM_OCR_NO_RESIZE") == "1") return raw;
        using var src = new Mat();
        CvInvoke.Imdecode(raw, ImreadModes.ColorBgr, src);

        // For EXIF=6/8 the image will be rotated 90° by Ollama — effective height is the raw width.
        // IM(3): raw 1200×1600 (EXIF=8) → effectiveHeight=1200 < 1344 → return raw unchanged.
        // Returning raw preserves the EXIF tag so Ollama can orient the image correctly at full size.
        int orientation = ReadJpegExifOrientation(raw);
        int effectiveHeight = (orientation == 6 || orientation == 8) ? src.Width : src.Height;
        if (effectiveHeight <= MaxGlmOcrHeight) return raw;

        double scale   = (double)MaxGlmOcrHeight / src.Height;
        var    newSize = new System.Drawing.Size((int)(src.Width * scale), MaxGlmOcrHeight);
        using var resized = new Mat();
        CvInvoke.Resize(src, resized, newSize, 0, 0, Inter.Area);

        var output = new VectorOfByte();
        CvInvoke.Imencode(".jpg", resized, output);
        return output.ToArray();
    }

    // Minimal JPEG EXIF orientation reader. Returns 1 (normal) if absent or unreadable.
    private static int ReadJpegExifOrientation(byte[] data)
    {
        if (data.Length < 12 || data[0] != 0xFF || data[1] != 0xD8) return 1;

        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) break;
            byte marker = data[pos + 1];
            int segLen = (data[pos + 2] << 8) | data[pos + 3];

            if (marker == 0xE1 && segLen > 6 && pos + 10 <= data.Length &&
                data[pos + 4] == 'E' && data[pos + 5] == 'x' && data[pos + 6] == 'i' &&
                data[pos + 7] == 'f' && data[pos + 8] == 0   && data[pos + 9] == 0)
            {
                int tiff = pos + 10;
                if (tiff + 8 > data.Length) break;
                bool le = data[tiff] == 'I';

                int ifd0Offset = ExifInt32(data, tiff + 4, le);
                int ifd0 = tiff + ifd0Offset;
                if (ifd0 + 2 > data.Length) break;
                int count = ExifUInt16(data, ifd0, le);

                for (int i = 0; i < count; i++)
                {
                    int ep = ifd0 + 2 + i * 12;
                    if (ep + 12 > data.Length) break;
                    if (ExifUInt16(data, ep, le) == 0x0112)
                        return ExifUInt16(data, ep + 8, le);
                }
            }

            if (marker == 0xD9 || marker == 0xDA) break;
            pos += 2 + segLen;
        }
        return 1;
    }

    private static int ExifUInt16(byte[] d, int o, bool le) =>
        le ? (d[o] | (d[o + 1] << 8)) : ((d[o] << 8) | d[o + 1]);

    private static int ExifInt32(byte[] d, int o, bool le) =>
        le ? (d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24))
           : ((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    // ── HTML table parser ─────────────────────────────────────────────────────

    internal static HtmlParseResult? ParseHtmlTable(string html)
    {
        // Extract all rows and their cells
        var allRows = TrRegex.Matches(html)
            .Select(m => TdRegex.Matches(m.Groups[1].Value)
                .Select(td => td.Groups[1].Value.Trim())
                .ToList())
            .ToList();

        // Find the date row: first row with ≥2 cells matching M/D/YYYY
        int dateRowIdx = -1;
        int[] dateCols = [];
        string[] dateValues = [];

        for (int i = 0; i < allRows.Count; i++)
        {
            var hits = allRows[i]
                .Select((cell, idx) => (cell, idx))
                .Where(x => DateCellRegex.IsMatch(x.cell))
                .ToArray();

            if (hits.Length >= 2)
            {
                dateRowIdx  = i;
                dateCols    = hits.Select(h => h.idx).ToArray();
                dateValues  = hits.Select(h => h.cell).ToArray();
                break;
            }
        }

        if (dateRowIdx < 0) return null;

        // Determine the day-of-week offsets for each date column (0=Sun … 6=Sat).
        // Look for the row immediately before the date row that has ≥ 4 day-name
        // abbreviations (SUN/MON/TUE/TUES/WED/THURS/THU/FRI/SAT).
        int[] dayOffsets = GetDayOffsets(allRows, dateRowIdx, dateCols);

        // Majority-vote anchor: compute the "week-start Sunday" implied by each date.
        // This corrects single OCR misreads (e.g., 10/26 → 10/28) without rejecting
        // the entire row, because the majority of dates are usually correct.
        var sundayCounts = new Dictionary<DateOnly, int>();
        for (int k = 0; k < dateValues.Length; k++)
        {
            if (!TryParseMDY(dateValues[k], out DateOnly d)) continue;
            DateOnly sunday = d.AddDays(-dayOffsets[k]);
            sundayCounts[sunday] = sundayCounts.GetValueOrDefault(sunday) + 1;
        }
        if (sundayCounts.Count == 0) return null;

        DateOnly anchorSunday = sundayCounts.MaxBy(kv => kv.Value).Key;

        // Rebuild isoDates using the anchor Sunday + each column's day offset
        string[] isoDates = dayOffsets
            .Select(off => anchorSunday.AddDays(off).ToString("yyyy-MM-dd"))
            .ToArray();

        // Derive month name and year from the first date
        var firstDate = DateOnly.ParseExact(isoDates[0], "yyyy-MM-dd");
        string monthName = firstDate.ToString("MMMM");
        int    year      = firstDate.Year;

        // Extract employee rows (rows after the date row)
        var employees = new List<HtmlEmployee>();
        for (int i = dateRowIdx + 1; i < allRows.Count; i++)
        {
            var row = allRows[i];
            if (row.Count < 2) continue;

            string rawName = row[0].Trim();
            if (string.IsNullOrWhiteSpace(rawName)) continue;
            // Skip purely numeric rows (year totals, sales targets)
            if (double.TryParse(rawName, out _)) continue;
            // Skip known header/footer keywords
            if (NonEmployeeKeywords.Contains(rawName)) continue;

            // Strip parenthetical training annotations: "Athena(train)" → "Athena"
            string name = ParenAnnotationRegex.Replace(rawName, "").Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Sliding-window extraction: handles days where GLM-OCR outputs only
            // 1 HTML cell (e.g., a store-closed holiday) instead of the normal 2
            // (shift + hours).  Detection: if cell[col+1] looks like a shift value
            // (time range, "x", PTO, RTO) rather than an hours number, the current
            // day used only 1 cell, so advance by 1 instead of 2.
            var shifts = new List<HtmlShift>(isoDates.Length);
            int col = 1; // start after name column
            for (int day = 0; day < isoDates.Length && col < row.Count; day++)
            {
                string shiftCell = row[col];
                bool oneCell = col + 1 < row.Count && IsShiftLike(row[col + 1]);
                shifts.Add(new HtmlShift(isoDates[day], NormalizeShiftValue(shiftCell)));
                col += oneCell ? 1 : 2;
            }
            employees.Add(new HtmlEmployee(name, shifts));
        }

        return new HtmlParseResult(monthName, year, employees);
    }

    // ── JSON output builder ───────────────────────────────────────────────────

    private static string BuildJson(HtmlParseResult result, string nameFilter)
    {
        var employees = string.IsNullOrWhiteSpace(nameFilter)
            ? result.Employees
            : result.Employees
                .Where(e => e.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        using var ms     = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("Month", result.Month);
        writer.WriteNumber("Year",  result.Year);
        writer.WriteStartArray("Employees");
        foreach (var emp in employees)
        {
            writer.WriteStartObject();
            writer.WriteString("Name", emp.Name);
            writer.WriteStartArray("Shifts");
            foreach (var s in emp.Shifts)
            {
                writer.WriteStartObject();
                writer.WriteString("Date",  s.Date);
                writer.WriteString("Shift", s.Shift);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Map day-name abbreviations to 0-based Sunday offsets (Sun=0 … Sat=6).
    private static readonly Dictionary<string, int> DayNameOffset =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "SUN", 0 }, { "MON", 1 }, { "TUE", 2 }, { "TUES", 2 },
            { "WED", 3 }, { "THURS", 4 }, { "THU", 4 }, { "FRI", 5 }, { "SAT", 6 }
        };

    // Returns the 0-based day-of-week offset (Sun=0 … Sat=6) for each column in dateCols.
    // Looks for the row just before dateRowIdx that has ≥4 day-name abbreviations.
    // Falls back to positional ordering [0, 1, 2, …, n-1] when no such row exists.
    private static int[] GetDayOffsets(List<List<string>> allRows, int dateRowIdx, int[] dateCols)
    {
        // Search up to 3 rows above the date row for a day-names row
        for (int r = dateRowIdx - 1; r >= Math.Max(0, dateRowIdx - 3); r--)
        {
            var row = allRows[r];
            int hits = row.Count(c => DayNameOffset.ContainsKey(c));
            if (hits < 4) continue;

            // Build offsets at the same column positions used for dates
            var offsets = new int[dateCols.Length];
            for (int k = 0; k < dateCols.Length; k++)
            {
                int col = dateCols[k];
                string cell = col < row.Count ? row[col] : "";
                offsets[k] = DayNameOffset.TryGetValue(cell, out int off) ? off : k;
            }
            return offsets;
        }

        // No day-names row found: assume columns map to Sun (0) … Sat (6) in order
        return Enumerable.Range(0, dateCols.Length).ToArray();
    }

    // Parse a cell that may be "M/D/YYYY" into a DateOnly. Returns false on failure.
    private static bool TryParseMDY(string s, out DateOnly result)
    {
        result = default;
        var parts = s.Split('/');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int mo) ||
            !int.TryParse(parts[1], out int dy) ||
            !int.TryParse(parts[2], out int yr)) return false;
        try { result = new DateOnly(yr, mo, dy); return true; }
        catch { return false; }
    }

    // Returns true if a cell looks like a shift value (not an hours number).
    // Used by the sliding-window extractor to detect 1-cell collapsed days.
    private static bool IsShiftLike(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (TimeRangeRegex.IsMatch(s)) return true;
        return s.Equals("xx",  StringComparison.OrdinalIgnoreCase)
            || s.Equals("x",   StringComparison.OrdinalIgnoreCase)
            || s.Equals("PTO", StringComparison.OrdinalIgnoreCase)
            || s.Equals("RTO", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeShiftValue(string raw)
    {
        raw = raw.Trim();
        if (string.IsNullOrEmpty(raw) || raw == "-" || raw == "—") return "";

        // Normalize unicode dashes
        raw = raw.Replace('\u2013', '-').Replace('\u2014', '-');

        if (raw.Equals("RTO", StringComparison.OrdinalIgnoreCase)) return "RTO";
        if (raw.Equals("PTO", StringComparison.OrdinalIgnoreCase)) return "PTO";
        if (raw.Equals("xx",  StringComparison.OrdinalIgnoreCase)) return "xx";
        if (raw.Equals("x",   StringComparison.OrdinalIgnoreCase)) return "x";

        // Accept time ranges
        if (TimeRangeRegex.IsMatch(raw)) return raw;

        // Discard hours numbers (e.g. "8", "4.5") and other noise
        return "";
    }

    // ── X-mark cross-reference (App SDK TextRecognizer) ─────────────────────

    /// <summary>
    /// Scans the image with the injected OCR service to locate standalone "x" or "xx" marks.
    /// For any GLM-OCR time-range that has an x-mark token nearby, overrides the shift to "x".
    /// This targets hand-drawn x-marks on printed calendars that GLM-OCR misreads as time ranges.
    /// </summary>
    private async Task CrossReferenceXMarksAsync(
        HtmlParseResult result, byte[] imageBytes, CancellationToken ct)
    {
        List<OcrElement> elements;
        try
        {
            elements = await _ocrService!.RecognizeAsync(imageBytes, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[x-ref] OCR failed: {ex.Message}");
            return;
        }
        if (elements.Count == 0) return;

        // ── 1. Approximate image width to identify the name column ─────────────────
        using var mat = new Emgu.CV.Mat();
        Emgu.CV.CvInvoke.Imdecode(imageBytes, Emgu.CV.CvEnum.ImreadModes.Grayscale, mat);
        float imgWidth = mat.Width > 0 ? mat.Width : elements.Max(e => e.Bounds.X + e.Bounds.Width);

        // Name column occupies the leftmost 18% of the image
        float nameColBoundary = imgWidth * 0.18f;

        // ── 2. Build ISO date → column x-center dictionary ────────────────────────
        var dateColX = new Dictionary<string, float>();
        foreach (var el in elements)
        {
            if (!DateCellRegex.IsMatch(el.Text)) continue;
            if (!TryParseMDY(el.Text, out DateOnly d)) continue;
            string iso = d.ToString("yyyy-MM-dd");
            if (!dateColX.ContainsKey(iso))
                dateColX[iso] = el.Bounds.CenterX;
        }
        if (dateColX.Count < 2) return; // need at least 2 column anchors

        // ── 3. Build employee name → row y-center dictionary ──────────────────────
        var nameRowY = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var employeeNames = result.Employees
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var el in elements)
        {
            if (el.Bounds.X > nameColBoundary) continue;
            foreach (var empName in employeeNames)
            {
                if (empName.Contains(el.Text, StringComparison.OrdinalIgnoreCase) ||
                    el.Text.Contains(empName, StringComparison.OrdinalIgnoreCase))
                {
                    nameRowY.TryAdd(empName, el.Bounds.CenterY);
                    break;
                }
            }
        }
        if (nameRowY.Count == 0) return;

        // ── 4. Collect x-mark candidates ──────────────────────────────────────────
        var xMarks = elements
            .Where(el => el.Text.Equals("x",  StringComparison.OrdinalIgnoreCase) ||
                         el.Text.Equals("xx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (xMarks.Count == 0) return;

        // ── 5. Estimate column width and row height from median gaps ───────────────
        float colWidth  = EstimateMedianGap(dateColX.Values.OrderBy(v => v).ToList());
        float rowHeight = EstimateMedianGap(nameRowY.Values.OrderBy(v => v).ToList());
        if (colWidth < 1 || rowHeight < 1) return;

        // ── 6. Override time-ranges that have a nearby x-mark token ───────────────
        float tolX = colWidth  * 0.60f;
        float tolY = rowHeight * 0.60f;

        foreach (var emp in result.Employees)
        {
            if (!nameRowY.TryGetValue(emp.Name, out float empY)) continue;
            for (int i = 0; i < emp.Shifts.Count; i++)
            {
                var shift = emp.Shifts[i];
                if (!TimeRangeRegex.IsMatch(shift.Shift)) continue;
                if (!dateColX.TryGetValue(shift.Date, out float dateX)) continue;

                bool hasXMark = xMarks.Any(x =>
                    Math.Abs(x.Bounds.CenterX - dateX) < tolX &&
                    Math.Abs(x.Bounds.CenterY - empY)  < tolY);

                if (hasXMark)
                {
                    Console.WriteLine($"[x-ref] {emp.Name} {shift.Date}: {shift.Shift} → x");
                    emp.Shifts[i] = shift with { Shift = "x" };
                }
            }
        }
    }

    private static float EstimateMedianGap(List<float> sortedValues)
    {
        if (sortedValues.Count < 2) return 0;
        var gaps = new List<float>(sortedValues.Count - 1);
        for (int i = 1; i < sortedValues.Count; i++)
            gaps.Add(sortedValues[i] - sortedValues[i - 1]);
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    // ── Data types ────────────────────────────────────────────────────────────

    internal record HtmlShift(string Date, string Shift);
    internal record HtmlEmployee(string Name, List<HtmlShift> Shifts);
    internal record HtmlParseResult(string Month, int Year, List<HtmlEmployee> Employees);
}