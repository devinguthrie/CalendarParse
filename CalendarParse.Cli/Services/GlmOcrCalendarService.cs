using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalendarParse.Services;

namespace CalendarParse.Cli.Services;

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

    public GlmOcrCalendarService(
        string baseUrl = DefaultBaseUrl,
        string model   = DefaultModel)
    {
        _baseUrl = baseUrl;
        _model   = model;
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

        var result = ParseHtmlTable(html);
        if (result is null)
            return "ERROR: GLM-OCR returned no parseable table headers";
        if (result.Employees.Count == 0)
            return "ERROR: GLM-OCR returned no parseable employee rows";

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
            options    = new { num_ctx = 32768, num_predict = 20000 }
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
        using var src = new Mat();
        CvInvoke.Imdecode(raw, ImreadModes.ColorBgr, src);
        if (src.Height <= MaxGlmOcrHeight) return raw;

        double scale   = (double)MaxGlmOcrHeight / src.Height;
        var    newSize = new System.Drawing.Size((int)(src.Width * scale), MaxGlmOcrHeight);
        using var resized = new Mat();
        CvInvoke.Resize(src, resized, newSize, 0, 0, Inter.Area);

        var output = new VectorOfByte();
        CvInvoke.Imencode(".jpg", resized, output);
        return output.ToArray();
    }

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
        return s.Equals("x",   StringComparison.OrdinalIgnoreCase)
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
        if (raw.Equals("x",   StringComparison.OrdinalIgnoreCase)) return "x";

        // Accept time ranges
        if (TimeRangeRegex.IsMatch(raw)) return raw;

        // Discard hours numbers (e.g. "8", "4.5") and other noise
        return "";
    }

    // ── Data types ────────────────────────────────────────────────────────────

    internal record HtmlShift(string Date, string Shift);
    internal record HtmlEmployee(string Name, List<HtmlShift> Shifts);
    internal record HtmlParseResult(string Month, int Year, List<HtmlEmployee> Employees);
}