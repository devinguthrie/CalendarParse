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
string ollamaModel    = OllamaCalendarService.DefaultModel;
string preprocessArg  = string.Empty;
string knownNamesArg  = string.Empty;

for (int i = 1; i < args.Length; i++)
{
    if ((args[i] is "--name" or "-n") && i + 1 < args.Length)
        nameFilter = args[++i];
    else if (args[i] is "--test" or "-t")
        testMode = true;
    else if (args[i] is "--vision" or "-V")
        visionMode = true;
    else if (args[i] is "--model" && i + 1 < args.Length)
        ollamaModel = args[++i];
    else if (args[i] is "--preprocess" && i + 1 < args.Length)
        preprocessArg = args[++i];
    else if (args[i] is "--known-names" && i + 1 < args.Length)
        knownNamesArg = args[++i];
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

string[] knownNamesArr = string.IsNullOrEmpty(knownNamesArg)
    ? Array.Empty<string>()
    : knownNamesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (visionMode)
{
    parser = new OllamaCalendarService(model: ollamaModel, knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null);
    Console.WriteLine($"Mode: VISION (Ollama model: {ollamaModel})");
    if (knownNamesArr.Length > 0)
        Console.WriteLine($"Known names: {string.Join(", ", knownNamesArr)}");
}
else
{
    parser = new HybridCalendarService(
        model:      ollamaModel,
        knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null);
    Console.WriteLine($"Mode: HYBRID (Ollama model: {ollamaModel} + WinRT OCR + grid crop)");
    if (knownNamesArr.Length > 0)
        Console.WriteLine($"Known names: {string.Join(", ", knownNamesArr)}");
}
HybridCalendarService? hybridParser = parser as HybridCalendarService;

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
    Console.WriteLine("Mode: TEST (writing .guess.json, comparing against .answer.json)");
Console.WriteLine();

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

        byte[] processedBytes = preprocessorInst.PreprocessBytes(rawBytes, preprocessMode);

        // Write the preprocessed image for inspection when a non-default mode is active.
        string debugImgDir = Path.Combine(folder, "preprocess-debug");
        if (preprocessMode != PreprocessMode.None)
        {
            Directory.CreateDirectory(debugImgDir);
            string debugExt       = WindowsImagePreprocessor.GetExtension(preprocessMode);
            string debugImagePath = Path.Combine(debugImgDir, $"{name}_{preprocessMode.ToString().ToLower()}{debugExt}");
            await File.WriteAllBytesAsync(debugImagePath, processedBytes);
        }

        string output = await parser.ProcessAsync(new MemoryStream(processedBytes), nameFilter);

        imageTimer.Stop();

        // Extract just the JSON portion (after the debug report)
        string jsonOnly = ExtractJson(output);

        // Feed detected names into the session pool so later images can resolve OCR fragments
        if (hybridParser != null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(jsonOnly);
                if (doc.RootElement.TryGetProperty("Employees", out var emps))
                {
                    var sessionNamesFromImage = emps.EnumerateArray()
                        .Select(e => e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "")
                        .Where(n => n.Length >= 2)
                        .ToList();
                    hybridParser.AddSessionNames(sessionNamesFromImage);
                }
            }
            catch { /* ignore JSON parse errors */ }
        }

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

        Console.WriteLine($"OK  ({empCount} employees -> {Path.GetFileName(outPath)})  [{imageTimer.Elapsed.TotalSeconds:F1}s]");
        results.Add((Path.GetFileName(imagePath), true, $"{empCount} employees  {imageTimer.Elapsed.TotalSeconds:F1}s"));
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
Console.WriteLine("-- Summary -----------------------------------------------------------");
int passed = results.Count(r => r.ok);
int failed = results.Count - passed;
foreach (var (file, ok, detail) in results)
    Console.WriteLine($"  {(ok ? "v" : "x")} {file,-40} {detail}");
Console.WriteLine();
Console.WriteLine($"  {passed} succeeded, {failed} failed");

// ── Test comparison (only in --test mode) ─────────────────────────────────────
if (testMode)
{
    Console.WriteLine();
    Console.WriteLine("-- Test Results ---------------------------------------------------------");

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
        Console.WriteLine($"  {(perfect ? "v" : "x")} {name + ".jpg",-40} {matched}/{expected} shifts correct");
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
        Console.WriteLine("-- Per-Employee Score ---------------------------------------------------");

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
            if (isFranny) row += "  <";
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

/// <summary>Zero-pads month and day in ISO date strings so 2025-11-1 == 2025-11-01.</summary>
static string NormalizeDate(string d)
{
    var m = System.Text.RegularExpressions.Regex.Match(d, @"^(\d{4})-(\d{1,2})-(\d{1,2})$");
    if (!m.Success) return d;
    return $"{int.Parse(m.Groups[1].Value):D4}-{int.Parse(m.Groups[2].Value):D2}-{int.Parse(m.Groups[3].Value):D2}";
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
    Console.WriteLine("Modes (default: hybrid):");
    Console.WriteLine("  (no flag)             HYBRID: WinRT OCR column detection + per-day LLM crop (90.8% accuracy)");
    Console.WriteLine("  -V, --vision          VISION: pure Ollama vision model, 5-pass multi-step (78.0% accuracy)");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -n, --name <filter>   Filter results to a specific employee name");
    Console.WriteLine("  -t, --test            Test mode: write .output.json and compare against .answer.json");
    Console.WriteLine("  --model <name>        Ollama model to use (default: qwen2.5vl:7b)");
    Console.WriteLine("  --preprocess <mode>   Image preprocessing before the vision model:");
    Console.WriteLine("                          none     Raw image bytes (default)");
    Console.WriteLine("                          current  Grayscale→blur→adaptive-threshold→dilate");
    Console.WriteLine("                          clahe    Grayscale→EqualizeHist (global histogram equalisation)");
    Console.WriteLine("                          llm      Colour unsharp-mask sharpen (preserves RGB signal)");
    Console.WriteLine("                          denoise  Grayscale→fast-denoise→EqualizeHist (for noisy scans)");
    Console.WriteLine("                        Preprocessed image written to preprocess-debug/ when mode != none.");
    Console.WriteLine("  --known-names <csv>   Comma-separated list of expected employee names.");
    Console.WriteLine("                        Normalises OCR phantoms and improves name-extraction accuracy.");
    Console.WriteLine();
    Console.WriteLine("Output:");
    Console.WriteLine("  For each image.jpg, writes image.output.json and image.debug.txt in the same folder.");
    Console.WriteLine("  In --test mode, also reports accuracy against image.answer.json.");
    Console.WriteLine();
    Console.WriteLine("Requires:");
    Console.WriteLine("  Ollama running locally (https://ollama.com) with the model pulled.");
    Console.WriteLine("  WinRT OCR available on Windows 10+ (no extra installation needed).");
}
