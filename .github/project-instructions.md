# CalendarParse – Copilot Instructions

## Project Overview

**CalendarParse** is a .NET MAUI app (Android primary; iOS/macCatalyst/Windows also compile) that photographs work schedule tables, detects the table grid, runs OCR and extracts per-employee shift data as JSON.

Originally this project used OpenCV for table grid detection and Google ML Kit for on-device OCR on Android, but the current implementation abstracts these details and focuses on extracting per-employee shift data as JSON. This was due to the difficulty in using on-device models.

## Build & Run

```bash
# Build for a specific target (Android is primary)
dotnet build -f net10.0-android
dotnet build -f net10.0-windows10.0.19041.0
dotnet build -f net10.0-ios
dotnet build -f net10.0-maccatalyst

# Run on Android (device/emulator must be connected)
dotnet run -f net10.0-android
```

> Windows builds use `WindowsPackageType=None` (unpackaged).  
> `XA0141` warnings from ML Kit about 16 KB page sizes are a third-party NuGet issue — ignore them.

## Architecture

### Service Layer (`Services/`)
| File | Purpose |
|------|---------|
| `ICalendarParseService` / `CalendarParseService` | Orchestrates the full pipeline |
| `ImagePreprocessor` | OpenCV preprocessing (Android-only via `#if ANDROID`) |
| `TableDetector` | OpenCV grid detection → `TableCell[row, col]` matrix (Android-only) |
| `OcrService` | ML Kit Text Recognition wrapper (Android-only) |
| `CalendarStructureAnalyzer` | Pure C# — date/employee inference from OCR'd cells |
| `AndroidTaskExtensions` | `AsTaskAsync<T>()` bridge for Java `Android.Gms.Tasks.Task` → .NET `Task<T>` |
| `IServices.cs` | Interface definitions for all services + `OcrElement` DTO |

### Data Models (`Models/`)
- `CalendarData` — root JSON object (month, year, employees list)
- `EmployeeSchedule` — employee name + `List<ShiftEntry>`
- `ShiftEntry` — ISO-8601 date string + raw shift text (e.g. "8am-4pm")
- `TableCell` — internal grid cell: row/col index, bounding `Rect`, OCR text, confidence
- `Rect` — platform-agnostic bounding rectangle (avoids Android/MAUI Graphics namespace collisions)

### DI Registration (MauiProgram.cs)
All services and `MainPage` are registered as `AddSingleton`. `App.CreateWindow()` resolves `MainPage` directly from `IPlatformApplication.Current.Services` — AppShell is not used for routing.

## Documentation Policy

Two Markdown files (plus this file) track accuracy experiments for the `CalendarParse.Cli` Ollama vision pipeline:

| File | Purpose |
|------|---------|
| `.github/experiment-log.md` | Chronological record of every phase, score, and root cause |
| `.github/testing-strategy.md` | Current error breakdown and ranked hypotheses |
| `.github/copilot-instructions.md` | Pipeline architecture, model inventory, and Ollama settings |

**Mandatory update triggers** — update the relevant files whenever:
- A new Ollama model is switched to or benchmarked (add a new Phase entry, update summary table, update model inventory in `copilot-instructions.md`)
- A significant architecture change is made to `OllamaCalendarService.cs` (new pass, changed voting logic, new prompt strategy)
- A new average score baseline is established (3+ run average)
- The answer JSON ground truth is corrected
- An approach is confirmed as an anti-pattern (add to the Anti-Patterns table)

**What to update in each file:**
- `.github/experiment-log.md`: Add a new `## Phase N` section with model, architecture, scores, and outcome. Update the Summary Table row. Update Pending Experiments.
- `.github/testing-strategy.md`: Update the Current Best/Avg lines and error breakdown table. Update ranked hypotheses — remove any that are resolved or confirmed tested. **Do NOT add a "Suggested Next Experiments" section** — all pending experiments are tracked exclusively in `.github/experiment-log.md` under `## Pending Experiments`.
- `.github/copilot-instructions.md`: Update the active model name, pipeline pass list, and any changed Ollama options.

## Repository Structure (relevant to CLI)

```
CalendarParse.Cli/
  Program.cs                      Entry point, scoring/comparison logic
  Services/
    OllamaCalendarService.cs      Vision pipeline (4-pass extraction + majority vote)
    WindowsImagePreprocessor.cs   OpenCV preprocessing (Windows, not used in Ollama path)
    WindowsOcrService.cs          WinRT OCR wrapper (not used in Ollama path)
    WindowsTableDetector.cs       OpenCV grid detection (not used in Ollama path)
    WindowsWinRtOcrService.cs     WinRT OCR alternative (not used in Ollama path)
    DebugImageWriter.cs           Saves debug images during processing

CalendarParse/
  calander-parse-test-imgs/
    IM (1).jpg                    Primary test image
    IM (1).answer.json            Ground truth (11 employees, 77 shifts)
    IM (1).output.json            Last run's extracted output
    IM (1).guess.json             Comparison run output (in --test mode)
    IM (1)-whole-table.csv        Raw CSV dump of the full table
    IM (1).debug.txt              Debug log from last run
    IM (1).debug-imgs/            Debug visualizations
```

---

## How to Run

```powershell
# Run against test images, compare to .answer.json, print score
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test

# Use a specific model
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b

# Filter to a single employee name
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b --name "Andee"
```

---

## Current Pipeline Architecture (OllamaCalendarService.cs)

### Pass 1 — Header Extraction (`ExtractHeaderAsync`)
- **Input**: base64-encoded image
- **Output**: `(string month, int year, List<string> isoDates[7])`
- **`num_predict`**: 300
- **Prompt strategy**: Ask for a JSON object with `Month`, `Year`, `Dates[7]` in ISO-8601 format
- **Post-processing**: Normalizes `M/D/YY` → `YYYY-MM-DD`, zero-pads single-digit month/day

### Pass 2 — Name Extraction (`ExtractNamesAsync`)
- **Input**: base64-encoded image
- **Output**: `List<string>` of employee names
- **`num_predict`**: 200
- **Prompt strategy**: Ask for names one-per-line from the leftmost column of the main table only; stop before secondary table; "Spell each name EXACTLY"
- **Known issue**: Occasionally reads "Seena" as "Siana" or similar — mitigated by Levenshtein ≤ 2 fuzzy name matching in the scorer (`Program.cs`)

### Pass 3 — Full Table Extraction (`ExtractAllShiftsAsync`) × 3, then majority vote
- **Input**: base64-encoded image + list of employee names
- **Output**: `Dictionary<string, List<object>>` — employee name → 7 `{Date, Shift}` entries
- **`num_predict`**: dynamic — `max(3000, count × 7 × 30 + 800)`
- **Response format**: Named-key JSON objects per employee:
  ```json
  { "Andee": {"Sun":"RTO","Mon":"RTO","Tue":"PTO","Wed":"PTO","Thu":"10:00-6:30","Fri":"11:00-7:30","Sat":"9:00-5:30"} }
  ```
- **Why named keys**: Positional arrays caused systematic off-by-one column shifts; named keys anchor each value to its day
- **Majority voting**: 3 independent calls, cell-by-cell most-frequent value wins; ties broken in favor of non-empty

### Smart Re-query (Post-Vote)
- After majority vote, any employee with ≥4 blank cells is re-queried using `ExtractAllShiftsAsync([just those names])`
- Rationale: ≥4 blanks typically indicates a column-shift failure across all 3 runs; a focused call is more likely to recover the correct values than the CSV fallback
- Guard: employees with ≥4 blanks in the majority result are excluded from the x-marks overlay (their blanks are likely unread time ranges, not genuine day-off marks)

### Pass 4 — X-Marks Binary Pass (`ExtractXMarksAsync`)
- **Input**: base64-encoded image
- **Output**: `Dictionary<string, HashSet<string>>` — employee name → set of day names that have an X mark
- **`num_predict`**: dynamic (similar scale to Pass 3)
- **Prompt strategy**: Ask only "which cells have an X mark?" — binary yes/no per cell; red ink hint: "X marks may be printed in RED ink or any other color — treat ANY X or checkmark as \"x\""
- **Post-processing**: Applied as overlay on the majority-vote result — promotes blank→`x` only; never overwrites a real extracted time/label value
- **Guard**: Skips employees with ≥4 blanks in the main extraction result (their blanks are likely unread time ranges, not genuine X marks)

### Fallback — Per-employee Row Extraction (`ExtractRowAsync`)
- **Input**: base64-encoded image + single employee name
- **Output**: `List<object>` — 7 `{Date, Shift}` entries as CSV
- **`num_predict`**: 150
- **Prompt**: Ask for exactly 7 comma-separated values; provides one example line
- **Used for**: employees missing from ALL 3 `ExtractAllShiftsAsync` calls (complete extraction failure only)

---

## Models Installed Locally

| Model | Size | Vision | Notes |
|-------|------|--------|-------|
| `qwen2.5vl:7b` | 6.0 GB | ✅ | **Current best model** — 76.6% avg accuracy |
| `llava:13b` | 8.0 GB | ✅ | Fails entirely on multi-employee tables — 0/77 |
| `minicpm-v:latest` | 5.5 GB | ✅ | Wrong week + all-x — 0/77, not viable |
| `granite3.2-vision:2b` | 2.4 GB | ✅ | Header extraction fails + all-x — 0/77, not viable |
| `gemma3:4b` | 3.3 GB | ✅ | Wrong year + phantom names + all-x — 0/77, not viable |
| `llama3.2-vision:11b` | 7.8 GB | ✅ | Previous default — best score 23.4% |

---

## Ground Truth

**File**: `CalendarParse\calander-parse-test-imgs\IM (1).answer.json`

- **Week**: Sunday Oct 26 – Saturday Nov 1, 2025
- **Employees** (11 total): Andee, Brittney, Cyndee, Sarah, Franny, Jenny, Victor, Halle, Kyleigh, Seena, Ciara
- **Total shifts**: 77 cells (empty string `""` counts as a valid shift value)
- **Shift value types**: `HH:MM-H:MM` time ranges, `RTO`, `PTO`, `x` (day off), `""` (blank/no shift)

**Important note**: There is a secondary summary table below the main schedule table in the image. Everything below "Ciara" (the last row of the main table) should be ignored.

---

## Scoring Logic (`Program.cs` — `CompareCalendarData`)

- Employee name match: **case-insensitive**; fuzzy fallback via Levenshtein ≤ 2 when exact match fails
- Date match: both sides normalized through `NormalizeDate()` (zero-pads `YYYY-M-D` → `YYYY-MM-DD`)
- Shift match: `ShiftsMatch()` helper — `""` (blank) and `"x"` are treated as equivalent "not working" values; all other values require an exact case-sensitive match
- Missing employees: counted as 7 wrong shifts
- Missing dates within a found employee: counted as 1 wrong shift per missing date

---

## Ollama API Configuration

- Base URL: `http://localhost:11434`
- `num_ctx`: 16384 (model context window)
- `temperature`: 0.1
- `keep_alive`: -1 (keep model loaded between calls)
- HTTP timeout: 600 seconds
- Model warmup: sends a cheap text-only prompt before the first image call to pre-load into VRAM

---

## Key Conventions

- **Nullable reference types** are enabled. All new code must be null-safe.
- **Implicit usings** are enabled — `System`, LINQ, etc. are always available.
- **`#if ANDROID` guards** isolate all Emgu.CV and ML Kit code. Non-Android builds compile but return empty/stub results.
- **`Rect` naming conflict**: `CalendarParse.Models.Rect` conflicts with both `Microsoft.Maui.Graphics.Rect` and `Android.Graphics.Rect`. Any file that uses `Models.Rect` must add a using alias: `using Rect = CalendarParse.Models.Rect;` (or `using ModelRect = ...`).
- **`CancellationToken` naming conflict** in Android files: `Android.Gms.Tasks` also defines `CancellationToken`. Always use the fully qualified `System.Threading.CancellationToken` in files that import `Android.Gms.Tasks`.
- **ML Kit binding types**: `Text.TextBlocks` returns `IList<Text.TextBlock>` (nested type). `TextBlock`, `TextLine`, `TextElement` cannot be used as standalone type names — use `var` in foreach loops.
- **Colors and brushes** are defined in `Resources/Styles/Colors.xaml`. Do not hardcode color values in pages.
- **Styles** for typography are in `Resources/Styles/Styles.xaml`.
- **Android colors** that need to match must also be updated in `Platforms/Android/Resources/values/colors.xml`.
- **NuGet packages**: Emgu.CV and ML Kit packages are scoped to `net10.0-android` only via a conditional `<ItemGroup>` in the csproj.

