using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalendarParse.Services;

namespace CalendarParse.Cli.Services;

/// <summary>
/// ICalendarParseService implementation that sends the image to a local Ollama
/// vision model (default: llama3.2-vision:11b) and returns structured JSON.
/// Bypasses the Tesseract/OpenCV pipeline entirely.
/// </summary>
public class OllamaCalendarService : ICalendarParseService
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel   = "llama3.2-vision:11b";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

    internal static readonly Regex OcrGarbagePattern =
        new(@"^\d+\.?\d*\s+\w", RegexOptions.Compiled);

    private readonly string _baseUrl;
    private readonly string _model;
    internal readonly IReadOnlyList<string> _knownNames;

    public OllamaCalendarService(string baseUrl = DefaultBaseUrl, string model = DefaultModel, IEnumerable<string>? knownNames = null)
    {
        _baseUrl    = baseUrl;
        _model      = model;
        _knownNames = knownNames?.ToArray() ?? Array.Empty<string>();
    }

    public async Task<string> ProcessAsync(Stream imageStream, string nameFilter, CancellationToken ct = default)
    {
        await EnsureModelLoadedAsync(ct);
        using var ms = new MemoryStream();
        await imageStream.CopyToAsync(ms, ct);
        string base64Image = Convert.ToBase64String(ms.ToArray());

        var processSw = System.Diagnostics.Stopwatch.StartNew();
        string T() => $"+{processSw.Elapsed.TotalSeconds:F0}s";

        // ── Pass 1: extract the 7 dates from the header row ───────────────────
        var (month, year, dates) = await ExtractHeaderAsync(base64Image, ct);
        Console.Error.WriteLine($"    [{T()}] pass 1/4: header ({month} {year}, {dates.Count} dates)");

        // ── Pass 2: extract employee names from the main table ────────────────
        var names = await ExtractNamesAsync(base64Image, ct);

        // P9: Normalize extracted names against known reference list (strip phantom suffixes,
        // deduplicate OCR variants like "Clara" → "Ciara", "Athena(train)" → "Athena").
        if (_knownNames.Count > 0)
            names = NormalizeNamesAgainstKnown(names);

        // Apply name filter
        if (!string.IsNullOrWhiteSpace(nameFilter))
            names = names.Where(n => n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.Error.WriteLine($"    [{T()}] pass 2/4: names ({names.Count} employees)");

        // ── Pass 3: extract all shifts — 3 row runs + 2 column runs, majority vote ──
        // P13a isolation: reverted P12 (back to 3 row runs), P13 (ISO date keys) active.
        // P13 uses the actual M/D header date strings as JSON keys so the model anchors
        // each value to a physical label it can see in the image.
        var shiftRuns = new Dictionary<string, List<object>>[5];
        for (int run = 0; run < 3; run++)
        {
            shiftRuns[run] = await ExtractAllShiftsAsync(base64Image, names, dates, ct);
            Console.Error.WriteLine($"    [{T()}] pass 3/4: row run {run + 1}/3");
        }
        shiftRuns[3] = await ExtractAllShiftsByColumnAsync(base64Image, names, dates, ct);
        Console.Error.WriteLine($"    [{T()}] pass 3/4: col run 1/2");
        shiftRuns[4] = await ExtractAllShiftsByColumnAsync(base64Image, names, dates, ct);
        Console.Error.WriteLine($"    [{T()}] pass 3/4: col run 2/2");
        var shiftMap = MajorityVoteShiftMaps(shiftRuns, names);

        // P19: Cross-employee systematic shift detection.
        // If 5+ employees simultaneously show blank@col[N] + non-blank@col[N+1], treat this as
        // a systematic right-column drift (the model read column N but wrote into column N+1 for
        // most employees). Majority vote cannot correct this because all 5 runs agreed on the
        // wrong answer. Apply a provisional left-shift correction, then re-query column N+1.
        {
            var dayNamesP19 = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            for (int i = 0; i < 6; i++)
            {
                int blankToNonBlankCount = 0;
                int nonBlankAtN = 0;
                var affectedEmployees = new List<string>();

                foreach (string name in names)
                {
                    if (!shiftMap.TryGetValue(name, out var shifts) || shifts.Count < 7) continue;
                    string valN  = ((dynamic)shifts[i]).Shift ?? "";
                    string valN1 = ((dynamic)shifts[i + 1]).Shift ?? "";
                    if (!string.IsNullOrEmpty(valN)) nonBlankAtN++;
                    if (string.IsNullOrEmpty(valN) && !string.IsNullOrEmpty(valN1))
                    {
                        blankToNonBlankCount++;
                        affectedEmployees.Add(name);
                    }
                }

                // Threshold: 5+ employees in the pattern AND fewer than 4 employees have
                // non-blank values at col N (confirming col N is nearly empty after vote)
                bool isSystematicShift = blankToNonBlankCount >= 5 && nonBlankAtN <= 3;
                if (!isSystematicShift) continue;

                Console.Error.WriteLine(
                    $"    [{T()}] pass 3/4: systematic right-shift detected at col {i}→{i+1} " +
                    $"({blankToNonBlankCount}/{names.Count} employees affected)");

                // Provisional left-shift correction: move N+1 value → N, clear N+1
                foreach (string name in affectedEmployees)
                {
                    var shifts = shiftMap[name];
                    dynamic cellN  = shifts[i];
                    dynamic cellN1 = shifts[i + 1];
                    shifts[i]     = new { Date = (string)cellN.Date,  Shift = (string)cellN1.Shift };
                    shifts[i + 1] = new { Date = (string)cellN1.Date, Shift = "" };
                }
                Console.Error.WriteLine(
                    $"    [{T()}] pass 3/4: systematic right-shift at col {i}→{i+1}, " +
                    $"correcting {affectedEmployees.Count} employees (provisional left-shift applied)");

                // Re-query column N+1 with date-anchored prompt to recover true values
                if (i + 1 >= dates.Count) continue;
                string reQueryDate = dates[i + 1];
                string reQueryDay  = dayNamesP19[i + 1];
                var colResult = await ExtractColumnAnchoredAsync(
                    base64Image, reQueryDay, reQueryDate, affectedEmployees, ct);
                Console.Error.WriteLine(
                    $"    [{T()}] pass 3/4: re-queried col {i+1} ({reQueryDay}) " +
                    $"for {affectedEmployees.Count} affected employee(s)");

                // Merge: for affected employees, apply re-query result only if it upgrades a blank
                for (int empIdx = 0; empIdx < affectedEmployees.Count; empIdx++)
                {
                    string name     = affectedEmployees[empIdx];
                    string newShift = empIdx < colResult.Count ? colResult[empIdx] : "";
                    if (!shiftMap.TryGetValue(name, out var shifts) || shifts.Count < 7) continue;

                    dynamic existingCell = shifts[i + 1];
                    string  existingVal  = existingCell.Shift ?? "";
                    bool existingIsBlank = string.IsNullOrEmpty(existingVal);
                    bool newIsNonBlank   = !string.IsNullOrEmpty(newShift);
                    if (existingIsBlank && newIsNonBlank)
                        shifts[i + 1] = new { Date = (string)existingCell.Date, Shift = newShift };
                }
            }
        }

        // P14: Drift detection — re-query employees showing +1 column-shift signature.
        // Signature: employee has blank at day[i] AND a non-blank at day[i+1] where
        // the non-blank at i+1 is also the expected value at i (we can't know expected,
        // but we can detect the structural pattern: blank-then-value repeated across
        // multiple consecutive day pairs, which is the hallmark of a right-shift).
        var driftSuspectNames = names.Where(n =>
        {
            if (!shiftMap.TryGetValue(n, out var shifts) || shifts.Count < 7) return false;
            // Count consecutive (blank, non-blank) pairs — indicates right-shift drift
            int driftPairs = 0;
            for (int i = 0; i < shifts.Count - 1; i++)
            {
                string cur  = ((dynamic)shifts[i]).Shift ?? "";
                string next = ((dynamic)shifts[i + 1]).Shift ?? "";
                if (string.IsNullOrEmpty(cur) && !string.IsNullOrEmpty(next) && next != "x")
                    driftPairs++;
            }
            return driftPairs >= 2; // 2+ consecutive blank→value pairs = likely drift
        }).ToList();

        if (driftSuspectNames.Count > 0)
        {
            Console.Error.WriteLine($"    [{T()}] pass 3/4: re-query {driftSuspectNames.Count} drift-suspect employee(s)");
            var driftRetry = await ExtractAllShiftsAsync(base64Image, driftSuspectNames, dates, ct);
            foreach (var kv in driftRetry)
            {
                if (shiftMap.TryGetValue(kv.Key, out var existing))
                {
                    int existingBlanks = existing.Count(s => string.IsNullOrEmpty(((dynamic)s).Shift));
                    int retryBlanks    = kv.Value.Count(s => string.IsNullOrEmpty(((dynamic)s).Shift));
                    // P18: Only apply if quality improves: fewer blanks OR (same blanks AND more valid-format values)
                    bool fewerBlanks = retryBlanks < existingBlanks;
                    bool sameBlanksBetterQuality = retryBlanks == existingBlanks && CountValidShifts(kv.Value) >= CountValidShifts(existing);
                    if (fewerBlanks || sameBlanksBetterQuality)
                        shiftMap[kv.Key] = kv.Value;
                }
                else
                {
                    shiftMap[kv.Key] = kv.Value;
                }
            }
            Console.Error.WriteLine($"    [{T()}] pass 3/4: drift re-query done");
        }

        // P14: heavy-blank re-query (existing)
        // Smart re-query: employees with >= 4 blank cells likely suffered a column-shift failure.
        // Re-query using the named-key approach focused on just those employees (much more reliable
        // than the old CSV ExtractRowAsync which also tends to return blank for lower-table rows).
        var heavyBlankNames = names
            .Where(n => shiftMap.ContainsKey(n) &&
                        shiftMap[n].Count(s => string.IsNullOrEmpty(((dynamic)s).Shift)) >= 4)
            .ToList();
        if (heavyBlankNames.Count > 0)
        {
            Console.Error.WriteLine($"    [{T()}] pass 3/4: retry {heavyBlankNames.Count} employee(s) with blank cells");
            var retry = await ExtractAllShiftsAsync(base64Image, heavyBlankNames, dates, ct);
            // P16: Only overwrite if the retry produced fewer (or equal) blanks — never make things worse
            foreach (var kv in retry)
            {
                if (shiftMap.TryGetValue(kv.Key, out var existing))
                {
                    int existingBlanks = existing.Count(s => string.IsNullOrEmpty(((dynamic)s).Shift));
                    int retryBlanks    = kv.Value.Count(s  => string.IsNullOrEmpty(((dynamic)s).Shift));
                    // P18: Only apply if quality improves: fewer blanks OR (same blanks AND more valid-format values)
                    bool fewerBlanks = retryBlanks < existingBlanks;
                    bool sameBlanksBetterQuality = retryBlanks == existingBlanks && CountValidShifts(kv.Value) >= CountValidShifts(existing);
                    if (fewerBlanks || sameBlanksBetterQuality)
                        shiftMap[kv.Key] = kv.Value;
                }
                else shiftMap[kv.Key] = kv.Value;
            }
            Console.Error.WriteLine($"    [{T()}] pass 3/4: retry done");
        }

        // P16: Per-employee row fallback for employees still stuck with >= 4 blank cells
        // after the batch re-query. ExtractRowAsync uses a simpler single-employee prompt
        // that tends to work better for lower-table rows.
        var persistentBlanks = names
            .Where(n => shiftMap.TryGetValue(n, out var s) &&
                        s.Count(c => string.IsNullOrEmpty(((dynamic)c).Shift)) >= 4)
            .ToList();
        if (persistentBlanks.Count > 0)
        {
            Console.Error.WriteLine($"    [{T()}] pass 3/4: per-employee fallback for {persistentBlanks.Count} employee(s)");
            foreach (string name in persistentBlanks)
            {
                var rowResult = await ExtractRowAsync(base64Image, name, dates, ct);
                int existingBlanks = shiftMap[name].Count(c => string.IsNullOrEmpty(((dynamic)c).Shift));
                int rowBlanks      = rowResult.Count(c  => string.IsNullOrEmpty(((dynamic)c).Shift));
                // P18: Only apply if quality improves: fewer blanks OR (same blanks AND more valid-format values)
                bool fewerBlanks = rowBlanks < existingBlanks;
                bool sameBlanksBetterQuality = rowBlanks == existingBlanks && CountValidShifts(rowResult) >= CountValidShifts(shiftMap[name]);
                if (fewerBlanks || sameBlanksBetterQuality)
                    shiftMap[name] = rowResult;
            }
            Console.Error.WriteLine($"    [{T()}] pass 3/4: per-employee fallback done");
        }

        // Fallback: for any employee still missing, try per-employee CSV
        var missingNames = names.Where(n => !shiftMap.ContainsKey(n)).ToList();
        foreach (string name in missingNames)
        {
            Console.Error.WriteLine($"    [{T()}] pass 3/4: fallback row query for {name}");
            shiftMap[name] = await ExtractRowAsync(base64Image, name, dates, ct);
        }

        // P19: OCR garbage sanitization — remove hallucinated values like "28.5 Seena" or "24 Jenny"
        // (a numeric prefix followed by a space and a word character, which can never be a valid shift).
        foreach (string name in names)
        {
            if (!shiftMap.TryGetValue(name, out var shifts)) continue;
            for (int idx = 0; idx < shifts.Count; idx++)
            {
                dynamic cell = shifts[idx];
                string  val  = cell.Shift ?? "";
                if (OcrGarbagePattern.IsMatch(val))
                {
                    shifts[idx] = new { Date = (string)cell.Date, Shift = "" };
                    Console.Error.WriteLine(
                        $"    [{T()}] pass 3/4: OCR garbage sanitized for {name} day {idx}: \"{val}\" → \"\"");
                }
            }
        }

        // ── Pass 4: X-marks clarification ─────────────────────────────────────
        // The main extraction confuses "x" (day off mark) with "" (blank cell).
        // A dedicated binary X-marks query is more reliable.  We only apply it
        // in one direction: blank→x (never overwrite a non-blank extracted value).
        var xMarks = await ExtractXMarksAsync(base64Image, names, ct);
        Console.Error.WriteLine($"    [{T()}] pass 4/4: x-marks done");
        foreach (string name in names)
        {
            if (!shiftMap.TryGetValue(name, out var shifts)) continue;
            if (!xMarks.TryGetValue(name, out var xDays) || xDays.Count == 0) continue;

            // Guard: if the main extraction left ≥4 cells blank, those are likely unread
            // time ranges, not genuine X marks — applying blank→x would corrupt them.
            int blankCount = shifts.Count(s => string.IsNullOrEmpty(((dynamic)s).Shift));
            if (blankCount >= 4) continue;

            var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var updated = new List<object>();
            for (int i = 0; i < shifts.Count; i++)
            {
                dynamic cell = shifts[i];
                string shift = cell.Shift ?? "";
                string date  = cell.Date  ?? "";
                // Only promote blank→x; never overwrite a real value
                if (string.IsNullOrEmpty(shift) && i < 7 && xDays.Contains(dayNames[i]))
                    shift = "x";
                updated.Add(new { Date = date, Shift = shift });
            }
            shiftMap[name] = updated;
        }

        var employees = new List<object>();
        foreach (string name in names)
        {
            employees.Add(new { Name = name, Shifts = shiftMap[name] });
        }

        var result = new { Month = month, Year = year, Employees = employees };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Header extraction ──────────────────────────────────────────────────────

    internal async Task<(string month, int year, List<string> isoDates)> ExtractHeaderAsync(
        string base64Image, CancellationToken ct)
    {
        const string prompt =
            "Look at this work schedule image.\n" +
            "Find the header row that contains the 7 dates of the week.\n" +
            "The week runs Sunday through Saturday (7 days).\n" +
            "Reply with ONLY a JSON object — no markdown, no extra text:\n" +
            "{\"Month\":\"<full month name of Sunday>\",\"Year\":<4-digit year>," +
            "\"Dates\":[\"YYYY-MM-DD\",\"YYYY-MM-DD\",\"YYYY-MM-DD\",\"YYYY-MM-DD\"," +
            "\"YYYY-MM-DD\",\"YYYY-MM-DD\",\"YYYY-MM-DD\"]}\n" +
            "Exactly 7 dates Sun→Sat. Convert M/D/YY to YYYY-MM-DD. Do NOT use today's date.";

        string raw = await CallOllamaAsync(base64Image, prompt, ct, numPredict: 300);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            string month = doc.RootElement.GetProperty("Month").GetString() ?? "Unknown";
            int year = doc.RootElement.GetProperty("Year").GetInt32();
            var dates = doc.RootElement.GetProperty("Dates")
                .EnumerateArray().Select(d => d.GetString() ?? "").ToList();
            while (dates.Count < 7) dates.Add("");
            return (month, year, dates.Take(7).ToList());
        }
        catch
        {
            return ("Unknown", 0, Enumerable.Repeat("", 7).ToList());
        }
    }

    // ── Names extraction ──────────────────────────────────────────────────────

    internal async Task<List<string>> ExtractNamesAsync(
        string base64Image, CancellationToken ct,
        IReadOnlyList<string>? additionalHints = null,
        IReadOnlyList<string>? ocrNameFragments = null)
    {
        string prompt =
            "Look at this work schedule image.\n" +
            "Note: this is a photograph of a printed grid — the grid lines may not be perfectly\n" +
            "straight or orthogonal due to camera angle and perspective distortion. Read each row\n" +
            "by visual context, not pixel-perfect alignment.\n" +
            "Find the MAIN employee scheduling table (the large table at the top).\n" +
            "List every employee name from the leftmost column of that table, one name per line.\n" +
            "Go top to bottom. Stop before any secondary or totals table.\n" +
            "Spell each name EXACTLY character by character as it appears in the image.\n" +
            "Output ONLY the names — no numbers, no extra text, no punctuation.";
        if (_knownNames.Count > 0)
            prompt += $"\nKnown employee names for this schedule (if you see one of these, use this exact spelling): {string.Join(", ", _knownNames)}. Do not invent names not on this list unless they are clearly visible.";
        if ((additionalHints?.Count ?? 0) > 0)
            // Session hints from prior images help with spelling but are not exhaustive — the
            // schedule may have employees not in this list, so still report everyone visible.
            prompt += $"\nSpelling reference from related schedules (use these exact spellings if you see them, but also list ANY other names clearly visible): {string.Join(", ", additionalHints!)}.";
        if ((ocrNameFragments?.Count ?? 0) > 0)
            // OCR partial reads from the name column of THIS image anchor the LLM to what
            // is actually present, including rows with unusual styling OCR couldn't fully decode.
            prompt += $"\nOCR detected these partial text fragments from the name column of this image " +
                      $"(they may be truncated or split): {string.Join(", ", ocrNameFragments!)}. " +
                      "Use these as anchors — every fragment likely corresponds to an employee row. " +
                      "Identify the full name for each fragment, and include ANY additional names " +
                      "visually present that OCR may have missed entirely.";

        string raw = await CallOllamaAsync(base64Image, prompt, ct, isJson: false, numPredict: 200);

        var names = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string n = line.Trim(' ', '\r', '-', '*', '\u2022');
            n = Regex.Replace(n, @"^\d+[.)\s]+", "").Trim(); // remove leading "1. "
            if (n.Length >= 2 && n.Length <= 40 && seen.Add(n))
                names.Add(n);
        }
        return names;
    }

    // ── X-marks binary extraction ─────────────────────────────────────────────

    internal async Task<Dictionary<string, HashSet<string>>> ExtractXMarksAsync(
        string base64Image, List<string> names, CancellationToken ct)
    {
        string nameList = string.Join(", ", names.Select(n => $"\"{n}\""));
        string prompt =
            "Look at this work schedule image.\n" +
            "Note: this is a photograph of a printed grid — lines may not be perfectly straight\n" +
            "or orthogonal due to camera angle. Read each cell by visual context.\n" +
            "For each employee listed below, identify ONLY the day columns that contain an X mark\n" +
            "or checkmark symbol — these indicate a day off. X marks may be red, black, or any color.\n" +
            $"Employees: {nameList}.\n" +
            "Reply with ONLY this JSON (no markdown, no explanation):\n" +
            "{\n" +
            "  \"<EmployeeName>\": [\"<DayName>\", ...],\n" +
            "  ...\n" +
            "}\n" +
            "Day names are: Sun, Mon, Tue, Wed, Thu, Fri, Sat.\n" +
            "Include ALL employees. Use [] if no days have an X mark.\n" +
            "Do NOT include days with time ranges, RTO, PTO, or blank cells — ONLY X marks.";

        string raw = await CallOllamaAsync(base64Image, prompt, ct, numPredict: 1500);

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var validDays = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var emp in doc.RootElement.EnumerateObject())
            {
                var days = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (emp.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var day in emp.Value.EnumerateArray())
                    {
                        string d = day.GetString() ?? "";
                        if (validDays.Contains(d)) days.Add(d);
                    }
                }
                result[emp.Name] = days;
            }
        }
        catch { /* return empty — overlay won't apply */ }
        return result;
    }

    // ── Majority-vote across multiple shift maps ───────────────────────────────

    private static Dictionary<string, List<object>> MajorityVoteShiftMaps(
        Dictionary<string, List<object>>[] maps,
        List<string> orderedNames)
    {
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in orderedNames)
        {
            // Collect all shift-lists for this employee across all maps
            var allShiftLists = maps
                .Where(m => m.TryGetValue(name, out _))
                .Select(m => m[name])
                .ToList();

            if (allShiftLists.Count == 0) continue;

            int maxCells = allShiftLists.Max(l => l.Count);
            var merged = new List<object>();

            for (int i = 0; i < Math.Max(7, maxCells); i++)
            {
                // Collect all values for this cell position
                var candidates = new List<string>();
                string dateVal = "";
                foreach (var shiftList in allShiftLists)
                {
                    if (i < shiftList.Count)
                    {
                        dynamic cell = shiftList[i];
                        candidates.Add(cell.Shift ?? "");
                        if (string.IsNullOrEmpty(dateVal)) dateVal = cell.Date ?? "";
                    }
                    else
                    {
                        candidates.Add("");
                    }
                }

                // Majority vote: find the most frequent non-empty value
                // If tied, prefer non-empty over empty
                var freq = candidates
                    .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key.Length) // non-empty wins ties
                    .ToList();

                string chosen = freq[0].Key;
                // If top vote is "" and there's a non-empty with same count (tie), use non-empty
                if (chosen == "" && freq.Count > 1 && freq[1].Count() == freq[0].Count())
                    chosen = freq[1].Key;

                merged.Add(new { Date = dateVal, Shift = chosen });
            }
            result[name] = merged;
        }
        return result;
    }

    // ── Vertical column extraction ────────────────────────────────────────────
    // Reads one day-column at a time (all employees, top→bottom) then zips the
    // ordered result with the employee name list to reconstruct the shift map.
    // Avoids row-lookup errors: model only needs to read down a single column.

    private async Task<Dictionary<string, List<object>>> ExtractAllShiftsByColumnAsync(
        string base64Image, List<string> names, List<string> dates, CancellationToken ct)
    {
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        // Read each of the 7 day columns sequentially
        var columns = new List<List<string>>();
        for (int i = 0; i < 7; i++)
        {
            string isoDate = i < dates.Count ? dates[i] : "";
            var col = await ExtractColumnAsync(base64Image, dayNames[i], isoDate, names, ct);
            columns.Add(col);
        }

        // Reconstruct shiftMap by position: column[day][empIdx] → shiftMap[name][day]
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        for (int empIdx = 0; empIdx < names.Count; empIdx++)
        {
            var shifts = new List<object>();
            for (int dayIdx = 0; dayIdx < 7; dayIdx++)
            {
                string date  = dayIdx < dates.Count ? NormalizeIsoDate(dates[dayIdx]) : "";
                string shift = empIdx < columns[dayIdx].Count ? columns[dayIdx][empIdx] : "";
                shifts.Add(new { Date = date, Shift = shift });
            }
            result[names[empIdx]] = shifts;
        }
        return result;
    }

    private async Task<List<string>> ExtractColumnAsync(
        string base64Image, string dayName, string isoDate, List<string> names, CancellationToken ct)
    {
        string nd_col = NormalizeIsoDate(isoDate);
        string dateLabel = !string.IsNullOrEmpty(isoDate) && nd_col.Length >= 5
            ? $"{dayName} ({nd_col[5..].Replace("-", "/")})"
            : dayName;
        string nameList = string.Join(", ", names.Select(n => $"\"  {n}\""));
        string lastEmployee = names.Count > 0 ? names[^1] : "the last employee";

        string prompt =
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

        int predictBudget = names.Count * 25 + 150;
        string raw = await CallOllamaAsync(base64Image, prompt, ct, numPredict: predictBudget);

        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string v = el.GetString() ?? "";
                v = v.Trim();
                v = Regex.Replace(v, @"\s+\d+(\.\d+)?$", "").Trim();
                if (Regex.IsMatch(v, @"^\d+\.?\d*$")) v = "";
                result.Add(v);
            }
        }
        catch { }

        // Pad or trim to exactly names.Count
        while (result.Count < names.Count) result.Add("");
        return result.Take(names.Count).ToList();
    }

    private async Task<List<string>> ExtractColumnAnchoredAsync(
        string base64Image, string dayName, string isoDate, List<string> names, CancellationToken ct)
    {
        string nd_col = NormalizeIsoDate(isoDate);
        string md = nd_col.Length >= 5 ? nd_col[5..].Replace("-", "/") : isoDate;
        string dateLabel = $"{dayName} ({md})";
        string nameList = string.Join(", ", names.Select(n => $"\"  {n}\""));
        string lastEmployee = names.Count > 0 ? names[^1] : "the last employee";

        string prompt =
            $"Look at this work schedule image. You must read the {dateLabel} column.\n" +
            $"IMPORTANT: Find the column whose header shows the date '{md}' printed at the top " +
            $"of the table. Do NOT guess the column by day-of-week position — locate the printed " +
            $"label '{md}' in the header row, then read the cells directly below it.\n" +
            $"There are exactly {names.Count} employee rows in this order: {nameList}.\n" +
            $"The table ends at the row labeled \"{lastEmployee}\".\n" +
            "Read each cell in that column from top to bottom, one value per employee row.\n" +
            "Each cell contains: a time range (e.g. 9:00-5:30), RTO, PTO, x (day off), or is blank.\n" +
            "X marks may be printed in RED ink or any other color — treat ANY X or checkmark as \"x\".\n" +
            "Ignore any hours number shown in a cell — only report the shift label.\n" +
            $"Reply with ONLY a JSON array of exactly {names.Count} strings, top to bottom:\n" +
            "[\"value1\", \"value2\", ...]\n" +
            "Use \"\" for blank cells. No explanation, no markdown.";

        int predictBudget = names.Count * 25 + 150;
        string raw = await CallOllamaAsync(base64Image, prompt, ct, numPredict: predictBudget);

        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string v = el.GetString() ?? "";
                v = v.Trim();
                v = Regex.Replace(v, @"\s+\d+(\.\d+)?$", "").Trim();
                if (Regex.IsMatch(v, @"^\d+\.?\d*$")) v = "";
                result.Add(v);
            }
        }
        catch { }

        while (result.Count < names.Count) result.Add("");
        return result.Take(names.Count).ToList();
    }

    // ── All-shifts single-shot extraction (used for heavy-blank re-query) ──────

    private async Task<Dictionary<string, List<object>>> ExtractAllShiftsAsync(
        string base64Image, List<string> names, List<string> dates, CancellationToken ct)
    {
        string nameList = string.Join(", ", names.Select(n => $"\"{n}\""));
        string lastEmployee = names.Count > 0 ? names[^1] : "the last employee";

        // Reverted from P13a: restored P11 behavior — day-name keys (Sun/Mon/Tue/Wed/Thu/Fri/Sat)
        // with the actual date shown in parentheses beside each day name in the prompt.
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        string dayStr = string.Join(", ", dayNames.Select((d, i) =>
        {
            if (i >= dates.Count) return d;
            string nd = NormalizeIsoDate(dates[i]);
            return nd.Length >= 5 ? $"{d} ({nd[5..].Replace("-", "/")})" : d;
        }));
        string dayKeyList    = string.Join(", ", dayNames.Select(k => $"\"{k}\""));
        string dayKeyExample = string.Join(",", dayNames.Select(k => $"\"{k}\":\"<value>\""));

        string prompt =
            "Look at this work schedule image.\n" +
            $"Extract shift values for these {names.Count} employees: {nameList}.\n" +
            "For each employee, find their row in the main scheduling table and read each day column carefully.\n" +
            $"The main table ends at the row labeled \"{lastEmployee}\" — do NOT read the secondary table below it.\n" +
            $"The 7 day columns are: {dayStr}.\n" +
            "Each cell shows one of: a time range (e.g. 9:00-5:30), RTO, PTO, x (day off), or is blank.\n" +
            "Each cell also shows an hours number — IGNORE the number, only report the shift label.\n" +
            "X marks may be printed in RED ink or any other color — treat ANY X or checkmark (red, black, or any color) as \"x\".\n" +
            "\"x\" and \"\" (blank) are DIFFERENT: an X mark in a cell = \"x\"; a completely empty cell = \"\".\n" +
            "Reply with ONLY this JSON (no markdown, no explanation). Use day names as keys:\n" +
            "{\n" +
            $"  \"<EmployeeName>\": {{{dayKeyExample}}},\n" +
            "  ...\n" +
            "}\n" +
            "Rules:\n" +
            "- Use empty string \"\" ONLY when a cell is completely empty/blank.\n" +
            "- Use \"x\" when a cell contains an X mark or checkmark (day off).\n" +
            "- Copy time ranges exactly as written (e.g. \"9:00-5:30\", \"10:00-6:30\", \"12:00-4:30\").\n" +
            "- Use \"RTO\" or \"PTO\" when those labels appear in the cell.\n" +
            "- Do NOT include hours numbers in values. Do NOT omit any employee or any day.\n" +
            $"- Include ALL employees. Use exactly these day-name keys: {dayKeyList}.\n" +
            // P11: warn about blank/holiday columns to prevent WRONG-COL drift
            "- IMPORTANT: Any date column may be entirely blank for ALL employees (e.g., a public holiday). " +
            "If every employee in a column has no shift, output \"\" for all of them — do NOT redistribute values from an adjacent column to fill a blank column.\n" +
            // P20: negative anti-WRONG-COL warning — explicitly forbid the right-shift error pattern
            "- CRITICAL: Do NOT shift values one column to the right. Each cell value belongs ONLY in the column " +
            "whose header is directly above that cell. If you read a value from the Mon column, it must be stored " +
            "under the \"Mon\" key — never under \"Tue\" or any other key. Read each column header and its cells independently.";

        // num_predict: employees × 7 day-value pairs × ~30 chars avg + JSON overhead
        int predictBudget = Math.Max(3000, names.Count * 7 * 30 + 800);
        string raw = await CallOllamaAsync(base64Image, prompt, ct, numPredict: predictBudget);

        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            foreach (var emp in doc.RootElement.EnumerateObject())
            {
                string empName = emp.Name;
                var shifts = new List<object>();

                for (int i = 0; i < 7; i++)
                {
                    string date  = i < dates.Count ? NormalizeIsoDate(dates[i]) : "";
                    string shift = "";

                    if (emp.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (emp.Value.TryGetProperty(dayNames[i], out var dayProp))
                            shift = dayProp.GetString() ?? "";
                    }
                    else if (emp.Value.ValueKind == JsonValueKind.Array)
                    {
                        var arr = emp.Value.EnumerateArray().ToList();
                        if (i < arr.Count) shift = arr[i].GetString() ?? "";
                    }

                    shift = shift.Trim();
                    shift = Regex.Replace(shift, @"\s+\d+(\.\d+)?$", "").Trim();
                    if (Regex.IsMatch(shift, @"^\d+\.?\d*$")) shift = "";
                    shifts.Add(new { Date = date, Shift = shift });
                }
                result[empName] = shifts;
            }
        }
        catch
        {
            // Return empty — caller will fall back to per-employee
        }
        return result;
    }

    // ── CSV-format row extraction (Phase 41 experiment) ──────────────────────
    // Uses image-visible date headers as CSV column keys instead of abstract day names.
    // Structural advantage: column count is verifiable; wrong-count rows can be flagged.

    private async Task<Dictionary<string, List<object>>> ExtractAllShiftsCsvAsync(
        string base64Image, List<string> names, List<string> dates, CancellationToken ct)
    {
        string nameList = string.Join(", ", names.Select(n => $"\"{n}\""));
        string lastEmployee = names.Count > 0 ? names[^1] : "the last employee";

        // Build date label strings to use as CSV column headers (e.g. "10/26", "10/27", ...)
        // These match what the model can visually read in the image header row.
        var dateLabels = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if (i < dates.Count)
            {
                string nd = NormalizeIsoDate(dates[i]);
                dateLabels.Add(nd.Length >= 5 ? nd[5..].Replace("-", "/") : $"Day{i + 1}");
            }
            else dateLabels.Add($"Day{i + 1}");
        }
        string csvHeader = "Employee," + string.Join(",", dateLabels);
        string csvExample = "<EmployeeName>," + string.Join(",", Enumerable.Repeat("<value>", 7));

        string prompt =
            "Look at this work schedule image.\n" +
            $"Extract shift values for these {names.Count} employees: {nameList}.\n" +
            "For each employee, find their row in the main scheduling table and read each day column carefully.\n" +
            $"The main table ends at the row labeled \"{lastEmployee}\" — do NOT read the secondary table below it.\n" +
            "Each cell shows one of: a time range (e.g. 9:00-5:30), RTO, PTO, x (day off), or is blank.\n" +
            "Each cell also shows an hours number — IGNORE the number, only report the shift label.\n" +
            "X marks may be printed in RED ink or any other color — treat ANY X or checkmark as \"x\".\n" +
            "\"x\" = day off (X mark present). Empty string = completely blank cell (no value, no mark).\n" +
            "Reply with ONLY a plain CSV table (no markdown, no explanation, no quotes around values).\n" +
            "First line must be exactly this header:\n" +
            csvHeader + "\n" +
            "Then one data row per employee:\n" +
            csvExample + "\n" +
            "Rules:\n" +
            "- CRITICAL: Do NOT shift values one column to the right. Each cell value belongs ONLY in the " +
            "column whose header date is directly above that cell in the image. Read each column header and " +
            "its cells independently.\n" +
            "- Blank cell = leave empty between the commas (two consecutive commas ,, or trailing comma for last column).\n" +
            "- Copy time ranges exactly as written (e.g. 9:00-5:30, 10:00-6:30, 12:00-4:30).\n" +
            "- Do NOT include hours numbers in values. Do NOT omit any employee or any day.\n" +
            $"- Include ALL {names.Count} employees, one row each.\n" +
            "- IMPORTANT: Any date column may be entirely blank for ALL employees (e.g., a public holiday). " +
            "Output empty for all of them — do NOT redistribute values from an adjacent column to fill it.";

        int predictBudget = Math.Max(2500, names.Count * 7 * 15 + 500);
        string raw = await CallOllamaAsync(base64Image, prompt, ct, isJson: false, numPredict: predictBudget);

        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                // Skip header row and any preamble lines (must contain at least one comma)
                int commaCount = line.Count(c => c == ',');
                if (commaCount == 0) continue;

                var parts = line.Split(',');
                string empName = parts[0].Trim().Trim('"').Trim();
                if (string.IsNullOrEmpty(empName)) continue;

                // Skip if first token looks like the CSV header
                if (empName.Equals("Employee", StringComparison.OrdinalIgnoreCase)) continue;
                // Skip if first token looks like a date label (e.g. "10/26")
                if (Regex.IsMatch(empName, @"^\d+/\d+")) continue;

                var shifts = new List<object>();
                for (int i = 0; i < 7; i++)
                {
                    string date  = i < dates.Count ? NormalizeIsoDate(dates[i]) : "";
                    string shift = i + 1 < parts.Length ? parts[i + 1].Trim().Trim('"').Trim() : "";
                    shift = Regex.Replace(shift, @"\s+\d+(\.\d+)?$", "").Trim();
                    if (Regex.IsMatch(shift, @"^\d+\.?\d*$")) shift = "";
                    shifts.Add(new { Date = date, Shift = shift });
                }
                result[empName] = shifts;
            }
        }
        catch
        {
            // Return empty — caller will fall back to per-employee
        }
        return result;
    }

    // ── Dual-view cross-reference extraction (Phase 42) ─────────────────────
    // Asks for BOTH a per-employee row view AND a per-day column view in ONE call.
    // Cells where both views agree → high-confidence value.
    // Cells where both views produce different non-empty values → "" (unresolvable,
    //   let the other 5 majority-vote runs determine it).
    // Cells where only one view has a value → use that value.
    // This result is added as a 6th run (5 existing + 1 dual) to the majority vote.

    private async Task<Dictionary<string, List<object>>> ExtractAllShiftsDualAsync(
        string base64Image, List<string> names, List<string> dates, CancellationToken ct)
    {
        string nameList = string.Join(", ", names.Select(n => $"\"{n}\""));
        string lastEmployee = names.Count > 0 ? names[^1] : "the last employee";

        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        string dayStr = string.Join(", ", dayNames.Select((d, i) =>
        {
            if (i >= dates.Count) return d;
            string nd = NormalizeIsoDate(dates[i]);
            return nd.Length >= 5 ? $"{d} ({nd[5..].Replace("-", "/")})" : d;
        }));
        string dayKeyList = string.Join(", ", dayNames.Select(k => $"\"{k}\""));

        string prompt =
            "Look at this work schedule image.\n" +
            $"Extract shift values for these {names.Count} employees: {nameList}.\n" +
            $"The main table ends at the row labeled \"{lastEmployee}\" — do NOT read the secondary table below it.\n" +
            $"The 7 day columns are: {dayStr}.\n" +
            "Each cell shows one of: a time range (e.g. 9:00-5:30), RTO, PTO, x (day off), or is blank.\n" +
            "Ignore any hours number shown in a cell — only report the shift label.\n" +
            "X marks may be printed in RED ink or any color — treat ANY X or checkmark as \"x\".\n" +
            "\"x\" = day off. \"\" = completely blank cell with no value or mark.\n" +
            "Reply with ONLY this JSON (no markdown, no explanation):\n" +
            "{\n" +
            "  \"by_employee\": {\n" +
            "    \"<EmployeeName>\": {\"Sun\": \"<value>\", \"Mon\": \"<value>\", \"Tue\": \"<value>\", \"Wed\": \"<value>\", \"Thu\": \"<value>\", \"Fri\": \"<value>\", \"Sat\": \"<value>\"},\n" +
            "    ...\n" +
            "  },\n" +
            "  \"by_day\": {\n" +
            "    \"Sun\": {\"<EmployeeName>\": \"<value>\", ...},\n" +
            "    \"Mon\": {\"<EmployeeName>\": \"<value>\", ...},\n" +
            "    ...\n" +
            "  }\n" +
            "}\n" +
            "For by_employee: read each employee row left-to-right across all 7 day columns.\n" +
            "For by_day: read each day column top-to-bottom, one value per employee.\n" +
            "Rules:\n" +
            "- Use \"\" for completely blank cells. Use \"x\" for X marks or checkmarks.\n" +
            "- Copy time ranges exactly as written (e.g. \"9:00-5:30\", \"10:00-6:30\").\n" +
            "- Do NOT include hours numbers in values. Include ALL employees and ALL days.\n" +
            $"- Use exactly these day-name keys: {dayKeyList}.\n" +
            "- IMPORTANT: Any date column may be entirely blank for ALL employees (e.g., a public holiday). " +
            "Output \"\" for all of them — do NOT redistribute values from an adjacent column.\n" +
            "- CRITICAL: Do NOT shift values one column to the right. Each cell value belongs ONLY in the " +
            "column whose header is directly above that cell. Read each column header and its cells independently.";

        // Budget: two full grids in one response
        int predictBudget = Math.Max(6000, names.Count * 7 * 60 + 1500);
        string raw = await CallOllamaAsync(base64Image, prompt, ct, numPredict: predictBudget);

        // ── Parse both views ──────────────────────────────────────────────────
        var byEmployee = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var byDay      = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(raw);

            // Parse by_employee
            if (doc.RootElement.TryGetProperty("by_employee", out var empElem))
            {
                foreach (var emp in empElem.EnumerateObject())
                {
                    var days = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (emp.Value.ValueKind == JsonValueKind.Object)
                        foreach (var day in emp.Value.EnumerateObject())
                            days[day.Name] = day.Value.GetString() ?? "";
                    byEmployee[emp.Name] = days;
                }
            }

            // Parse by_day
            if (doc.RootElement.TryGetProperty("by_day", out var dayElem))
            {
                foreach (var day in dayElem.EnumerateObject())
                {
                    var emps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (day.Value.ValueKind == JsonValueKind.Object)
                        foreach (var emp in day.Value.EnumerateObject())
                            emps[emp.Name] = emp.Value.GetString() ?? "";
                    byDay[day.Name] = emps;
                }
            }
        }
        catch { /* return empty on parse failure */ }

        // ── Cross-reference: build result per employee ────────────────────────
        var result = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            byEmployee.TryGetValue(name, out var empDays);
            var shifts = new List<object>();

            for (int i = 0; i < 7; i++)
            {
                string dayName = dayNames[i];
                string date    = i < dates.Count ? NormalizeIsoDate(dates[i]) : "";

                // Row view: what by_employee says for this employee/day
                string rowVal = "";
                if (empDays != null && empDays.TryGetValue(dayName, out var rv))
                    rowVal = (rv ?? "").Trim();
                rowVal = Regex.Replace(rowVal, @"\s+\d+(\.\d+)?$", "").Trim();
                if (Regex.IsMatch(rowVal, @"^\d+\.?\d*$")) rowVal = "";

                // Column view: what by_day says for this day/employee
                string colVal = "";
                if (byDay.TryGetValue(dayName, out var dayEmps) && dayEmps.TryGetValue(name, out var cv))
                    colVal = (cv ?? "").Trim();
                colVal = Regex.Replace(colVal, @"\s+\d+(\.\d+)?$", "").Trim();
                if (Regex.IsMatch(colVal, @"^\d+\.?\d*$")) colVal = "";

                string chosen;
                if (string.Equals(rowVal, colVal, StringComparison.OrdinalIgnoreCase))
                {
                    // Both views agree (including both empty)
                    chosen = rowVal;
                }
                else if (string.IsNullOrEmpty(rowVal))
                {
                    // Only column view has a value — use it
                    chosen = colVal;
                }
                else if (string.IsNullOrEmpty(colVal))
                {
                    // Only row view has a value — use it
                    chosen = rowVal;
                }
                else
                {
                    // Both views have different non-empty values — unresolvable disagreement.
                    // Return "" so the other 5 majority-vote runs determine this cell.
                    chosen = "";
                }

                shifts.Add(new { Date = date, Shift = chosen });
            }
            result[name] = shifts;
        }
        return result;
    }

    // ── Per-employee row extraction (fallback) ────────────────────────────────

    private async Task<List<object>> ExtractRowAsync(
        string base64Image, string employeeName, List<string> dates, CancellationToken ct)
    {
        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        string dayStr = string.Join(", ", dayNames.Select((d, i) =>
        {
            if (i >= dates.Count) return d;
            string nd = NormalizeIsoDate(dates[i]);
            return nd.Length >= 5 ? $"{d} ({nd[5..].Replace("-", "/")})" : d;
        }));

        string prompt =
            $"Look at this work schedule image. Find the row labeled \"{employeeName}\".\n" +
            $"The 7 day columns are: {dayStr}.\n" +
            "Each cell shows a shift label or is blank. Ignore the hours number in each cell.\n" +
            "\"x\" = X mark (day off). \"\" = completely empty cell. RTO/PTO as written.\n" +
            "Reply with ONLY this JSON (no markdown, no explanation):\n" +
            "{\"Sun\":\"...\",\"Mon\":\"...\",\"Tue\":\"...\",\"Wed\":\"...\",\"Thu\":\"...\",\"Fri\":\"...\",\"Sat\":\"...\"}\n" +
            "Copy time ranges exactly (e.g. \"9:00-5:30\", \"10:00-6:30\"). Do not explain.";

        string raw = await CallOllamaAsync(base64Image, prompt, ct, isJson: true, numPredict: 600);

        var shifts = new List<object>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            for (int i = 0; i < 7; i++)
            {
                string date  = i < dates.Count ? NormalizeIsoDate(dates[i]) : "";
                string shift = "";
                if (doc.RootElement.TryGetProperty(dayNames[i], out var prop))
                    shift = prop.GetString() ?? "";
                shift = shift.Trim();
                shift = Regex.Replace(shift, @"\s+\d+(\.\d+)?$", "").Trim();
                if (Regex.IsMatch(shift, @"^\d+\.?\d*$")) shift = "";
                shifts.Add(new { Date = date, Shift = shift });
            }
        }
        catch
        {
            // Fallback: return empty shifts so the caller can decide what to do
            for (int i = 0; i < 7; i++)
            {
                string date = i < dates.Count ? NormalizeIsoDate(dates[i]) : "";
                shifts.Add(new { Date = date, Shift = "" });
            }
        }
        return shifts;
    }

    /// <summary>
    /// Core Ollama call with JSON normalization. Returns the cleaned model text (or parsed JSON string).
    /// </summary>
    internal async Task<string> CallOllamaAsync(
        string base64Image, string prompt, CancellationToken ct, bool isJson = true, int numPredict = -1)
    {
        var requestBody = new
        {
            model      = _model,
            prompt,
            images     = new[] { base64Image },
            stream     = false,
            keep_alive = -1,
            options    = new { temperature = 0.0, num_ctx = 16384, num_predict = numPredict } // P15: temperature 0→deterministic
        };

        const int maxAttempts = 3;
        string lastRaw = "";

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1) await Task.Delay(2000, ct);

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await Http.PostAsJsonAsync($"{_baseUrl}/api/generate", requestBody, ct);
            }
            catch (HttpRequestException ex)
            {
                return MakeError($"Ollama not reachable at {_baseUrl}.\n{ex.Message}");
            }
            catch (TaskCanceledException)
            {
                return MakeError("Request timed out after 600 s.");
            }

            string rawBody = await httpResponse.Content.ReadAsStringAsync(ct);
            if (!httpResponse.IsSuccessStatusCode)
                return MakeError($"Ollama HTTP {(int)httpResponse.StatusCode}: {Truncate(rawBody, 300)}");

            string modelText;
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                modelText = doc.RootElement.GetProperty("response").GetString() ?? "";
            }
            catch { return MakeError($"Could not parse Ollama wrapper: {Truncate(rawBody, 300)}"); }

            // Strip Qwen3 thinking blocks <think>...</think> before any other processing
            modelText = Regex.Replace(modelText, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();

            // Strip markdown fences
            modelText = Regex.Replace(modelText.Trim(), @"^```\w*\s*", "", RegexOptions.Multiline);
            modelText = Regex.Replace(modelText,        @"```\s*$",    "", RegexOptions.Multiline);
            modelText = modelText.Trim();

            // Normalize unicode quotes
            modelText = modelText
                .Replace('\u201C', '"').Replace('\u201D', '"')
                .Replace('\u2018', '\'').Replace('\u2019', '\'')
                .Replace('\u00A0', ' ').Replace('\u2014', '-').Replace('\u2013', '-')
                .Replace('\u2026', ' ').Replace('\u00AB', '"').Replace('\u00BB', '"')
                .Replace('\u201E', '"');

            if (!isJson) return modelText;

            // Normalize US date format M/D/YY or M/D/YYYY → ISO 8601
            modelText = Regex.Replace(modelText,
                @"\b(\d{1,2})/(\d{1,2})/(\d{2})\b",
                m => $"20{m.Groups[3].Value}-{int.Parse(m.Groups[1].Value):D2}-{int.Parse(m.Groups[2].Value):D2}");
            modelText = Regex.Replace(modelText,
                @"\b(\d{1,2})/(\d{1,2})/(\d{4})\b",
                m => $"{m.Groups[3].Value}-{int.Parse(m.Groups[1].Value):D2}-{int.Parse(m.Groups[2].Value):D2}");

            // Sanitize malformed dates like "2025-10-26-01"
            modelText = Regex.Replace(modelText, @"(\d{4}-\d{2}-\d{2})-\d+", "$1");

            // Zero-pad single-digit month/day in ISO dates: 2025-11-1 → 2025-11-01
            modelText = Regex.Replace(modelText, @"\b(\d{4})-(\d{1,2})-(\d{1,2})\b",
                m => $"{int.Parse(m.Groups[1].Value):D4}-{int.Parse(m.Groups[2].Value):D2}-{int.Parse(m.Groups[3].Value):D2}");

            // Remove stray hours numbers trailing shift values
            modelText = Regex.Replace(modelText, @"(""Shift""\s*:\s*"")(\d+\.?\d*)("")", "$1$3");
            modelText = Regex.Replace(modelText, @"(?<=[""null])\s+\d+(\.\d+)?(?=\s*[,}\]])", "");
            modelText = Regex.Replace(modelText, @"""\s+\d+(\.\d+)?\s*:", "\":");

            // Extract outermost JSON structure ({ or [)
            char open  = modelText.Contains('[') && modelText.IndexOf('[') < (modelText.IndexOf('{') < 0 ? int.MaxValue : modelText.IndexOf('{')) ? '[' : '{';
            char close = open == '[' ? ']' : '}';
            int  start = modelText.IndexOf(open);
            int  end   = modelText.LastIndexOf(close);
            if (start < 0 || end <= start) { lastRaw = modelText; continue; }

            string json = modelText[start..(end + 1)];
            json = RepairTruncatedJson(json);
            json = EscapeControlCharsInStrings(json);

            try { JsonDocument.Parse(json); return json; }
            catch { lastRaw = json; /* retry */ }
        }

        return lastRaw; // return best effort even if not valid JSON
    }

    /// <summary>
    /// Checks if the model is already loaded via /api/ps. If not, sends a
    /// lightweight text-only request to load it before the heavy image call.
    /// </summary>
    internal async Task EnsureModelLoadedAsync(CancellationToken ct)
    {
        try
        {
            // Check running models
            var psResponse = await Http.GetAsync($"{_baseUrl}/api/ps", ct);
            if (psResponse.IsSuccessStatusCode)
            {
                string psBody = await psResponse.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(psBody);
                bool isLoaded = false;
                if (doc.RootElement.TryGetProperty("models", out var models))
                    foreach (var m in models.EnumerateArray())
                        if (m.TryGetProperty("name", out var n) &&
                            n.GetString()?.StartsWith(_model.Split(':')[0], StringComparison.OrdinalIgnoreCase) == true)
                        { isLoaded = true; break; }

                if (isLoaded) return;
            }
        }
        catch { /* fall through to warmup */ }

        // Model not loaded — send a cheap text prompt to load it into GPU memory
        Console.Write(" [loading model...]");
        var sw = Stopwatch.StartNew();
        var warmup = new { model = _model, prompt = "hi", stream = false, keep_alive = -1 };
        try
        {
            await Http.PostAsJsonAsync($"{_baseUrl}/api/generate", warmup, ct);
            sw.Stop();
            Console.Write($" [loaded in {sw.Elapsed.TotalSeconds:F1}s]");
        }
        catch
        {
            Console.Write(" [warmup failed, attempting anyway]");
        }
    }

    // ── P9: Name normalisation against known reference list ─────────────────

    /// <summary>
    /// After ExtractNamesAsync, maps each extracted name to the closest entry in
    /// _knownNames (Levenshtein ≤ 2 or parenthetical-suffix strip) and deduplicates.
    /// Fixes phantom names like "Athena(train)" → "Athena" and "Clara" → "Ciara".
    /// </summary>
    internal List<string> NormalizeNamesAgainstKnown(
        List<string> extracted, IReadOnlyList<string>? extraKnown = null)
    {
        var result  = new List<string>(extracted.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allKnown = extraKnown?.Count > 0
            ? _knownNames.Concat(extraKnown).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : _knownNames;

        foreach (var raw in extracted)
        {
            // Strip trailing parenthetical: "Athena(train)" → "Athena"
            string cleaned = Regex.Replace(raw, @"\s*\(.*?\)\s*$", "").Trim();
            if (string.IsNullOrEmpty(cleaned)) continue;

            // Find the closest known name
            string? best     = null;
            int     bestDist = int.MaxValue;
            foreach (var known in allKnown)
            {
                if (string.Equals(cleaned, known, StringComparison.OrdinalIgnoreCase))
                { best = known; bestDist = 0; break; }
                int d = LevenshteinDist(cleaned, known);
                if (d <= 2 && d < bestDist) { best = known; bestDist = d; }
            }

            string canonical = best ?? cleaned;

            // Deduplicate: if this canonical name is already claimed by an earlier
            // (better) match, drop this phantom entry.
            if (claimed.Contains(canonical)) continue;
            claimed.Add(canonical);
            result.Add(canonical);
        }
        return result;
    }

    internal static int LevenshteinDist(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        int la = a.Length, lb = b.Length;
        if (la == 0) return lb; if (lb == 0) return la;
        var prev = new int[lb + 1]; var curr = new int[lb + 1];
        for (int j = 0; j <= lb; j++) prev[j] = j;
        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= lb; j++)
            {
                int c = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + c);
            }
            Array.Copy(curr, prev, lb + 1);
        }
        return prev[lb];
    }

    /// <summary>Zero-pads month and day in ISO date strings so 2025-11-1 becomes 2025-11-01.</summary>
    internal static string NormalizeIsoDate(string d)
    {
        var m = Regex.Match(d, @"^(\d{4})-(\d{1,2})-(\d{1,2})$");
        if (!m.Success) return d;
        return $"{int.Parse(m.Groups[1].Value):D4}-{int.Parse(m.Groups[2].Value):D2}-{int.Parse(m.Groups[3].Value):D2}";
    }

    private static string MakeError(string msg) =>
        JsonSerializer.Serialize(new { ERROR = msg }, new JsonSerializerOptions { WriteIndented = true });

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// If the model output was cut off mid-JSON (context limit), closes any
    /// unclosed arrays and objects so the string is at least parseable.
    /// </summary>
    private static string RepairTruncatedJson(string json)
    {
        var stack = new Stack<char>();
        bool inString = false;
        bool escaped  = false;

        foreach (char c in json)
        {
            if (escaped)           { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"')          { inString = !inString; continue; }
            if (inString)          continue;

            if (c == '{') stack.Push('}');
            else if (c == '[') stack.Push(']');
            else if (c == '}' || c == ']')
            {
                if (stack.Count > 0 && stack.Peek() == c) stack.Pop();
            }
        }

        if (stack.Count == 0) return json;

        // Strip any dangling comma or incomplete token before closing
        string trimmed = json.TrimEnd();
        trimmed = Regex.Replace(trimmed, @",\s*$", "");  // remove trailing comma
        trimmed = Regex.Replace(trimmed, @"""[^""]*$", "\"\"");  // close open string

        var sb = new System.Text.StringBuilder(trimmed);
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    /// <summary>
    /// Walks the JSON string and escapes any bare control characters (newlines,
    /// tabs, carriage returns) that appear inside string values, which would
    /// otherwise cause System.Text.Json to throw on parse.
    /// </summary>
    private static string EscapeControlCharsInStrings(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        bool escaped  = false;

        foreach (char c in json)
        {
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\' && inString) { escaped = true; sb.Append(c); continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }

            if (inString && c < 0x20)
            {
                // Replace bare control characters with their JSON escape sequences
                sb.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _    => $"\\u{(int)c:x4}"
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // P18: Count how many shift values in a list match a valid format.
    // Valid: "" (blank), "x", "RTO", "PTO", or a time range like "9:00-5:30".
    // Used to guard re-query application — only replace existing data if quality >= existing.
    private static int CountValidShifts(IEnumerable<object> shifts)
    {
        int count = 0;
        foreach (dynamic cell in shifts)
        {
            string s = cell.Shift ?? "";
            if (string.IsNullOrEmpty(s) ||
                string.Equals(s, "x",   StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "RTO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "PTO", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,2}:\d{2}-\d{1,2}:\d{2}$"))
                count++;
        }
        return count;
    }
}
