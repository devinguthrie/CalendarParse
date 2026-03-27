using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalendarParse.Models;
using CalendarParse.Services;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;

namespace CalendarParse.Cli.Services;

/// <summary>
/// Hybrid ICalendarParseService that eliminates WRONG-COL by construction.
/// <para>
/// Strategy:
/// 1. LLM pass on full image → header (month/year/dates) + employee names.
/// 2. OpenCV grid detection → column pixel boundaries.
/// 3. For each of the 7 day columns: crop a narrow strip containing ONLY the
///    employee-name column and that one day column, then query the LLM on the
///    strip.  Because there is exactly ONE shift column visible, the model
///    cannot place a value in the wrong column.
/// 4. WinRT OCR pre-fill: before the 7 strip LLM calls, run Windows WinRT OCR
///    on the full image and directly accept any cells whose combined text
///    matches a time-range pattern.  Those cells skip the LLM call entirely.
/// 5. X-marks clarification pass on full image (same as OllamaCalendarService).
/// </para>
/// Falls back to full-image LLM per-column if the grid detector cannot locate
/// at least 4 of the 7 day columns.
/// </summary>
public sealed class HybridCalendarService : ICalendarParseService
{
    // Time range in a cell: "9:00-5:30", "10:00-6:30" etc.
    // Allow en-dash as well.  Anchored so "9:00-5:30 7.5" does NOT match.
    private static readonly Regex TimeRangeRegex = new(
        @"^\d{1,2}:\d{2}\s*[-\u2013]\s*\d{1,2}:\d{2}$", RegexOptions.Compiled);

    // Strip a trailing hours number from a shift string, e.g. "9:00-5:30 7.5" → "9:00-5:30"
    private static readonly Regex TrailingHours = new(
        @"\s+\d+(\.\d+)?$", RegexOptions.Compiled);

    // Date format found in calendar header rows: M/D (e.g. "9/21") or M/D/YY (e.g. "9/21/25").
    private static readonly Regex OcrDatePattern =
        new(@"^(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?$", RegexOptions.Compiled);

    private static readonly string[] MonthNames =
    {
        "", "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    };

    // Maps day-name tokens found by OCR to their 0-based day index (0=Sun…6=Sat).
    // Includes full names, 3-letter abbreviations, 2-letter variants, and common
    // OCR mis-reads / alternate spellings to maximise header detection.
    private static readonly Dictionary<string, int> DayIndexMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        {"sun", 0}, {"sunday",    0}, {"su",   0},
        {"mon", 1}, {"monday",    1}, {"mo",   1},
        {"tue", 2}, {"tuesday",   2}, {"tu",   2}, {"tues",  2},
        {"wed", 3}, {"wednesday", 3}, {"we",   3}, {"weds",  3},
        {"thu", 4}, {"thursday",  4}, {"th",   4}, {"thur",  4}, {"thurs", 4},
        {"fri", 5}, {"friday",    5}, {"fr",   5},
        {"sat", 6}, {"saturday",  6}, {"sa",   6}, {"satur", 6},
    };

    // 3-character prefixes used for prefix-based day header matching
    // (handles tokens like "Tue28" or "Thu/4" produced by WinRT OCR).
    private static readonly (string Prefix, int Idx)[] DayPrefixes =
    [
        ("sun", 0), ("mon", 1), ("tue", 2), ("wed", 3),
        ("thu", 4), ("fri", 5), ("sat", 6),
    ];

    private readonly OllamaCalendarService _ollama;
    private readonly WindowsImagePreprocessor _preprocessor;
    private readonly WindowsTableDetector _tableDetector;
    private readonly WindowsWinRtOcrService _winRtOcr;

    public HybridCalendarService(
        string baseUrl = OllamaCalendarService.DefaultBaseUrl,
        string model   = OllamaCalendarService.DefaultModel,
        IEnumerable<string>? knownNames = null)
    {
        _ollama      = new OllamaCalendarService(baseUrl, model, knownNames);
        _preprocessor = new WindowsImagePreprocessor();
        _tableDetector = new WindowsTableDetector();
        _winRtOcr    = new WindowsWinRtOcrService();
    }

    public async Task<string> ProcessAsync(
        Stream imageStream, string nameFilter, CancellationToken ct = default)
    {
        await _ollama.EnsureModelLoadedAsync(ct);

        // ── Read raw bytes once ───────────────────────────────────────────────
        using var ms = new MemoryStream();
        await imageStream.CopyToAsync(ms, ct);
        byte[] rawBytes = ms.ToArray();
        string base64   = Convert.ToBase64String(rawBytes);

        var sw = Stopwatch.StartNew();
        string T() => $"+{sw.Elapsed.TotalSeconds:F0}s";

        // ── Pass 1: header (LLM on full image) ───────────────────────────────
        var (month, year, dates) = await _ollama.ExtractHeaderAsync(base64, ct);
        Console.Error.WriteLine(
            $"    [{T()}] pass 1/4: header ({month} {year}, {dates.Count} dates)");

        // ── Pass 2: employee names (LLM on full image) ────────────────────────
        var names = await _ollama.ExtractNamesAsync(base64, ct);
        if (_ollama._knownNames.Count > 0)
            names = _ollama.NormalizeNamesAgainstKnown(names);
        if (!string.IsNullOrWhiteSpace(nameFilter))
            names = names
                .Where(n => n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        Console.Error.WriteLine(
            $"    [{T()}] pass 2/4: names ({names.Count} employees)");

        // ── WinRT OCR on original image ───────────────────────────────────────
        var ocrElements = await _winRtOcr.RecognizeAsync(rawBytes, ct);
        Console.Error.WriteLine(
            $"    [{T()}] ocr: {ocrElements.Count} elements");

        // ── Compute image dimensions ──────────────────────────────────────────
        int imageWidth, imageHeight;
        {
            using var tmp = new Mat();
            CvInvoke.Imdecode(rawBytes, ImreadModes.Grayscale, tmp);
            imageWidth  = tmp.Width;
            imageHeight = tmp.Height;
        }

        // ── Derive day column bounds from OCR day-name headers ────────────────
        // Returns a dictionary keyed by day index (0=Sun–6=Sat) so that even if
        // some day headers aren’t detected, the ones we do find go to the correct slot.
        var dayColBounds  = ComputeDayColBoundsFromOcr(ocrElements, imageWidth);
        bool gridReliable = dayColBounds.Count >= 4;
        {
            string[] dn = { "Sun","Mon","Tue","Wed","Thu","Fri","Sat" };
            string found = string.Join(",", dayColBounds.Keys.OrderBy(k => k).Select(k => dn[k]));
            Console.Error.WriteLine(
                $"    [{T()}] ocr-cols: {dayColBounds.Count} day columns located ({found}), reliable={gridReliable}");
        }

        // ── OCR-based date override ────────────────────────────────────────────
        // WinRT OCR reliably reads M/D tokens (e.g. "9/21") from the header row.
        // If at least 4 are found, override the LLM-extracted dates and month —
        // the LLM can hallucinate the month (e.g. "January" for a September image).
        // Only override LLM header when OCR detects a DIFFERENT month — this is the
        // tell-tale sign of a hallucination (e.g. LLM says "January" for a September image).
        // When LLM and OCR agree on the month, trust the LLM dates (which are typically
        // cleaner than garbled OCR tokens).
        if (TryExtractOcrDates(ocrElements, dayColBounds, out var ocrIsoDates, out int ocrMonth, out int ocrYear)
            && ocrYear > 0
            && ocrMonth >= 1 && ocrMonth <= 12
            && ocrIsoDates.Count(d => !string.IsNullOrEmpty(d)) >= 1)
        {
            // Compare OCR month number against LLM month name
            int llmMonthNum = Array.IndexOf(MonthNames, month);  // 0 if not found, 1-12 otherwise
            bool monthMismatch = llmMonthNum < 1 || ocrMonth != llmMonthNum;
            if (monthMismatch)
            {
                string ocrMonthName = MonthNames[ocrMonth];
                Console.Error.WriteLine(
                    $"    [{T()}] ocr-dates: override LLM ({month} {year}) → OCR ({ocrMonthName} {ocrYear}), " +
                    $"{ocrIsoDates.Count(d => !string.IsNullOrEmpty(d))}/7 dates");
                month = ocrMonthName;
                year  = ocrYear;
                for (int i = 0; i < 7 && i < dates.Count; i++)
                    if (!string.IsNullOrEmpty(ocrIsoDates[i]))
                        dates[i] = ocrIsoDates[i];
            }
        }

        // ── OCR pre-fill: time-range strings found directly in each day column ─
        // ocrTimeMap[dayIdx][empIdx] = confirmed time-range string (or null)
        string?[][] ocrTimeMap = new string?[7][];
        for (int d = 0; d < 7; d++)
            ocrTimeMap[d] = new string?[names.Count];

        if (gridReliable)
        {
            foreach (var (dayIdx, (colXStart, colXEnd)) in dayColBounds)
            {
                if (dayIdx >= 7) continue;
                var colOcr = ocrElements
                    .Where(e => e.Bounds.CenterX >= colXStart && e.Bounds.CenterX <= colXEnd
                             && !IsHeaderLike(e.Text))
                    .OrderBy(e => e.Bounds.Y)
                    .ToList();

                for (int empIdx = 0; empIdx < names.Count && empIdx < colOcr.Count; empIdx++)
                {
                    string text = NormalizeShift(colOcr[empIdx].Text);
                    if (TimeRangeRegex.IsMatch(text))
                        ocrTimeMap[dayIdx][empIdx] = text;
                }
            }
        }

        // ── Initialize shift map with empty values ────────────────────────────
        static string[] DayNames() => new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var dayNamesArr = DayNames();

        var shiftMap = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var shifts = new List<object>();
            for (int i = 0; i < 7; i++)
            {
                string date = i < dates.Count
                    ? OllamaCalendarService.NormalizeIsoDate(dates[i])
                    : "";
                shifts.Add(new { Date = date, Shift = "" });
            }
            shiftMap[name] = shifts;
        }

        // ── Apply OCR pre-fills ───────────────────────────────────────────────
        int ocrFilled = 0;
        for (int dayIdx = 0; dayIdx < 7; dayIdx++)
        {
            for (int empIdx = 0; empIdx < names.Count; empIdx++)
            {
                string? val = ocrTimeMap[dayIdx][empIdx];
                if (val is null) continue;
                if (!shiftMap.TryGetValue(names[empIdx], out var shifts)) continue;
                if (dayIdx >= shifts.Count) continue;
                dynamic cell = shifts[dayIdx];
                shifts[dayIdx] = new { Date = (string)cell.Date, Shift = val };
                ocrFilled++;
            }
        }
        if (ocrFilled > 0)
            Console.Error.WriteLine($"    [{T()}] ocr pre-fill: {ocrFilled} time-range cells filled directly");

        // \u2500\u2500 Name column right-edge: left boundary of first day column \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        int nameXEnd = dayColBounds.Count > 0
            ? dayColBounds[0].XStart
            : Math.Min(250, imageWidth);

        // ── Pass 3: column-strip LLM query for each day column ────────────────
        for (int dayIdx = 0; dayIdx < 7; dayIdx++)
        {
            // Check how many employees still need LLM for this column
            var needsLlm = Enumerable.Range(0, names.Count)
                .Where(empIdx =>
                {
                    string? pre = dayIdx < ocrTimeMap.Length ? ocrTimeMap[dayIdx][empIdx] : null;
                    if (pre is not null) return false; // already filled by OCR
                    if (!shiftMap.TryGetValue(names[empIdx], out var s)) return true;
                    if (dayIdx >= s.Count) return true;
                    string existing = ((dynamic)s[dayIdx]).Shift ?? "";
                    return string.IsNullOrEmpty(existing);
                })
                .ToList();

            if (needsLlm.Count == 0)
            {
                Console.Error.WriteLine(
                    $"    [{T()}] pass 3/4: day {dayIdx} ({dayNamesArr[dayIdx]}) — all filled by OCR");
                continue;
            }

            // Build the query image: column strip if grid is reliable, else full image
            byte[] queryImage;
            bool   usingStrip = gridReliable && dayColBounds.ContainsKey(dayIdx);

            if (usingStrip)
            {
                // Stitch ONLY the name column + the single target day column so
                // the LLM cannot be confused by adjacent day columns.
                var (dayXStart, dayXEnd) = dayColBounds[dayIdx];
                int nameW  = nameXEnd;
                int dayW   = dayXEnd - dayXStart;
                queryImage = CropAndStitch(rawBytes, 0, nameW, dayXStart, dayW, imageHeight);
            }
            else
            {
                queryImage = rawBytes;
            }

            string isoDate  = dayIdx < dates.Count ? dates[dayIdx] : "";
            string colBase64 = Convert.ToBase64String(queryImage);

            // We always query for ALL employees in this column (not just needsLlm),
            // then merge only the blank slots.  This keeps the array size stable.
            List<string> colResults = await ExtractColumnFromImageAsync(
                colBase64,
                dayNamesArr[dayIdx],
                isoDate,
                names,
                usingStrip,
                ct);

            // Column-wide holiday/closure detection: if ≥ 80% of employees share the same
            // RTO/PTO value (all non-blank entries identical), it's a column annotation like
            // "Thanksgiving: RTO" — the answer records these as "" (closed). Blank them all.
            {
                var nonBlank = colResults.Where(v => !string.IsNullOrEmpty(v)).ToList();
                if (nonBlank.Count >= (int)Math.Ceiling(names.Count * 0.8) && nonBlank.Count > 0)
                {
                    string modeVal = nonBlank[0];
                    bool allSame   = nonBlank.All(v =>
                        v.Equals(modeVal, StringComparison.OrdinalIgnoreCase));
                    if (allSame && (modeVal.Equals("RTO", StringComparison.OrdinalIgnoreCase)
                                 || modeVal.Equals("PTO", StringComparison.OrdinalIgnoreCase)))
                    {
                        for (int i = 0; i < colResults.Count; i++) colResults[i] = "";
                    }
                }
            }

            // Merge: fill blanks from LLM, never overwrite OCR-confirmed values
            for (int empIdx = 0; empIdx < names.Count && empIdx < colResults.Count; empIdx++)
            {
                if (!needsLlm.Contains(empIdx)) continue; // already filled
                string name = names[empIdx];
                string val  = colResults[empIdx];
                if (string.IsNullOrEmpty(val)) continue;
                if (!shiftMap.TryGetValue(name, out var shifts)) continue;
                if (dayIdx >= shifts.Count) continue;
                dynamic cell    = shifts[dayIdx];
                string existing = (string)cell.Shift;
                if (string.IsNullOrEmpty(existing))
                    shifts[dayIdx] = new { Date = (string)cell.Date, Shift = val };
            }

            Console.Error.WriteLine(
                $"    [{T()}] pass 3/4: day {dayIdx} ({dayNamesArr[dayIdx]}) " +
                $"— {(usingStrip ? "strip" : "full-img")} LLM");
        }

        // ── OCR garbage sanitization ──────────────────────────────────────────
        foreach (string name in names)
        {
            if (!shiftMap.TryGetValue(name, out var shifts)) continue;
            for (int idx = 0; idx < shifts.Count; idx++)
            {
                dynamic cell = shifts[idx];
                string  val  = cell.Shift ?? "";
                if (OllamaCalendarService.OcrGarbagePattern.IsMatch(val))
                    shifts[idx] = new { Date = (string)cell.Date, Shift = "" };
            }
        }

        // ── Pass 4: X-marks clarification (LLM on full image) ────────────────
        var xMarks = await _ollama.ExtractXMarksAsync(base64, names, ct);
        Console.Error.WriteLine($"    [{T()}] pass 4/4: x-marks done");

        foreach (string name in names)
        {
            if (!shiftMap.TryGetValue(name, out var shifts)) continue;
            if (!xMarks.TryGetValue(name, out var xDays) || xDays.Count == 0) continue;

            // Guard: if >= 4 blank cells, the main extraction likely failed for this
            // employee — applying blank→x would corrupt unread time-ranges.
            int blankCount = shifts.Count(s => string.IsNullOrEmpty(((dynamic)s).Shift));
            if (blankCount >= 4) continue;

            for (int i = 0; i < shifts.Count; i++)
            {
                dynamic cell  = shifts[i];
                string  shift = cell.Shift ?? "";
                string  date  = cell.Date  ?? "";
                if (string.IsNullOrEmpty(shift) && i < 7 && xDays.Contains(dayNamesArr[i]))
                    shift = "x";
                shifts[i] = new { Date = date, Shift = shift };
            }
            shiftMap[name] = shifts;
        }

        // ── Assemble JSON ─────────────────────────────────────────────────────
        var employees = new List<object>();
        foreach (string name in names)
            employees.Add(new { Name = name, Shifts = shiftMap.GetValueOrDefault(name, new()) });

        var result = new { Month = month, Year = year, Employees = employees };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── OCR column boundary detection ─────────────────────────────────────────

    /// <summary>
    /// Scans WinRT OCR elements for day-name tokens (Sun–Sat) and uses
    /// their x-positions to compute per-column pixel boundaries.
    /// Returns a dictionary keyed by day index (0=Sun–6=Sat) so that detected
    /// columns map to the correct slot even when some headers are absent from the OCR output.
    /// Returns an empty dictionary if fewer than 4 day headers are detected.
    /// </summary>
    private static Dictionary<int, (int XStart, int XEnd)> ComputeDayColBoundsFromOcr(
        List<OcrElement> ocrElements, int imageWidth)
    {
        // Find each OCR element whose full text matches a day name and record the
        // center-x of that element. Use the topmost (header-row) occurrence of each day.
        // Also do prefix matching so tokens like "Tue28" or "Thu/4" still resolve.
        var centers = new Dictionary<int, int>();   // dayIndex → center-x
        var topmost = new Dictionary<int, int>();   // dayIndex → min-Y seen  (prefer header row)
        foreach (var elem in ocrElements)
        {
            string token = elem.Text.Trim();

            // Exact lookup first (fastest and most specific)
            if (!DayIndexMap.TryGetValue(token, out int dayIdx))
            {
                // Try 3-char prefix (handles "Tue/28", "Thurs", etc.)
                dayIdx = -1;
                if (token.Length >= 3)
                {
                    foreach (var (prefix, idx) in DayPrefixes)
                    {
                        if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        { dayIdx = idx; break; }
                    }
                }
                if (dayIdx < 0) continue;
            }

            int y = elem.Bounds.Y;
            // Only overwrite if this occurrence is higher on the image (smaller Y),
            // so we keep the header-row hit rather than a data-row occurrence.
            if (!topmost.ContainsKey(dayIdx) || y < topmost[dayIdx])
            {
                topmost[dayIdx]  = y;
                centers[dayIdx]  = elem.Bounds.CenterX;
            }
        }

        if (centers.Count < 4) return new();

        // Sort found days by x-position and compute boundaries as midpoints.
        var sorted = centers.OrderBy(kv => kv.Value).ToList();
        var result = new Dictionary<int, (int XStart, int XEnd)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            int dayIdx = sorted[i].Key;
            int center = sorted[i].Value;
            int spanL  = i > 0                  ? (center - sorted[i - 1].Value) / 2 : 80;
            int spanR  = i < sorted.Count - 1   ? (sorted[i + 1].Value - center) / 2 : 80;
            int xStart = i == 0                  ? Math.Max(0, center - spanL)
                                                 : (sorted[i - 1].Value + center) / 2;
            int xEnd   = i == sorted.Count - 1  ? Math.Min(imageWidth, center + spanR)
                                                 : (center + sorted[i + 1].Value) / 2;
            result[dayIdx] = (xStart, xEnd);
        }

        return result;
    }

    /// <summary>
    /// Crops two strips from an image and stitches them side-by-side (horizontally).
    /// Used to produce name-column + single-day-column crops without including
    /// the intervening columns that would confuse the LLM.
    /// </summary>
    private static byte[] CropAndStitch(
        byte[] imageBytes,
        int xA, int wA,   // left strip  (employee names)
        int xB, int wB,   // right strip (target day)
        int height)
    {
        using var src = new Mat();
        CvInvoke.Imdecode(imageBytes, ImreadModes.ColorBgr, src);

        int safeH = Math.Min(height, src.Height);

        int safeXA = Math.Clamp(xA, 0, src.Width - 1);
        int safeWA = Math.Clamp(wA, 1, src.Width - safeXA);
        int safeXB = Math.Clamp(xB, 0, src.Width - 1);
        int safeWB = Math.Clamp(wB, 1, src.Width - safeXB);

        using var roiA = new Mat(src, new System.Drawing.Rectangle(safeXA, 0, safeWA, safeH));
        using var roiB = new Mat(src, new System.Drawing.Rectangle(safeXB, 0, safeWB, safeH));
        using var stitched = new Mat();
        CvInvoke.HConcat(roiA, roiB, stitched);

        var buf = new VectorOfByte();
        CvInvoke.Imencode(".jpg", stitched, buf,
            new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 92));
        return buf.ToArray();
    }

    // ── Column LLM query ──────────────────────────────────────────────────────

    /// <summary>
    /// Queries the Ollama LLM on <paramref name="base64Image"/> and returns an
    /// ordered list of shift values (one per employee, top→bottom).
    /// When <paramref name="stripMode"/> is true the image contains ONLY the
    /// name column and one day column, so the "focus on column X" instruction
    /// is replaced by "read the right portion".
    /// </summary>
    private async Task<List<string>> ExtractColumnFromImageAsync(
        string base64Image,
        string dayName,
        string isoDate,
        List<string> names,
        bool   stripMode,
        CancellationToken ct)
    {
        string nd = OllamaCalendarService.NormalizeIsoDate(isoDate);
        string dateLabel = nd.Length >= 8
            ? $"{dayName} ({nd[5..].Replace("-", "/")})"
            : dayName;

        string nameList      = string.Join(", ", names.Select(n => $"\"{n}\""));
        string lastEmployee  = names.Count > 0 ? names[^1] : "the last employee";
        int    predictBudget = names.Count * 25 + 200;

        string prompt;
        if (stripMode)
        {
            prompt =
                "You are looking at a narrow two-column strip from a weekly work schedule image.\n" +
                "The LEFT column shows employee names. The RIGHT column shows their shifts " +
                $"for {dateLabel}.\n" +
                "The top row of the RIGHT column is a HEADER showing the day name and date — SKIP IT.\n" +
                "Do NOT output any date (like '2025-10-30' or '11/27') as a shift value — dates are NEVER shifts.\n" +
                $"Below the header there are exactly {names.Count} employee rows in this order: {nameList}.\n" +
                $"The table ends at the row labeled \"{lastEmployee}\".\n" +
                "Read ONLY the employee rows (not the header) in the RIGHT column from top to bottom.\n" +
                "Each cell contains: a time range (e.g. 9:00-5:30), RTO, PTO, x (day off), or is blank.\n" +
                "X marks may be in RED ink or any color — treat ANY X or checkmark as \"x\".\n" +
                "Ignore any hours number shown in a cell — only report the shift label.\n" +
                $"Reply with ONLY a JSON array of exactly {names.Count} strings, top to bottom:\n" +
                "[\"value1\", \"value2\", ...]\n" +
                "Use \"\" for blank cells. No explanation, no markdown.";
        }
        else
        {
            // Full-image fallback: same as OllamaCalendarService.ExtractColumnAsync
            prompt =
                $"Look at this work schedule image. Focus ONLY on the {dateLabel} column.\n" +
                $"There are exactly {names.Count} employee rows in this order: {nameList}.\n" +
                $"The table ends at the row labeled \"{lastEmployee}\".\n" +
                "Read each cell in that column from top to bottom, one value per employee row.\n" +
                "Each cell contains: a time range (e.g. 9:00-5:30), RTO, PTO, x (day off), or is blank.\n" +
                "X marks may be printed in RED ink or any other color — treat ANY X or checkmark as \"x\".\n" +
                "Ignore any hours number shown in a cell — only report the shift label.\n" +
                $"Reply with ONLY a JSON array of exactly {names.Count} strings, top to bottom:\n" +
                "[\"value1\", \"value2\", ...]\n" +
                "Use \"\" for blank cells. No explanation, no markdown.";
        }

        string raw = await _ollama.CallOllamaAsync(base64Image, prompt, ct, numPredict: predictBudget);

        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string v = el.GetString() ?? "";
                v = v.Trim();
                v = TrailingHours.Replace(v, "").Trim();
                if (Regex.IsMatch(v, @"^\d+\.?\d*$")) v = "";
                result.Add(v);
            }
        }
        catch { /* return partial or empty — merge will skip blanks */ }

        // If the LLM included the column header date as the first array element (e.g. "2025-10-30"),
        // remove it to realign employee rows — the padding below will fill the last slot with "".
        if (result.Count > 0 && Regex.IsMatch(result[0], @"^\d{4}-\d{2}-\d{2}$"))
            result.RemoveAt(0);

        while (result.Count < names.Count) result.Add("");
        return result.Take(names.Count).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Spatial assignment: for each OCR element, find the cell with the greatest
    /// bounding-box overlap and append the element's text to that cell.
    /// </summary>
    private static void MapOcrToCells(List<TableCell> cells, List<OcrElement> ocr)
    {
        foreach (var elem in ocr)
        {
            var best = cells
                .Select(c => (cell: c, overlap: OverlapArea(elem.Bounds, c.Bounds)))
                .Where(x => x.overlap > 0)
                .OrderByDescending(x => x.overlap)
                .FirstOrDefault();

            if (best.cell is null) continue;
            best.cell.Text = string.IsNullOrWhiteSpace(best.cell.Text)
                ? elem.Text
                : best.cell.Text + " " + elem.Text;
        }
    }

    private static int OverlapArea(Rect a, Rect b)
    {
        int ox = Math.Max(0, Math.Min(a.X + a.Width,  b.X + b.Width)  - Math.Max(a.X, b.X));
        int oy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return ox * oy;
    }

    /// <summary>
    /// Crop a rectangular region from a JPEG/PNG byte array using OpenCV.
    /// Clamps the rectangle to the image bounds automatically.
    /// Returns the cropped region as a JPEG byte array.
    /// </summary>
    private static byte[] CropRegion(byte[] imageBytes, int x, int y, int width, int height)
    {
        using var src = new Mat();
        CvInvoke.Imdecode(imageBytes, ImreadModes.ColorBgr, src);

        int safeX = Math.Max(0, Math.Min(x, src.Width  - 1));
        int safeY = Math.Max(0, Math.Min(y, src.Height - 1));
        int safeW = Math.Max(1, Math.Min(width,  src.Width  - safeX));
        int safeH = Math.Max(1, Math.Min(height, src.Height - safeY));

        var rect = new System.Drawing.Rectangle(safeX, safeY, safeW, safeH);
        using var roi = new Mat(src, rect);
        var buf = new VectorOfByte();
        CvInvoke.Imencode(".jpg", roi, buf,
            new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 92));
        return buf.ToArray();
    }

    /// <summary>
    /// Extracts date strings from OCR header tokens adjacent to the detected day columns.
    /// Uses permissive digit-group parsing to handle garbled OCR (e.g. "09,'21/2025").
    /// Returns true if at least 1 date is found; missing columns are filled by
    /// extrapolation from adjacent known dates.
    /// Populates <paramref name="isoDates"/> (YYYY-MM-DD per day index, empty for
    /// columns that couldn't be filled), <paramref name="month"/> (1–12),
    /// and <paramref name="year"/>.
    /// </summary>
    private static bool TryExtractOcrDates(
        List<OcrElement> ocrElements,
        Dictionary<int, (int XStart, int XEnd)> dayColBounds,
        out string[] isoDates,
        out int month,
        out int year)
    {
        isoDates = new string[7];
        month = 0;
        year  = 0;

        if (dayColBounds.Count < 4) return false;

        // Header row Y = minimum Y of any day-name OCR token
        int headerY = int.MaxValue;
        foreach (var elem in ocrElements)
        {
            string token = elem.Text.Trim();
            bool isDayName = DayIndexMap.ContainsKey(token);
            if (!isDayName && token.Length >= 3)
                isDayName = DayPrefixes.Any(p =>
                    token.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase));
            if (isDayName && elem.Bounds.Y < headerY)
                headerY = elem.Bounds.Y;
        }
        if (headerY == int.MaxValue) return false;

        // Search zone: from just above the topmost day-name to 100 px below it
        int zoneTop    = Math.Max(0, headerY - 20);
        int zoneBottom = headerY + 100;

        // Digit-group extractor: pulls all numeric sequences from a token.
        // More permissive than strict M/D/YYYY regex — handles garbled OCR like
        // "09,'21/2025" (comma separator), "09/25/20?0" (? character), "09/27/2" (truncated).
        // Requires the token to contain "/" or "," (date separator) to avoid false positives.
        static bool TryParseOcrDateToken(string text, out int mo, out int day, out int yr)
        {
            mo = day = yr = 0;
            if (!text.Contains('/') && !text.Contains(',')) return false;

            // Extract all digit runs
            var groups = Regex.Matches(text, @"\d+").Select(m => m.Value).ToList();
            if (groups.Count < 2) return false;

            if (!int.TryParse(groups[0], out mo) || mo < 1 || mo > 12) return false;

            // Day: take up to 2 digits from the second group (handles "261202" → 26)
            string dayStr = groups[1].Length <= 2 ? groups[1] : groups[1][..2];
            if (!int.TryParse(dayStr, out day) || day < 1 || day > 31) return false;

            // Year: find first group that's exactly 4 digits in [2020, 2035]
            yr = 0;
            foreach (var g in groups.Skip(2))
            {
                if (g.Length == 4 && int.TryParse(g, out int y4) && y4 >= 2020 && y4 <= 2035)
                { yr = y4; break; }
            }
            return true;
        }

        var parsedDates = new Dictionary<int, (int Mo, int Day, int Yr)>();

        foreach (var (dayIdx, (xStart, xEnd)) in dayColBounds)
        {
            var candidates = ocrElements
                .Where(e => e.Bounds.Y >= zoneTop && e.Bounds.Y <= zoneBottom
                         && e.Bounds.CenterX >= xStart && e.Bounds.CenterX <= xEnd)
                .OrderBy(e => e.Bounds.Y)
                .ToList();

            foreach (var cand in candidates)
            {
                if (TryParseOcrDateToken(cand.Text.Trim(), out int mo, out int day, out int yr))
                {
                    parsedDates[dayIdx] = (mo, day, yr);
                    break;
                }
            }
        }

        if (parsedDates.Count < 1) return false;

        // Consensus month: most common valid month across all parsed dates.
        // Then discard any entries whose month differs from the consensus —
        // a garbled token like "2,'202S" may parse as mo=2 when the true month
        // is 9; the consensus (4:1) still picks 9 correctly.
        int consensusMonth = parsedDates.Values.GroupBy(d => d.Mo).OrderByDescending(g => g.Count()).First().Key;
        month = consensusMonth;
        foreach (var key in parsedDates.Keys.Where(k => parsedDates[k].Mo != consensusMonth).ToList())
            parsedDates.Remove(key);

        if (parsedDates.Count < 1) return false;

        // Consensus year: prefer valid 4-digit years (yr > 0) over yr=0 (not found in token)
        var validYears = parsedDates.Values.Where(d => d.Yr > 0).ToList();
        year = validYears.Count > 0
            ? validYears.GroupBy(d => d.Yr).OrderByDescending(g => g.Count()).First().Key
            : 0;

        foreach (var (dayIdx, (mo, day, yr)) in parsedDates)
        {
            int effectiveYr = yr > 0 ? yr : year;
            if (effectiveYr > 0)
                isoDates[dayIdx] = $"{effectiveYr:D4}-{mo:D2}-{day:D2}";
        }

        // Fill missing columns by extrapolation from nearest known date
        for (int i = 0; i < 7; i++)
        {
            if (!string.IsNullOrEmpty(isoDates[i])) continue;
            for (int offset = 1; offset <= 6; offset++)
            {
                bool filled = false;
                foreach (int sign in new[] { -1, 1 })
                {
                    int neighbor = i + sign * offset;
                    if (neighbor < 0 || neighbor >= 7) continue;
                    if (string.IsNullOrEmpty(isoDates[neighbor])) continue;
                    if (DateTime.TryParse(isoDates[neighbor], out var neighborDate))
                    {
                        isoDates[i] = neighborDate.AddDays(i - neighbor).ToString("yyyy-MM-dd");
                        filled = true;
                        break;
                    }
                }
                if (filled) break;
            }
        }

        return true;
    }

    /// <summary>
    /// Heuristic: does the cell text look like a header cell rather than a name/shift?
    /// Header cells typically contain "/" (date like 10/27) or day abbreviations.
    /// </summary>
    private static bool IsHeaderLike(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains('/') ||
               Regex.IsMatch(text, @"\b(sun|mon|tue|wed|thu|fri|sat)\b",
                   RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Normalizes a raw OCR cell string: strips trailing hours numbers
    /// (e.g. "9:00-5:30 7.5" → "9:00-5:30") and trims whitespace.
    /// </summary>
    private static string NormalizeShift(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string v = TrailingHours.Replace(text.Trim(), "").Trim();
        if (Regex.IsMatch(v, @"^\d+\.?\d*$")) return "";
        return v;
    }
}
