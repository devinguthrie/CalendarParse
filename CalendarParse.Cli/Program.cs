using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using CalendarParse.Parsing.Services;
using CalendarParse.Models;
using CalendarParse.Services;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding  = Encoding.UTF8;

// ── Load .env file from repo root (if present) ────────────────────────────────
LoadDotEnv();

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
bool   fireworksMode = false;
bool   glmOcrMode    = false;
bool   useTesseract   = false;
bool   useEasyOcr     = false;
bool   usePaddleOcr   = false;
string ollamaModel    = OllamaCalendarService.DefaultModel;
string preprocessArg  = string.Empty;
string knownNamesArg  = string.Empty;
string correctionsDbPath = string.Empty;

for (int i = 1; i < args.Length; i++)
{
    if ((args[i] is "--name" or "-n") && i + 1 < args.Length)
        nameFilter = args[++i];
    else if (args[i] is "--test" or "-t")
        testMode = true;
    else if (args[i] is "--vision" or "-V")
        visionMode = true;
    else if (args[i] is "--fireworks")
        fireworksMode = true;
    else if (args[i] is "--glm-ocr")
        glmOcrMode = true;
    else if (args[i] is "--use-tesseract")
        useTesseract = true;
    else if (args[i] is "--use-easyocr")
        useEasyOcr = true;
    else if (args[i] is "--use-paddleocr")
        usePaddleOcr = true;
    else if (args[i] is "--model" && i + 1 < args.Length)
        ollamaModel = args[++i];
    else if (args[i] is "--preprocess" && i + 1 < args.Length)
        preprocessArg = args[++i];
    else if (args[i] is "--known-names" && i + 1 < args.Length)
        knownNamesArg = args[++i];
    else if (args[i] is "--test-from-corrections-db" && i + 1 < args.Length)
        correctionsDbPath = args[++i];
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

// Accept either a folder or a single image file.
bool singleFileMode = false;
if (string.IsNullOrWhiteSpace(correctionsDbPath))
{
    if (File.Exists(folder))
    {
        string ext = Path.GetExtension(folder).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png"))
        {
            Console.Error.WriteLine($"ERROR: File must be .jpg, .jpeg, or .png: {folder}");
            return 1;
        }
        singleFileMode = true;
    }
    else if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"ERROR: Path not found: {folder}");
        return 1;
    }
}

if (!string.IsNullOrWhiteSpace(correctionsDbPath))
{
    if (!File.Exists(correctionsDbPath))
    {
        Console.Error.WriteLine($"ERROR: Corrections DB not found: {correctionsDbPath}");
        return 1;
    }

    string materializedFolder;
    try
    {
        materializedFolder = await BuildBenchmarkFolderFromCorrectionsDbAsync(correctionsDbPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
    folder = materializedFolder;
    testMode = true;
    singleFileMode = false;
    Console.WriteLine($"Benchmark source: ConfirmedCorrections DB ({correctionsDbPath})");
    Console.WriteLine($"Materialized temp benchmark folder: {folder}");
}

// ── Service wiring ────────────────────────────────────────────────────────────
ICalendarParseService parser;
var preprocessorInst = new WindowsImagePreprocessor();

string[] knownNamesArr = string.IsNullOrEmpty(knownNamesArg)
    ? Array.Empty<string>()
    : knownNamesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// Resolve Fireworks backend if --fireworks was passed
FireworksCalendarService? fireworksBackend = null;
if (fireworksMode)
{
    string? apiKey = Environment.GetEnvironmentVariable("FIREWORKS_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.Error.WriteLine("ERROR: --fireworks requires the FIREWORKS_API_KEY environment variable to be set.");
        return 1;
    }
    fireworksBackend = new FireworksCalendarService(
        apiKey:     apiKey,
        model:      ollamaModel,
        knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null);
}

if (visionMode)
{
    parser = fireworksBackend
        ?? (ICalendarParseService)new OllamaCalendarService(model: ollamaModel, knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null);
    string backend = fireworksMode ? $"Fireworks model: {ollamaModel}" : $"Ollama model: {ollamaModel}";
    Console.WriteLine($"Mode: VISION ({backend})");
    if (knownNamesArr.Length > 0)
        Console.WriteLine($"Known names: {string.Join(", ", knownNamesArr)}");
}
else if (glmOcrMode)
{
    string glmModel = string.IsNullOrEmpty(ollamaModel) ? GlmOcrCalendarService.DefaultModel : ollamaModel;
#if WINDOWS
    IOcrService? glmOcrAppSdk = new WindowsAppSdkTextRecognizerService();
#else
    IOcrService? glmOcrAppSdk = null;
#endif
    parser = new GlmOcrCalendarService(model: glmModel, ocrService: glmOcrAppSdk);
    Console.WriteLine($"Mode: GLM-OCR ({glmModel} via Ollama, full-table markdown)");
}
else
{
    IOcrService? ocrOverride;
    if (useTesseract)
    {
        ocrOverride = new TesseractOcrService(Path.Combine(AppContext.BaseDirectory, "tessdata"));
    }
    else if (useEasyOcr)
    {
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "easyocr_ocr.py");
        string pythonExe  = FindVenvPython() ?? "python";
        Console.WriteLine($"EasyOCR: python={pythonExe}");
        Console.WriteLine($"EasyOCR: script={scriptPath}");
        Console.WriteLine("EasyOCR: loading model (first run may take ~30s to download)...");
        ocrOverride = new EasyOcrService(scriptPath, pythonExe);
        Console.WriteLine("EasyOCR: READY");
    }
    else if (usePaddleOcr)
    {
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "paddleocr_ocr.py");
        string pythonExe  = FindVenvPython() ?? "python";
        Console.WriteLine($"PaddleOCR: python={pythonExe}");
        Console.WriteLine($"PaddleOCR: script={scriptPath}");
        Console.WriteLine("PaddleOCR: loading model (first run may take ~30s to download)...");
        ocrOverride = new PaddleOcrService(scriptPath, pythonExe);
        Console.WriteLine("PaddleOCR: READY");
    }
    else
    {
        ocrOverride = null;
    }
    parser = new HybridCalendarService(
        model:      ollamaModel,
        knownNames: knownNamesArr.Length > 0 ? knownNamesArr : null,
        llmBackend: fireworksBackend,
        ocrService: ocrOverride);
    string ocrLabel = useTesseract ? "Tesseract" : useEasyOcr ? "EasyOCR" : usePaddleOcr ? "PaddleOCR" : "WinRT OCR";
    string backend = fireworksMode ? $"Fireworks model: {ollamaModel}" : $"Ollama model: {ollamaModel}";
    Console.WriteLine($"Mode: HYBRID ({backend} + {ocrLabel} + grid crop)");
    if (knownNamesArr.Length > 0)
        Console.WriteLine($"Known names: {string.Join(", ", knownNamesArr)}");
}
HybridCalendarService? hybridParser = parser as HybridCalendarService;

// ── Batch processing ──────────────────────────────────────────────────────────
List<string> imageFiles;
if (singleFileMode)
{
    imageFiles = [Path.GetFullPath(folder)];
    folder     = Path.GetDirectoryName(imageFiles[0])!;
    Console.WriteLine($"Single image: {Path.GetFileName(imageFiles[0])}");
}
else
{
    imageFiles = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
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
}
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

        // Write positions sidecar (enables overlay rendering, now and in future runs)
        if (hybridParser?.LastRunPositions is { } positions)
        {
            string posPath = Path.Combine(folder, name + ".positions.json");
            await File.WriteAllTextAsync(posPath,
                JsonSerializer.Serialize(positions, new JsonSerializerOptions { WriteIndented = true }));

        }


        // Count employees for the summary
        int empCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(jsonOnly);
            if (doc.RootElement.TryGetProperty("Employees", out var emps))
                empCount = emps.GetArrayLength();
        }
        catch { /* ignore parse errors in summary */ }

        Console.WriteLine($"OK  ({empCount} employees -> {Path.GetFileName(outPath)})  [{FmtMmSs(imageTimer.Elapsed)}]");

        // ── Step profiling table (--test mode + hybrid pipeline only) ────────
        if (testMode && hybridParser?.LastRunSteps is { Count: > 0 } runSteps)
        {
            string stepAnsPath = Path.Combine(folder, name + ".answer.json");
            if (File.Exists(stepAnsPath))
            {
                try
                {
                    string answerText = await File.ReadAllTextAsync(stepAnsPath);
                    Console.WriteLine($"    {"Step",-26} {"step",6}  {"total",6}  {"delta",6}  {"score",-16}");
                    Console.WriteLine($"    {new string('-', 26)} {new string('-', 6)}  {new string('-', 6)}  {new string('-', 6)}  {new string('-', 16)}");
                    int prevMatched = 0;
                    foreach (var snap in runSteps)
                    {
                        CompareCalendarData(snap.JsonSnapshot, answerText,
                            out int expected, out int matched, out _);
                        int delta    = matched - prevMatched;
                        prevMatched  = matched;
                        double pct   = expected > 0 ? 100.0 * matched / expected : 0;
                        string stepT  = $"+{snap.StepElapsed.TotalSeconds:F0}s";
                        string totT   = FmtMmSs(snap.TotalElapsed);
                        string deltaS = delta == 0 ? "0" : delta > 0 ? $"+{delta}" : $"{delta}";
                        Console.WriteLine(
                            $"    {snap.StepName,-26} {stepT,6}  {totT,6}  {deltaS,6}  {matched}/{expected} ({pct:F1}%)");
                        if (snap.StepName == "per-day strip LLM" && hybridParser.LastRunDayBreakdown is { Count: > 0 } dayBD)
                        {
                            string dayLine = string.Join("  ", dayBD.Select(d =>
                                $"{d.DayName}:{(d.CellsAdded > 0 ? "+" : "")}{d.CellsAdded}"));
                            Console.WriteLine($"    {"",-26}  {dayLine}");
                        }
                    }
                    if (hybridParser.LastRunLlmCalls > 0)
                    {
                        int tc      = hybridParser.LastRunTotalCells;
                        double oPct = tc > 0 ? 100.0 * hybridParser.LastRunOcrFilled / tc : 0;
                        string retS = hybridParser.LastRunRetries > 0 ? $" | {hybridParser.LastRunRetries} {(hybridParser.LastRunRetries == 1 ? "retry" : "retries")}" : "";
                        string holS = hybridParser.LastRunHolidayFires > 0 ? $" | {hybridParser.LastRunHolidayFires} holiday blank" : "";
                        Console.WriteLine($"    stats: {hybridParser.LastRunLlmCalls} LLM calls | OCR fill {hybridParser.LastRunOcrFilled}/{tc} ({oPct:F0}%){retS}{holS}");
                    }
                    Console.WriteLine();
                }
                catch { /* don't let profiling table crash the run */ }
            }
        }

        results.Add((Path.GetFileName(imagePath), true, $"{empCount} employees  {FmtMmSs(imageTimer.Elapsed)}"));
    }
    catch (Exception ex)
    {
        imageTimer.Stop();
        Console.WriteLine($"FAIL  {ex.Message}  [{FmtMmSs(imageTimer.Elapsed)}]");
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
/// <summary>Formats a TimeSpan as M:SS (e.g. 1:05, 0:42).</summary>
static string FmtMmSs(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
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
    Console.WriteLine("  --overlay <name>      After parsing, write an overlay image showing guessed");
    Console.WriteLine("                        shift values at each cell's position for the named employee.");
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
    Console.WriteLine("  --test-from-corrections-db <path>");
    Console.WriteLine("                        Build a temporary benchmark set from ConfirmedCorrections in jobs.db,");
    Console.WriteLine("                        then run --test against it automatically.");
    Console.WriteLine();
    Console.WriteLine("Output:");
    Console.WriteLine("  For each image.jpg, writes image.output.json and image.debug.txt in the same folder.");
    Console.WriteLine("  In --test mode, also reports accuracy against image.answer.json.");
    Console.WriteLine();
    Console.WriteLine("Requires:");
    Console.WriteLine("  Ollama running locally (https://ollama.com) with the model pulled.");
    Console.WriteLine("  WinRT OCR available on Windows 10+ (no extra installation needed).");
}

/// <summary>
/// Walks up from the executable's directory looking for a .venv created by the
/// VS Code Python extension (Windows path: .venv\Scripts\python.exe).
/// Returns the full path if found, or null if the venv is not present.
/// </summary>
static string? FindVenvPython()
{
    string? dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        var candidate = Path.Combine(dir, ".venv", "Scripts", "python.exe");
        if (File.Exists(candidate))
            return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static void LoadDotEnv()
{
    // Walk up from the executable's location to find a .env file (handles both
    // `dotnet run` from the repo root and a published binary in a sub-folder).
    string? dir = AppContext.BaseDirectory;
    while (dir is not null)
    {
        string candidate = Path.Combine(dir, ".env");
        if (File.Exists(candidate))
        {
            foreach (string line in File.ReadAllLines(candidate))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key   = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                // Only set if not already present — real env vars take priority.
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    Environment.SetEnvironmentVariable(key, value);
            }
            return;
        }
        dir = Path.GetDirectoryName(dir);
    }
}

static async Task<string> BuildBenchmarkFolderFromCorrectionsDbAsync(string dbPath)
{
    string tempDir = Path.Combine(
        Path.GetTempPath(),
        $"calendarparse-live-benchmark-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    await using var conn = new SqliteConnection($"Data Source={dbPath}");
    await conn.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
SELECT Id, ImagePath, ShiftsJson
FROM ConfirmedCorrections
ORDER BY Id";

    SqliteDataReader reader;
    try
    {
        reader = await cmd.ExecuteReaderAsync();
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "ConfirmedCorrections table not found. Start CalendarParse.Api once to initialize schema, then submit and confirm at least one run.");
    }

    await using (reader)
    {

        int exported = 0;
        while (await reader.ReadAsync())
        {
            int id = reader.GetInt32(0);
            string? imagePath = reader.IsDBNull(1) ? null : reader.GetString(1);
            string shiftsJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                continue;

            var answer = BuildAnswerFromConfirmedShiftsJson(shiftsJson);
            if (answer is null || answer.Employees.Count == 0)
                continue;

            string stem = $"CORR ({id})";
            string ext = Path.GetExtension(imagePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

            File.Copy(imagePath, Path.Combine(tempDir, stem + ext), overwrite: true);

            await File.WriteAllTextAsync(
                Path.Combine(tempDir, stem + ".answer.json"),
                JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true }));

            exported++;
        }

        if (exported == 0)
            throw new InvalidOperationException(
                "No usable corrections found in ConfirmedCorrections (missing images or empty shifts).");
    }

    return tempDir;
}

static BenchmarkAnswer? BuildAnswerFromConfirmedShiftsJson(string shiftsJson)
{
    List<ShiftData>? shifts;
    try
    {
        shifts = JsonSerializer.Deserialize<List<ShiftData>>(shiftsJson);
    }
    catch
    {
        return null;
    }

    if (shifts is null || shifts.Count == 0)
        return null;

    var validShifts = shifts
        .Where(s => !string.IsNullOrWhiteSpace(s.Employee) && !string.IsNullOrWhiteSpace(s.Date))
        .ToList();
    if (validShifts.Count == 0)
        return null;

    var employees = validShifts
        .GroupBy(s => s.Employee.Trim(), StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new BenchmarkEmployee
        {
            Name = g.Key,
            Shifts = g
                .Select(s => new BenchmarkShift
                {
                    Date = NormalizeDate(s.Date ?? string.Empty),
                    Shift = s.TimeRange?.Trim() ?? string.Empty,
                })
                .OrderBy(s => s.Date, StringComparer.Ordinal)
                .ToList()
        })
        .Where(e => e.Shifts.Count > 0)
        .ToList();

    string month = string.Empty;
    int year = 0;
    DateTime? firstDate = validShifts
        .Select(s => NormalizeDate(s.Date ?? string.Empty))
        .Select(d => DateTime.TryParse(d, out var dt) ? dt : (DateTime?)null)
        .Where(dt => dt.HasValue)
        .OrderBy(dt => dt!.Value)
        .FirstOrDefault();

    if (firstDate.HasValue)
    {
        month = firstDate.Value.ToString("MMMM");
        year = firstDate.Value.Year;
    }

    return new BenchmarkAnswer
    {
        Month = month,
        Year = year,
        Employees = employees,
    };
}

sealed class BenchmarkAnswer
{
    public string Month { get; set; } = string.Empty;
    public int Year { get; set; }
    public List<BenchmarkEmployee> Employees { get; set; } = [];
}

sealed class BenchmarkEmployee
{
    public string Name { get; set; } = string.Empty;
    public List<BenchmarkShift> Shifts { get; set; } = [];
}

sealed class BenchmarkShift
{
    public string Date { get; set; } = string.Empty;
    public string Shift { get; set; } = string.Empty;
}
