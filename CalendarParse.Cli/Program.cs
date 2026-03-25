using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarParse.Cli.Services;
using CalendarParse.Models;
using CalendarParse.Services;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding  = Encoding.UTF8;

// ── Argument parsing ──────────────────────────────────────────────────────────
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

string folder = args[0];
string nameFilter    = string.Empty;
bool   testMode      = false;
bool   visionMode    = false;
bool   hybridMode    = false;
bool   debugMode     = false;
bool   halvesMode    = false;
int    resizeWidth   = 0;
string ollamaModel    = OllamaCalendarService.DefaultModel;
string preprocessArg  = string.Empty;
string knownNamesArg  = string.Empty;
string ensembleModel  = string.Empty;

for (int i = 1; i < args.Length; i++)
{
    if ((args[i] is "--name" or "-n") && i + 1 < args.Length)
        nameFilter = args[++i];
    else if (args[i] is "--test" or "-t")
        testMode = true;
    else if (args[i] is "--vision" or "-V")
        visionMode = true;
    else if (args[i] is "--hybrid")
        hybridMode = true;
    else if (args[i] is "--debug" or "-d")
        debugMode = true;
    else if (args[i] is "--model" && i + 1 < args.Length)
        ollamaModel = args[++i];
    else if (args[i] is "--preprocess" && i + 1 < args.Length)
        preprocessArg = args[++i];
    else if (args[i] is "--resize" && i + 1 < args.Length)
        int.TryParse(args[++i], out resizeWidth);
    else if (args[i] is "--halves")
        halvesMode = true;
    else if (args[i] is "--known-names" && i + 1 < args.Length)
        knownNamesArg = args[++i];
    else if (args[i] is "--ensemble" && i + 1 < args.Length)
        ensembleModel = args[++i];
}

PreprocessMode preprocessMode = PreprocessMode.None;
if (!string.IsNullOrEmpty(preprocessArg))
{
    if (!Enum.TryParse<PreprocessMode>(preprocessArg, ignoreCase: true, out preprocessMode))
    {
        Console.Error.WriteLine($"ERROR: Unknown --preprocess mode '{preprocessArg}'. Valid: none, current, clahe, llm, denoise");
        return 1;
    }
}

if (!Directory.Exists(folder))
{
    Console.Error.WriteLine($"ERROR: Folder not found: {folder}");
    return 1;
}

// ── Service wiring ────────────────────────────────────────────────────────────
ICalendarParseService parser;
var preprocessorInst = new WindowsImagePreprocessor();
WindowsTableDetector?     tableDetectorInst = null;
WindowsOcrService?        ocrServiceInst    = null;
CalendarStructureAnalyzer? analyzerInst     = null;

string[] knownNamesArr = string.IsNullOrEmpty(knownNamesArg)
    ? Array.Empty<string>()
    : knownNamesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (hybridMode)
{
    parser = new HybridCalendarService(
        model:      ollamaModel,
        knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null);
    Console.WriteLine($"Mode: HYBRID (Ollama model: {ollamaModel} + WinRT OCR + grid crop)");
    if (knownNamesArr.Length > 0)
        Console.WriteLine($"Known names: {string.Join(", ", knownNamesArr)}");
}
else if (visionMode)
{
    parser = new OllamaCalendarService(model: ollamaModel, knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null);
    Console.WriteLine($"Mode: VISION (Ollama model: {ollamaModel})");
    if (knownNamesArr.Length > 0)
        Console.WriteLine($"Known names: {string.Join(", ", knownNamesArr)}");
    if (!string.IsNullOrEmpty(ensembleModel))
        Console.WriteLine($"Ensemble: {ensembleModel} (blank-fill pass)");
}
else
{
    tableDetectorInst = new WindowsTableDetector();
    ocrServiceInst    = new WindowsOcrService();
    analyzerInst      = new CalendarStructureAnalyzer();
    parser = new CalendarParseService(preprocessorInst, tableDetectorInst, ocrServiceInst, analyzerInst);
}

// ── Batch processing ──────────────────────────────────────────────────────────
var imageFiles = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
    .Where(f => f.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".png",  StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f)
    .ToList();

if (imageFiles.Count == 0)
{
    Console.Error.WriteLine($"No jpg/jpeg/png files found in: {folder}");
    return 1;
}

Console.WriteLine($"Found {imageFiles.Count} image(s) in {folder}");
if (!string.IsNullOrWhiteSpace(nameFilter))
    Console.WriteLine($"Name filter: \"{nameFilter}\"");
if (testMode)
    Console.WriteLine("Mode: TEST (writing .guess.json, comparing against .answer.json)");if (debugMode)
    Console.WriteLine("Mode: DEBUG (saving annotated pipeline images to <name>.debug-imgs/ folders)");Console.WriteLine();

var results     = new List<(string file, bool ok, string detail)>();
var outputPaths = new Dictionary<string, string>(); // imagePath → outPath (for test mode)

foreach (var imagePath in imageFiles)
{
    string name   = Path.GetFileNameWithoutExtension(imagePath);
    string suffix = preprocessMode == PreprocessMode.None
        ? ".output.json"
        : $".{SanitizeModelName(ollamaModel)}.{preprocessMode.ToString().ToLower()}.output.json";
    string outPath = Path.Combine(folder, name + suffix);
    outputPaths[imagePath] = outPath;

    Console.Write($"  {Path.GetFileName(imagePath)} ... ");

    var imageTimer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        byte[] rawBytes = await File.ReadAllBytesAsync(imagePath);

        // Optional: resize before any preprocessing so the model sees a predictable resolution.
        if (resizeWidth > 0)
            rawBytes = preprocessorInst.ResizeToWidth(rawBytes, resizeWidth);

        byte[] processedBytes = preprocessorInst.PreprocessBytes(rawBytes, preprocessMode);

        // Write the preprocessed image to a dedicated subdirectory so it is
        // never picked up as an input image on subsequent runs.
        string debugExt    = WindowsImagePreprocessor.GetExtension(preprocessMode);
        string debugImgDir = Path.Combine(folder, "preprocess-debug");
        Directory.CreateDirectory(debugImgDir);
        string debugImagePath = Path.Combine(debugImgDir, $"{name}_{preprocessMode.ToString().ToLower()}{debugExt}");
        await File.WriteAllBytesAsync(debugImagePath, processedBytes);

        string output = await parser.ProcessAsync(new MemoryStream(processedBytes), nameFilter);

        // ── Optional halves pass: re-extract bottom employees from a header + bottom-half
        // composite image and merge, to give the model more pixel budget on lower-table rows.
        if (halvesMode && visionMode)
        {
            Console.Write(" [halves...");
            byte[] bottomBytes     = preprocessorInst.CreateHeaderAndBottomHalf(rawBytes);
            byte[] processedBottom = preprocessorInst.PreprocessBytes(bottomBytes, preprocessMode);

            // Save debug image of the composite for inspection
            await File.WriteAllBytesAsync(
                Path.Combine(debugImgDir, $"{name}_bottom.jpg"), bottomBytes);

            string outputB  = await parser.ProcessAsync(new MemoryStream(processedBottom), nameFilter);
            string jsonB    = ExtractJson(outputB);
            output          = MergeCalendarResults(output, jsonB);
            Console.Write("] ");
        }

        // ── P8: Ensemble blank-fill pass ──────────────────────────────────────
        // Run a secondary model and use its values to fill any cells the primary
        // model left blank (""). Never overwrites a non-blank primary value.
        if (!string.IsNullOrEmpty(ensembleModel) && visionMode)
        {
            Console.Write(" [ensemble...");
            string[] kn = string.IsNullOrEmpty(knownNamesArg)
                ? Array.Empty<string>()
                : knownNamesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ensembleSvc = new OllamaCalendarService(model: ensembleModel, knownNames: kn.Length > 0 ? kn : null);
            string outputE  = await ensembleSvc.ProcessAsync(new MemoryStream(processedBytes), nameFilter);
            string jsonE    = ExtractJson(outputE);
            output          = MergeBlankFill(output, jsonE);
            Console.Write("] ");
        }

        imageTimer.Stop();

        // Extract just the JSON portion (after the debug report)
        string jsonOnly = ExtractJson(output);

        await File.WriteAllTextAsync(outPath, jsonOnly);

        // Save full debug report for tuning
        string debugPath = Path.Combine(folder, name + ".debug.txt");
        await File.WriteAllTextAsync(debugPath, output);

        // Count employees for the summary
        int empCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(jsonOnly);
            if (doc.RootElement.TryGetProperty("Employees", out var emps))
                empCount = emps.GetArrayLength();
        }
        catch { /* ignore parse errors in summary */ }

        Console.WriteLine($"OK  ({empCount} employees → {Path.GetFileName(outPath)})  [{imageTimer.Elapsed.TotalSeconds:F1}s]");
        results.Add((Path.GetFileName(imagePath), true, $"{empCount} employees  {imageTimer.Elapsed.TotalSeconds:F1}s"));

        // ── Optional debug image pass ─────────────────────────────────────────
        if (debugMode && !visionMode &&
            tableDetectorInst != null && ocrServiceInst != null)
        {
            string debugDir = Path.Combine(folder, name + ".debug-imgs");
            try
            {
                Console.Write($"      [debug] saving pipeline images → {Path.GetFileName(debugDir)}/ ... ");
                var debugTimer = System.Diagnostics.Stopwatch.StartNew();
                await SaveDebugImagesAsync(imagePath, debugDir,
                    preprocessorInst, tableDetectorInst, ocrServiceInst);
                debugTimer.Stop();
                Console.WriteLine($"done [{debugTimer.Elapsed.TotalSeconds:F1}s]");
            }
            catch (Exception debugEx)
            {
                Console.WriteLine($"WARN: debug images failed — {debugEx.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        imageTimer.Stop();
        Console.WriteLine($"FAIL  {ex.Message}  [{imageTimer.Elapsed.TotalSeconds:F1}s]");
        results.Add((Path.GetFileName(imagePath), false, ex.Message));
    }
}

// ── Summary ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("── Summary ─────────────────────────────────────────────────");
int passed = results.Count(r => r.ok);
int failed = results.Count - passed;
foreach (var (file, ok, detail) in results)
    Console.WriteLine($"  {(ok ? "✓" : "✗")} {file,-40} {detail}");
Console.WriteLine();
Console.WriteLine($"  {passed} succeeded, {failed} failed");

// ── Test comparison (only in --test mode) ─────────────────────────────────────
if (testMode)
{
    Console.WriteLine();
    Console.WriteLine("── Test Results ─────────────────────────────────────────────");

    int totalExpected = 0, totalMatched = 0;
    // per-employee stats: imageName → (empName → (matched, expected))
    var allImageEmpStats = new List<(string imgName, Dictionary<string, (int matched, int expected)> stats)>();

    foreach (var imagePath in imageFiles)
    {
        string name = Path.GetFileNameWithoutExtension(imagePath);
        string guessPath  = outputPaths.TryGetValue(imagePath, out var op)
            ? op
            : Path.Combine(folder, name + ".output.json");
        string answerPath = Path.Combine(folder, name + ".answer.json");

        if (!File.Exists(guessPath))
        {
            Console.WriteLine($"  SKIP  {name} (no guess file)");
            continue;
        }
        if (!File.Exists(answerPath))
        {
            Console.WriteLine($"  SKIP  {name} (no answer file)");
            continue;
        }

        var diffs = CompareCalendarData(
            await File.ReadAllTextAsync(guessPath),
            await File.ReadAllTextAsync(answerPath),
            out int expected, out int matched, out var imageEmpStats);

        allImageEmpStats.Add((name + ".jpg", imageEmpStats));

        totalExpected += expected;
        totalMatched  += matched;

        bool perfect = diffs.Count == 0;
        Console.WriteLine($"  {(perfect ? "✓" : "✗")} {name + ".jpg",-40} {matched}/{expected} shifts correct");
        foreach (var d in diffs)
            Console.WriteLine($"      {d}");
    }

    Console.WriteLine();
    double pct = totalExpected > 0 ? 100.0 * totalMatched / totalExpected : 0;
    Console.WriteLine($"  Overall: {totalMatched}/{totalExpected} shifts matched ({pct:F1}%)");

    // ── Per-employee breakdown ────────────────────────────────────────────────
    if (allImageEmpStats.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("── Per-Employee Score ────────────────────────────────────────");

        // Build combined stats across all images
        var combined = new Dictionary<string, (int matched, int expected)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, imgStats) in allImageEmpStats)
            foreach (var (emp, s) in imgStats)
            {
                combined.TryGetValue(emp, out var c);
                combined[emp] = (c.matched + s.matched, c.expected + s.expected);
            }

        // Header
        var imgCols = allImageEmpStats.Select(x => x.imgName).ToList();
        string hdr = $"  {"Employee",-20}" + string.Concat(imgCols.Select(n => $"  {n.Replace(".jpg", ""),-14}")) + $"  {"Total",-12}";
        Console.WriteLine(hdr);
        Console.WriteLine("  " + new string('-', hdr.Length - 2));

        foreach (var emp in combined.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            bool isFranny = emp.Equals("Franny", StringComparison.OrdinalIgnoreCase);
            string row = $"  {emp,-20}";
            foreach (var (imgName, imgStats) in allImageEmpStats)
            {
                if (imgStats.TryGetValue(emp, out var s) && s.expected > 0)
                {
                    double p = 100.0 * s.matched / s.expected;
                    row += $"  {s.matched}/{s.expected} ({p:F0}%)".PadRight(16);
                }
                else
                    row += $"  {"-",-14}";
            }
            var tot = combined[emp];
            double tp = tot.expected > 0 ? 100.0 * tot.matched / tot.expected : 0;
            row += $"  {tot.matched}/{tot.expected} ({tp:F0}%)";
            if (isFranny) row += "  ◄";
            Console.WriteLine(row);
        }
    }
}

return failed > 0 ? 1 : 0;

// ── Helpers ───────────────────────────────────────────────────────────────────
static string ExtractJson(string output)
{
    // The debug report is followed by a blank line and then the JSON
    int jsonStart = output.IndexOf('\n');
    while (jsonStart >= 0 && jsonStart < output.Length - 1)
    {
        int next = jsonStart + 1;
        if (next < output.Length && output[next] == '{')
            return output[next..].Trim();
        jsonStart = output.IndexOf('\n', next);
    }
    // Fallback: return the whole output
    return output.Trim();
}

/// <summary>
/// Compares guess JSON vs answer JSON for calendar data.
/// Ignores CapturedAt. Returns list of human-readable diff strings.
/// </summary>
static List<string> CompareCalendarData(
    string guessJson, string answerJson,
    out int totalExpected, out int totalMatched,
    out Dictionary<string, (int matched, int expected)> perEmployeeStats)
{
    totalExpected    = 0;
    totalMatched     = 0;
    perEmployeeStats = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
    var diffs = new List<string>();

    JsonDocument? guessDoc = null, answerDoc = null;
    try { guessDoc  = JsonDocument.Parse(guessJson); }
    catch { diffs.Add("GUESS parse error"); return diffs; }
    try { answerDoc = JsonDocument.Parse(answerJson); }
    catch { diffs.Add("ANSWER parse error"); return diffs; }

    using (guessDoc) using (answerDoc)
    {
        var g = guessDoc.RootElement;
        var a = answerDoc.RootElement;

        // Month / Year
        string gMonth = g.TryGetProperty("Month", out var gm) ? gm.GetString() ?? "" : "";
        string aMonth = a.TryGetProperty("Month", out var am) ? am.GetString() ?? "" : "";
        if (!string.Equals(gMonth, aMonth, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"Month: got \"{gMonth}\" expected \"{aMonth}\"");

        int gYear = g.TryGetProperty("Year", out var gy) ? gy.GetInt32() : 0;
        int aYear = a.TryGetProperty("Year", out var ay) ? ay.GetInt32() : 0;
        if (gYear != aYear)
            diffs.Add($"Year: got {gYear} expected {aYear}");

        // Build guess shift lookup: (employeeName, date) → shift text
        var guessShifts = new Dictionary<(string emp, string date), string>();
        if (g.TryGetProperty("Employees", out var gEmps))
        {
            foreach (var emp in gEmps.EnumerateArray())
            {
                string empName = emp.TryGetProperty("Name", out var en) ? en.GetString() ?? "" : "";
                if (!emp.TryGetProperty("Shifts", out var shifts)) continue;
                foreach (var shift in shifts.EnumerateArray())
                {
                    string date = NormalizeDate(shift.TryGetProperty("Date",  out var d) ? d.GetString() ?? "" : "");
                    string text = shift.TryGetProperty("Shift", out var s) ? s.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(date))
                        guessShifts[(empName, date)] = text;
                }
            }
        }

        // Compare against every shift in the answer
        var answerKeys = new HashSet<(string emp, string date)>();
        if (a.TryGetProperty("Employees", out var aEmps))
        {
            foreach (var emp in aEmps.EnumerateArray())
            {
                string empName = emp.TryGetProperty("Name", out var en) ? en.GetString() ?? "" : "";
                if (!emp.TryGetProperty("Shifts", out var shifts)) continue;
                int empExpected = 0, empMatched = 0;
                foreach (var shift in shifts.EnumerateArray())
                {
                    string date     = NormalizeDate(shift.TryGetProperty("Date",  out var d) ? d.GetString() ?? "" : "");
                    string expected = shift.TryGetProperty("Shift", out var s) ? s.GetString() ?? "" : "";

                    totalExpected++;
                    empExpected++;
                    answerKeys.Add((empName, date));

                    // Try exact match, then case-insensitive, then fuzzy (Levenshtein ≤ 2)
                    string? actual = null;
                    if (guessShifts.TryGetValue((empName, date), out var exactMatch))
                        actual = exactMatch;
                    else
                    {
                        foreach (var kv in guessShifts)
                            if (string.Equals(kv.Key.emp, empName, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(kv.Key.date, date, StringComparison.OrdinalIgnoreCase))
                            { actual = kv.Value; break; }

                        if (actual is null)
                        {
                            // Fuzzy name match: try candidates within Levenshtein distance ≤ 2
                            foreach (var kv in guessShifts)
                                if (string.Equals(kv.Key.date, date, StringComparison.OrdinalIgnoreCase)
                                    && Levenshtein(kv.Key.emp, empName) <= 2)
                                { actual = kv.Value; break; }
                        }
                    }

                    if (actual is not null)
                    {
                        if (ShiftsMatch(actual, expected))
                        { totalMatched++; empMatched++; }
                        else
                            diffs.Add($"{empName} {date}: got \"{actual}\" expected \"{expected}\"");
                    }
                    else
                    {
                        diffs.Add($"{empName} {date}: MISSING (expected \"{expected}\")");
                    }
                }
                // Record per-employee stats for this image
                if (empExpected > 0)
                    perEmployeeStats[empName] = (empMatched, empExpected);
            }
        }

        // Extra shifts in guess not in answer
        foreach (var (key, shiftText) in guessShifts)
            if (!answerKeys.Any(k => string.Equals(k.emp,  key.emp,  StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(k.date, key.date, StringComparison.OrdinalIgnoreCase)))
                diffs.Add($"{key.emp} {key.date}: EXTRA (got \"{shiftText}\", not in answer)");
    }

    return diffs;
}

/// <summary>
/// Returns true if two shift values are equivalent.
/// "" (blank) and "x" are both treated as "not working" and considered a match.
/// </summary>
static bool ShiftsMatch(string a, string b)
{
    a = a.Trim();
    b = b.Trim();
    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
    bool aOff = a == "" || string.Equals(a, "x", StringComparison.OrdinalIgnoreCase);
    bool bOff = b == "" || string.Equals(b, "x", StringComparison.OrdinalIgnoreCase);
    return aOff && bOff;
}

/// <summary>Converts a model name to a filesystem-safe string (colons and dots → hyphens).</summary>
static string SanitizeModelName(string model) =>
    model.Replace(':', '-').Replace('.', '-');

/// <summary>
/// Merges two CalendarData JSON strings. <paramref name="jsonB"/> employees override
/// <paramref name="jsonA"/> employees by name (exact or Levenshtein ≤2 match).
/// Used when jsonB is extracted from a bottom-half composite image.
/// </summary>
static string MergeCalendarResults(string outputA, string jsonB)
{
    string jsonA = ExtractJson(outputA);
    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    CalendarData? dataA = null, dataB = null;
    try { dataA = JsonSerializer.Deserialize<CalendarData>(jsonA,  opts); } catch { }
    try { dataB = JsonSerializer.Deserialize<CalendarData>(jsonB,  opts); } catch { }

    if (dataA is null) return outputA;  // A is broken; can't merge
    if (dataB is null) return outputA;  // B is broken; keep A

    // Build dict by name from A; override with B entries (fuzzy name match)
    var merged = dataA.Employees.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
    foreach (var empB in dataB.Employees)
    {
        if (string.IsNullOrWhiteSpace(empB.Name)) continue;
        // Prefer exact match, then fuzzy
        var matchKey = merged.Keys.FirstOrDefault(k =>
            string.Equals(k, empB.Name, StringComparison.OrdinalIgnoreCase) ||
            Levenshtein(k, empB.Name) <= 2);
        if (matchKey is not null)
            merged[matchKey] = empB;
        else
            merged[empB.Name] = empB;
    }

    var result = new CalendarData
    {
        Month     = dataA.Month,
        Year      = dataA.Year,
        Employees = merged.Values.ToList()
    };
    // Return in the same "debug header + newline + json" format that ExtractJson expects
    return "\n" + JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Zero-pads month and day in ISO date strings so 2025-11-1 == 2025-11-01.</summary>
static string NormalizeDate(string d)
{
    var m = System.Text.RegularExpressions.Regex.Match(d, @"^(\d{4})-(\d{1,2})-(\d{1,2})$");
    if (!m.Success) return d;
    return $"{int.Parse(m.Groups[1].Value):D4}-{int.Parse(m.Groups[2].Value):D2}-{int.Parse(m.Groups[3].Value):D2}";
}

/// <summary>
/// P8: Ensemble blank-fill merge.
/// Copies shift values from <paramref name="fillOutput"/> into <paramref name="primaryOutput"/>
/// ONLY for cells where the primary model returned an empty string "".
/// Non-blank primary values are never overwritten.
/// </summary>
static string MergeBlankFill(string primaryOutput, string fillOutput)
{
    string primaryJson = ExtractJson(primaryOutput);
    string fillJson    = ExtractJson(fillOutput);

    JsonElement pRoot, fRoot;
    try { using var d = JsonDocument.Parse(primaryJson); pRoot = d.RootElement.Clone(); }
    catch { return primaryOutput; }
    try { using var d = JsonDocument.Parse(fillJson);    fRoot = d.RootElement.Clone(); }
    catch { return primaryOutput; }

    // Build filler lookup: (name_lower, normalised_date) → shift
    var fillMap = new Dictionary<(string, string), string>();
    if (fRoot.TryGetProperty("Employees", out var fEmpsEl))
    {
        foreach (var fe in fEmpsEl.EnumerateArray())
        {
            string fn = fe.TryGetProperty("Name", out var fnEl) ? fnEl.GetString() ?? "" : "";
            if (!fe.TryGetProperty("Shifts", out var fShifts)) continue;
            foreach (var fs in fShifts.EnumerateArray())
            {
                string date  = NormalizeDate(fs.TryGetProperty("Date",  out var fdEl)  ? fdEl.GetString()  ?? "" : "");
                string shift = fs.TryGetProperty("Shift", out var fshEl) ? fshEl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(fn) && !string.IsNullOrEmpty(date))
                    fillMap[(fn.ToLowerInvariant(), date)] = shift;
            }
        }
    }

    // Rebuild primary employees, filling blanks from secondary
    var employees = new List<object>();
    if (pRoot.TryGetProperty("Employees", out var pEmpsEl))
    {
        // Cache filler employee names for fuzzy matching
        var fillerNames = fRoot.TryGetProperty("Employees", out var fEmps2)
            ? fEmps2.EnumerateArray().Select(fe => fe.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "").ToList()
            : new List<string>();

        foreach (var pe in pEmpsEl.EnumerateArray())
        {
            string pn = pe.TryGetProperty("Name", out var pnEl) ? pnEl.GetString() ?? "" : "";

            // Find best fuzzy filler name match
            string? bestFiller = fillerNames.Count > 0
                ? fillerNames.OrderBy(fn => Levenshtein(pn, fn)).First()
                : null;
            string? bestFillerKey = bestFiller?.ToLowerInvariant();

            var shifts = new List<object>();
            if (pe.TryGetProperty("Shifts", out var pShifts))
            {
                foreach (var ps in pShifts.EnumerateArray())
                {
                    string date  = ps.TryGetProperty("Date",  out var pdEl)  ? pdEl.GetString()  ?? "" : "";
                    string shift = ps.TryGetProperty("Shift", out var pshEl) ? pshEl.GetString() ?? "" : "";
                    string nd    = NormalizeDate(date);

                    // Only fill if primary is blank and filler has a non-blank value
                    if (string.IsNullOrEmpty(shift) && bestFillerKey is not null)
                    {
                        if (fillMap.TryGetValue((bestFillerKey, nd), out var fill) && !string.IsNullOrEmpty(fill))
                            shift = fill;
                    }

                    shifts.Add(new { Date = date, Shift = shift });
                }
            }
            employees.Add(new { Name = pn, Shifts = shifts });
        }
    }

    string month = pRoot.TryGetProperty("Month", out var mEl) ? mEl.GetString() ?? "Unknown" : "Unknown";
    int    year  = pRoot.TryGetProperty("Year",  out var yEl) ? yEl.GetInt32() : 0;

    var result = new { Month = month, Year = year, Employees = employees };
    return "\n" + JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Computes the Levenshtein edit distance between two strings (case-insensitive).
/// Used for fuzzy employee name matching to handle OCR misreads like "Siana"→"Seena".
/// </summary>
static int Levenshtein(string a, string b)
{
    a = a.ToLowerInvariant();
    b = b.ToLowerInvariant();
    int la = a.Length, lb = b.Length;
    if (la == 0) return lb;
    if (lb == 0) return la;
    var prev = new int[lb + 1];
    var curr = new int[lb + 1];
    for (int j = 0; j <= lb; j++) prev[j] = j;
    for (int i = 1; i <= la; i++)
    {
        curr[0] = i;
        for (int j = 1; j <= lb; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
        }
        Array.Copy(curr, prev, lb + 1);
    }
    return prev[lb];
}

static void PrintUsage()
{
    Console.WriteLine("CalendarParse.Cli — batch-processes schedule images into JSON answer keys");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  CalendarParse.Cli <image-folder> [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -n, --name <filter>   Filter results to a specific employee name");
    Console.WriteLine("  -t, --test            Test mode: write .output.json and compare against .answer.json");
    Console.WriteLine("  -V, --vision          Use local Ollama vision model instead of Tesseract");
    Console.WriteLine("  -d, --debug           Save annotated pipeline images to <name>.debug-imgs/ folders");
    Console.WriteLine("  --model <name>        Ollama model to use (default: llama3.2-vision:11b)");
  Console.WriteLine("  --preprocess <mode>   Image preprocessing before the vision model:");
  Console.WriteLine("                          none     Raw image bytes (default — current behaviour)");
  Console.WriteLine("                          current  Grayscale→blur→adaptive-threshold→dilate (legacy OCR pipeline)");
  Console.WriteLine("                          clahe    Grayscale→EqualizeHist (global histogram equalisation)");
  Console.WriteLine("                          llm      Colour unsharp-mask sharpen (preserves RGB signal)");
  Console.WriteLine("                          denoise  Grayscale→fast-denoise→EqualizeHist (for noisy scans)");
  Console.WriteLine("                        Debug image written as <name>_<mode>.jpg/.png alongside source.");
  Console.WriteLine("  --resize <width>      Resize image to this width (px) before sending to model. 0 = no resize.");
  Console.WriteLine("                        qwen2.5vl:7b optimal input width is ~1120px.");
  Console.WriteLine("  --halves              Also run extraction on a header+bottom-half composite image and merge.");
  Console.WriteLine("                        Targets WRONG-COL errors in lower-table employees.");
  Console.WriteLine("  --known-names <csv>   Comma-separated list of expected employee names.");
  Console.WriteLine("                        P9: normalises OCR phantoms (e.g. \"Athena(train)\" → \"Athena\").");
  Console.WriteLine("                        P10: injected into name-extraction prompt for better OCR spellings.");
  Console.WriteLine("  --ensemble <model>    P8: run a secondary Ollama model and use its output to fill");
  Console.WriteLine("                        cells the primary model left blank. Never overwrites non-blank values.");
    Console.WriteLine();
    Console.WriteLine("Output:");
    Console.WriteLine("  Normal: For each image.jpg, writes image.output.json in the same folder.");
    Console.WriteLine("  Test:   For each image.jpg, writes image.output.json and reports accuracy.");
    Console.WriteLine("  Debug:  For each image.jpg, writes annotated PNGs to image.debug-imgs/.");
    Console.WriteLine("          01_original.png       — source image as decoded");
    Console.WriteLine("          02_preprocessed.png   — adaptive-threshold output used for grid detection");
    Console.WriteLine("          03_gridlines.png      — H+V morphological line masks overlaid on original");
    Console.WriteLine("          04_cells.png          — each detected cell colour-coded by role");
    Console.WriteLine("          05_ocr.png            — OCR text shown inside each detected cell");
    Console.WriteLine();
    Console.WriteLine("Requires:");
    Console.WriteLine("  tessdata/eng.traineddata next to the executable.");
    Console.WriteLine("  Download from: https://github.com/tesseract-ocr/tessdata");
}

/// <summary>
/// Runs the preprocessing and grid-detection steps again on a single image and saves
/// annotated debug PNGs to <paramref name="debugDir"/>.
/// </summary>
static async Task SaveDebugImagesAsync(
    string imagePath,
    string debugDir,
    WindowsImagePreprocessor preprocessor,
    WindowsTableDetector tableDetector,
    WindowsOcrService ocrService)
{
    Directory.CreateDirectory(debugDir);

    // 1. Original
    var rawBytes = await File.ReadAllBytesAsync(imagePath);
    DebugImageWriter.SaveRaw(rawBytes, Path.Combine(debugDir, "01_original.png"));

    // 2. Preprocessed
    await using var ms = new MemoryStream(rawBytes);
    var preprocessed = await preprocessor.PreprocessAsync(ms);
    DebugImageWriter.SavePreprocessed(preprocessed, Path.Combine(debugDir, "02_preprocessed.png"));

    // 3. Grid lines overlay
    var (cells, hLinesPng, vLinesPng) = await tableDetector.DetectCellsWithMasksAsync(preprocessed);
    DebugImageWriter.SaveGridLinesOverlay(rawBytes, hLinesPng, vLinesPng,
        Path.Combine(debugDir, "03_gridlines.png"));

    // 4. Cells bounding boxes overlay (colour-coded by role)
    // Heuristic: assume row 0 is header, col 0 is name column
    int headerRow = cells.Count > 0 ? cells.Min(c => c.Row) : 0;
    int nameCol   = cells.Count > 0 ? cells.Min(c => c.Col) : 0;
    DebugImageWriter.SaveCellsOverlay(rawBytes, cells,
        Path.Combine(debugDir, "04_cells.png"),
        headerRow: headerRow,
        nameCol:   nameCol);

    // 5. OCR overlay — run OCR on the original, map elements into cells by spatial overlap
    var ocrElements = await ocrService.RecognizeAsync(rawBytes);

    // Simple spatial assignment: each OCR element's centre → containing cell
    foreach (var el in ocrElements)
    {
        int cx = el.Bounds.X + el.Bounds.Width  / 2;
        int cy = el.Bounds.Y + el.Bounds.Height / 2;
        var best = cells.FirstOrDefault(c =>
            cx >= c.Bounds.X && cx <= c.Bounds.X + c.Bounds.Width &&
            cy >= c.Bounds.Y && cy <= c.Bounds.Y + c.Bounds.Height);
        if (best is not null)
        {
            best.Text = string.IsNullOrWhiteSpace(best.Text)
                ? el.Text
                : best.Text + " " + el.Text;
        }
    }

    DebugImageWriter.SaveOcrOverlay(rawBytes, cells,
        Path.Combine(debugDir, "05_ocr.png"),
        headerRow: headerRow,
        nameCol:   nameCol);
}
