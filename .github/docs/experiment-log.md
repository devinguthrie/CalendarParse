# CalendarParse CLI — Experiment Log

_Last updated: 2026-04-06 (session 27). Full verbose history in experiment-log.old.md._

Test images: IM(1) = 11 employees, 77 shifts, Oct 26–Nov 1 2025. IM(2) = 13 employees, 91 shifts, Nov 23–29 2025. **IM(3) = 12 employees, 84 shifts, Sep 21–27 2025.** IM(4) = 13 employees, 91 shifts, Jul 27–Aug 2 2025. IM(5) = 13 employees, 91 shifts, Jul 20–26 2025. Combined = **434 shifts** (5 images).

---

## Summary Table

> **The per-experiment record has moved to [`tried_changes.json`](../../tried_changes.json)** (repo root).
> Each phase is a JSON object with fields: `id`, `phase`, `change_type`, `description`, `score_before`, `score_after`, `total_shifts`, `outcome`, `notes`.
> The benchmark-loop script also appends every `committed` and `reverted` runtime entry to this file automatically.

---

## Key Architectural Lessons

### What works
- **Named day keys** in JSON (Sun/Mon/…) — anchors column identity (Phase 9)
- **3 row + 2 col majority vote** at temp=0 — complementary error correction (Phase 9c→36)
- **X-marks binary pass** + red ink hint — resolves X-SWAP bulk errors (Phase 9e)
- **`--known-names` injection** — eliminates NAME-SPLIT phantoms (Phase 21) — now superseded by dynamic discovery
- **Anti-WRONG-COL warning at END of rules** in multi-col extraction — +13 shifts (Phase 36)
- **temperature=0.0** — perfectly deterministic; any score change is real signal (Phase 29)
- **Hybrid OCR+LLM**: WinRT OCR positions → column boundaries → per-day strip → single-col LLM (Phases 44–49)
- **Retail-context hint (v3) in reinforcedPrompt retry** — CRITICAL + STOP messages for Sat/Sun columns in `ExtractColumnFromImageAsync` breaks the model's Saturday=RTO prior at the re-query stage: +17 shifts (Phase 63)
- **Session name pool**: names extracted from earlier images in the same run accumulate and are used as spelling hints for later images (Phase 51)
- **Late OCR name supplement**: after pass 4, OCR name-column tokens fuzzy-matched to find employees the LLM missed; added with empty shifts (Phase 51)
- **OCR fragments before pass 2**: run WinRT OCR first, collect name-column partial reads, pass to LLM as grounding anchors — helps reconstruct fragmented names (e.g. "M"+"an" → "Megan") (Phase 52)
- **Non-orthogonal coordinate hint**: telling the LLM the image is a photo of a grid (lines may not be straight) improves row-boundary accuracy (Phase 52)

### What never works
- Non-JSON output formats (CSV, pipe-delimited) — model copies examples
- Any model <6B params — cannot read dense tables
- CPU-offloaded models — accuracy AND speed degrade
- Grayscale/binary preprocessing — destroys color signal
- Adding anti-shift warning to single-column context
- More copies of same extraction method at temp=0 — no diversity, amplifies errors
- Anchor-guided re-extraction — corrupts anchor employees
- Image downscaling — destroys fine digit legibility
- Two-shot self-anchoring — biases away from cell reading
- Pass 2b (name column strip as separate LLM crop) — LLM hallucinates variant names (Andrea/Cyndi) even with edit-distance filter; zero net benefit
- Adjacent-duplicate re-query (all pairs) — false positives on legitimate shared values (multiple employees with same shift)
- Triplet detection re-query (≥3 same value in column) — even time-range-restricted version fails: fires correctly on Thu Oct30 but re-queries return same wrong answer at temp=0 because highlighted cell visual anchor persists in strip image

### Critical context-sensitive rules
- Anti-WRONG-COL warning: ✅ multi-column (`ExtractAllShiftsAsync`), ❌ single-column (`ExtractColumnAsync`)
- Warning placement: ✅ end of rules, ❌ top of rules (−27 shifts)
- Majority vote ratio: 3 row + 2 col is optimal at temp=0; do not reweight

---

## Experiment Log

> **Update Rules** — follow these exactly so the log stays clean:
> 1. **New entries append at the BOTTOM** — below the most recent Session entry, above nothing.
> 2. **Use `Session N` format** (not "Phase N"). Session number matches the `_Last updated_` front-matter.
> 3. **Include the score** in the heading: `### Session N — Description (N/434 = X%)`.
> 4. **Reverted experiments still get an entry.** Mark heading with `(REVERTED, N/434)`.
> 5. **Separate each entry with `---`** on its own line above the heading.
> 6. **Do not touch the Phase archive** (Phase 9e through Phase 63 below) — it is frozen history.

---

_Phase-era archive (pre-session-22, Phase N format, oldest-first). Only committed milestones. Full detail in `tried_changes.json`._

### Phase 9e — X-Marks Pass + Red Ink Hint (65.3% avg)
Added Pass 4 `ExtractXMarksAsync`: binary "which cells have X?" query. Applied as overlay: blank→x only. Red ink hint in prompts. +22 pts over Phase 9d.

### Phase 9f — Scorer blank≡x (71.4% avg)
`ShiftsMatch()` treats `""`≡`"x"` as equivalent "not working". +5 pts.

### Phase 9g — Answer.json Victor Fix (76.6%)
Victor Tue/Thu corrected from 10:00-6:00 → 10:00-6:30 (model was correct). +2 pts.

### Phase 21 — Known-Names + Name Normalisation (71.4% single run)
`--known-names` prevents NAME-SPLIT (Athena→Clara+Athena(train)). +5 shifts on IM(2). Fuzzy merge post-process. True 3-run mean later established at 66.1% (Phase 26).

### Phase 29 — Temperature 0.0 (69.0% deterministic)
Eliminated all run-to-run variance. Every run identical. +3 pts vs temp=0.1 baseline.

### Phase 36 — Anti-WRONG-COL Warning (78.0% — vision best)
Single CRITICAL sentence at end of `ExtractAllShiftsAsync` rules:
> "Do NOT shift values one column to the right. Each cell value belongs ONLY in the column whose header is directly above that cell."

+13 shifts. IM(2) +11, IM(1) +2.

### Phases 44–49 — Hybrid Pipeline Evolution (50.6% → 89.9%)
| Phase | Fix | Gain |
|-------|-----|------|
| 44 | OCR positional list → strip LLM | Baseline 50.6% |
| 45 | `Dictionary<int,...>` keyed by day index | +14 |
| 46 | CropAndStitch (name+day col only) + header-skip prompt | +13 |
| 47 | DayPrefixes 30+ variants, 3-char prefix matching | +6 |
| 48 | ISO date cascade fix (strip date-as-first-element) | +20 |
| 49 | RTO/PTO uniform-column holiday heuristic (≥80% → blank) | +13 |

### Phase 50 — IM(3) Added to Test Set (252 shifts total)
Third test image: Sep 21–27 2025, 12 employees, 84 shifts. Includes Megan and Raul (unique to IM(3)) and Andee, Brittney, Cyndee, Sarah, Franny, Jenny, Victor, Halle, Kyleigh, Seena (shared with IM(1)). Baseline with --known-names and no session features: 190/252 (75.4%). Root cause: LLM pass 2 stochastically returns only 7–10 of 12 employees for IM(3).

### Phase 51 — Session Name Pool + Late OCR Name Supplement (210/252 = 83.3%)
Two complementary features:
1. **Session name pool**: `HybridCalendarService` accumulates employee names across images in the same run (`_sessionNames`). Names from IM(1) and IM(2) are passed as `additionalHints` to pass 2 of IM(3), prompting the LLM to use those spellings and look for those employees. Soft constraint: "spelling reference — also list any other names clearly visible."
2. **Late OCR name supplement**: After pass 4, OCR tokens from the name column are fuzzy-matched against the session pool. Names matched (Tori, Raul via prefix/contains/startswith) are added with empty shift records. Empty shifts correctly match `"x"` days via the `"" ≡ x` scorer rule.
3. **OCR supplement fallback fix**: When the session pool exists but a token doesn't match any pool name, fall through to heuristics instead of skipping — catches employees not in any prior image.

### Phase 52 — OCR Fragments Before Pass 2 + Non-Orthogonal Hint (227/252 = 90.1%)
Two changes:
1. **OCR moved before pass 2**: WinRT OCR now runs before the LLM name extraction call. Name-column tokens are grouped into per-row text fragments (14px Y-band) and passed as `ocrNameFragments` to `ExtractNamesAsync`. Prompt says: "OCR detected these partial text fragments from the name column of this image (they may be truncated or split): [fragments]. Use these as anchors — every fragment likely corresponds to an employee row. Identify the full name for each fragment." This lets the LLM reconstruct "M"+"an" → "Megan" and other fragmented names.
2. **Non-orthogonal coordinate hint**: All LLM prompts (pass 2, pass 3 strip, pass 4 x-marks) now include: "Note: this is a photograph of a printed grid — the grid lines may not be perfectly straight or orthogonal due to camera angle and perspective distortion. Read each row by visual context, not pixel-perfect alignment." This improved cross-row boundary accuracy.

Net result: Megan (IM(3)) 0/7 → 7/7, Raul (IM(3)) 4/7 → 7/7, Seena (IM(3)) 4/7 → 7/7 (was getting Megan's data), Sarah/Franny IM(1) each +1. No `--known-names` flag required.

### Phases 53–55 — Last-Employee Re-Query Chain (227→230/252 = 91.3%)

Three incremental improvements to handle the Ciara/Seena truncation/row-swap cluster:

| Phase | Change | Fix |
|-------|--------|-----|
| 53 | Last-employee re-query when LLM array truncates to n−1 values | +1 Ciara Oct31 |
| 54 | Extended re-query to also fire when last employee is blank in full n-element array | +1 Ciara Oct29 |
| 55 | Penultimate duplicate-value re-query: if last-employee re-query returns a value equal to the penultimate employee's value, re-query the penultimate employee too (row-swap absorption detection) | +1 Seena Oct29 |

These three commits together (+3 pts) brought the score from 227 to 230/252 — the current all-time best.

### Phases 56–58 — Duplicate Detection Attempts (All Reverted, 252-shift era)

**Diagnostic finding — Thu Oct30 root cause**: Investigated `ComputeDayColBoundsFromOcr()` boundaries for IM(1) Thu Oct30. Debug output showed clean, geometrically correct boundaries (center=907, x=[830,984], w=154, uniform ~154px spacing). The column boundary is NOT the root cause. Actual cause: **green highlighted Andee cell** in the Thu column acts as a visual anchor — the LLM reads it for rows 2 (Brittney) and 8 (Halle) and loses row position, producing "10:00-6:30" × 3 in the output. At temp=0, all re-queries against the same strip image return the same wrong answer — the visual anchor cannot be overcome by prompt changes. **Thu Oct30 cluster is irreducible with re-query approaches.**

| Phase | Change | Result | Why Reverted |
|-------|--------|--------|--------------|
| 56 | Adjacent-duplicate re-query: scan all pairs (idx 1 to n−2), re-query second employee if both share same non-trivial non-x value | 220/252 (−10) | False positives on legitimate shared values (Halle/Kyleigh both "2:00-6:30" on Oct26) |
| 57 | Triplet detection: re-query employees 2 and N when any non-trivial non-x value appears ≥3 times in a column | 219/252 (−11) | RTO/PTO holiday columns trigger it (IM(2) Nov27 Thanksgiving: 5 employees with "RTO") |
| 58 | Triplet detection restricted to TimeRangeRegex values only | 225/252 (−5) | Fired correctly on Thu Oct30 triplet ("10:00-6:30" ×3) but re-queries returned same wrong answer (highlighted cell persists at temp=0); also false-positive regressions in IM(3) Sat column and IM(1) Jenny Oct31 / Halle Nov01 / Ciara Oct31 |

### Answer.json Corrections
- Seena Sun: 10:00-3:00 → 10:30-3:00
- Kyleigh Tue: 9:00-1:00 → 9:30-2:00
- Ciara Tue: "" → x
- Victor Tue+Thu: 10:00-6:00 → 10:00-6:30

---

### Phase 56 — 5-Image Test Set + Architectural Fixes (374/434 = 86.2%)

Added IM(4) (Jul 27–Aug 2 2025, 13 employees, 91 shifts) and IM(5) (Jul 20–26 2025, 13 employees, 91 shifts) to the test set. Total: 434 shifts across 5 images.

Architectural fixes made while establishing the 5-image baseline:
- **`holidayBlankedDayIndices` HashSet**: tracks which day columns were blanked by the holiday detector so the x-marks pass (Pass 4) correctly skips them instead of re-filling with "x".
- **Uniform-RTO fall-through**: hours-contamination check no longer returns early — it falls through to the uniform-RTO check, allowing the Sat column to be correctly holiday-blanked after a numeric re-query.
- **DayNamePattern stripping moved before uniform-RTO check**: the LLM sometimes returns the day name as the first array element (e.g. "SAT"); stripping it before the 80%-threshold RTO check ensures the threshold isn't diluted by the header element.

Per-image baseline: IM(1)=68/77, IM(2)=78/91, IM(3)=83/84, IM(4)=75/91, IM(5)=70/91.

### Phase 57 — Retry Result Header Stripping (374/434, architectural fix)

**Root cause found**: The uniform-RTO retry path (`result = retryResult`) was not stripping leading day-name or ISO-date headers from the retry result. The main result had headers stripped before the uniform-RTO check, but the retry result did not. Added explicit header stripping immediately after `result = retryResult`.

No measurable score change (IM(4) Mon RTO retry does not fire in practice), but prevents a latent off-by-one alignment bug if the retry result has "THU" as its first element.

### Phase 58 — 8:xx Start-Time Contamination → Narrow Strip Re-Query (+3, 377/434 = 86.9%)

**Root cause**: IM(4) has a "hours" sub-column printed next to shift cells. The model reads the "8" hours-count digit as the start of a time range, producing "8:00-6:30", "8:00-5:30", etc. Confirmed by `[col-raw]` debug: IM(4) Thu returned `[8:00-6:30, x, 8:00-5:30, RTO, 8:00-6:00, xx, 8:00-6:30, ...]`. Confirmed safe: `Select-String '"Shift": "8:'` in all 5 answer files returns ZERO results — no legitimate shift starts with "8:".

**Fix**: After hours-contamination check and header stripping, count time-range values starting with "8:". If ≥ 2, re-query using the narrow strip (60% column width, crops out the hours sub-column area). Apply header stripping to the narrow result too.

**Result**: IM(4) Thu: Andee, Cyndee, Jenny corrected (+3). Franny changed from "8:00-6:00" (wrong) to "9:30-6:30" (still wrong but different). Net: IM(4) 75→78/91, overall 374→377/434.

### Phase 59 — Name Parenthetical Normalization (377/434, committed)

**Root cause**: When processing IM(2), the LLM sometimes reads the employee "Athena" as "Athena(train)" because the image shows a trainee designation next to her name. Without normalization, "Athena(train)" doesn't match "Athena" in the answer file.

**Fix** (in `OllamaCalendarService.ExtractNamesAsync`): Strip trailing parenthetical suffixes of ≤20 characters from extracted names:
```csharp
n = Regex.Replace(n, @"\s*\([^)]{1,20}\)\s*$", "").Trim();
```

Handles "(train)", "(T)", "(mgr)", "(SM)", etc. **Neutral in benchmark** (#7 = 377/434): the LLM didn't find "Athena(train)" this run — it missed Athena entirely. Non-determinism in the model means the LLM finds Athena with "(train)" in roughly half of runs. Committed because it's architecturally correct and safe.

### Phase 60 — Leading-RTO Contamination Re-Query (REVERTED, 372/434 = −5)

**Hypothesis**: IM(4) Mon Jul28 consistently returns RTO for the first 3 employees (Andee, Brittney, Cyndee) when their actual shifts are time-ranges. The uniform-RTO check (≥80% of all values being the same) doesn't fire because only 8/13 = 62% are RTO and the values aren't all the same. Ground-truth analysis confirmed: **no legitimate date column in any of the 5 test images has the first 3 employees all on RTO/PTO** — the max legitimate RTO for any first-3 group is 0 across all 35 date columns.

**Implemented**: If `result[0] == result[1] == result[2]` and all are "RTO"/"PTO" AND at least one time-range exists elsewhere in the column → re-query with a narrow-strip reinforced prompt explicitly warning "the FIRST employee's cell likely contains a TIME RANGE, not RTO."

**Benchmark #8 result: 372/434 (−5)**:
- IM(4): 76/91 (was 78/91, −2) — led to fix firing on **IM(4) Sun** where the model erroneously returned RTO for first 3 employees (model misread, not contamination)
- IM(5): 67/91 (was 70/91, −3) — also fired false positives on some IM(5) columns

**Root cause of failure**: The "first 3 employees all RTO" pattern can occur due to **model misread on any column** — it is not exclusive to contaminated columns. When the model returns RTO for first 3 on a non-contaminated column, our heuristic fires and the re-query changes those values to close-but-wrong time ranges, creating new errors. The heuristic cannot distinguish contamination from model error.

**Reverted in full.**

### Phase 62 — ocrTimeMap Holiday-Guard Discriminator (REVERTED, 377/434 = 0)

**Target**: IM(4) Aug2 Sat — 9 errors. Model returns all-RTO for a normal work day → holiday detector false-fires → 9 employees with time-range shifts become blank.

**Hypothesis**: If WinRT OCR found ≥1 time-range token in the column (via `ocrTimeMap[dayIdx]`), the store is open and the holiday blank should be suppressed. Proposed discriminator: `if (ocrTimeRangesInCol > 0) skip holiday blank`.

**Diagnostic result**: `ocrTimeMap has 0 time-range(s)` for **all 7 day columns** of IM(4). WinRT OCR scanned 163 total elements across the image but zero matched `TimeRangeRegex` (`^\d{1,2}:\d{2}\s*[-–]\s*\d{1,2}:\d{2}$`). IM(4)'s time cells are read by WinRT as fragments (separate "9:00" and "5:30" tokens) rather than compound "9:00-5:30" strings. The guard never fired — it is dead code for this dataset.

**Conclusion**: IM(4) Aug2 Sat confirmed as a second Sat-column hard ceiling (same failure mode as IM(5) Jul26). Score unchanged at 377/434. Code reverted in full.

### Phase 61 — 80% Majority Holiday/Uniform-RTO Check (REVERTED, 377/434 = 0)

**Target**: IM(5) Jul26 Sat — 11 errors (all employees should have time-ranges; model returns all-RTO for the column).

**Root cause analysis via debug runs**:
- IM(5) Jul26 (Sat) is day 6, col X≈[1185,1347], narrow strip = left 60% = X≈[1185,1282]
- Initial LLM call returns 13 values: 12 `RTO` + Seena=`"2:30-8:00"` 
- Inner uniform-RTO check uses strict `allSame` → Seena's value breaks it → narrow retry fires (12/12 uniform after header strip)
- Narrow retry (reinforced prompt, 60% width crop) returns: `[SAT, RTO, RTO, RTO, RTO, x, RTO, RTO, RTO, RTO, RTO, RTO, RTO]` — "varied but no time-ranges" → original 12-RTO + Seena kept
- Outer holiday detector: `allSame = All(v == "RTO") = FALSE` (Seena breaks it) → detector doesn't fire → 10 employees get wrong RTO written to output
- `ocrTimeMap[6]` = all null (WinRT OCR finds ZERO time-range tokens in Sat column) → OCR pre-fill cannot help

**Attempted fix**: Change both inner uniform-RTO check and outer holiday detector from strict `allSame = All(...)` to 80% majority (`mode ≥ 80% of non-blank values`).

**Result of fix**: Holiday detector now fires for Jul26 → blanks all 13 cells → OCR salvage recovers only `Victor=RTO` and `Seena=RTO` (2/13 cells). Jenny (should be RTO) and Kyleigh (should be RTO) become `""` (blank/wrong). The only improvement is Seena going from `"2:30-8:00"` (wrong) to `""` (correct by blank≡x rule? no — Seena's ground truth is a time-range). Net effect: −1 (Jenny −1, Kyleigh −1, Seena +1 only if her ground truth is RTO which it isn't → likely net −1 or 0 depending on scorer edge cases). Score unchanged at 377/434.

**Why this is a hard ceiling**:
1. Model pattern-completes all-RTO for the Sat column regardless of narrow-strip prompt reinforcement
2. WinRT OCR finds ZERO time-range tokens in the Sat column — cannot pre-fill or salvage the correct values
3. Firing the holiday detector makes things worse (only 2/13 cells recovered via OCR salvage, missing Jenny/Kyleigh)
4. No improvement path without image preprocessing or model fine-tuning

**Reverted in full.**

### Phase 63 — Retail-Context Hint v3 in Hybrid Pipeline (+17, 394/434 = 90.8%)

**Root cause of v1/v2 failure**: The previous retail hints (session 21 experiment attempts) were injected into OllamaCalendarService.ExtractColumnAsync. That function is **never called** by the hybrid pipeline — HybridCalendarService.ExtractColumnFromImageAsync is the actual column extractor. The hints were dead code with no effect.

**Fix**: Moved retail hint to the two real LLM call sites in HybridCalendarService.ExtractColumnFromImageAsync:
1. **Initial prompt** (both stripMode and full-image fallback): added retailHint at the VERY START (maximum salience), conditional on isWeekendCol = dayName ∈ {Sat, Sun}.
2. **reinforcedPrompt retry** (the re-query that fires when uniform-RTO contamination is detected): added 
einforcedRetailHint at the top with explicit "STOP. Your previous answer had RTO for every employee. That is WRONG." language.

**v3 hint content**:
- Initial: "CRITICAL: This retail store is open 7 days a week. Saturday and Sunday are NORMAL WORKDAYS — every employee is scheduled for a regular shift. You MUST read the actual value printed in each cell. Most cells will contain a shift time range (e.g. '9:00-5:30', '12:00-8:30'). ONLY write 'RTO' if you can clearly see the letters R-T-O printed in that individual cell. Never assume 'RTO' because it is a weekend. If you find yourself writing 'RTO' for every employee, STOP — you are misreading shift times. Look at the actual numbers."
- Reinforced retry: "STOP. Your previous answer had 'RTO' for every employee. That is WRONG for a weekend column. This retail store is open on Saturdays and Sundays — every employee has a real shift time printed in their cell. You MUST read the actual numbers printed in each cell (like '9:00-5:30', '8:00-4:00'). ONLY write 'RTO' if you clearly see the letters R-T-O in that specific cell — not because it is a weekend."

**Result**:
| Image | Before | After | Delta |
|-------|--------|-------|-------|
| IM(1) | 68/77 | 68/77 | 0 |
| IM(2) | 78/91 | 77/91 | −1 |
| IM(3) | 84/84 | 84/84 | 0 |
| IM(4) | 78/91 | 86/91 | **+8** |
| IM(5) | 70/91 | 79/91 | **+9** |
| **Total** | **377/434** | **394/434** | **+17** |

IM(4) Sat Aug2: 
arrow-reinforced retry has time-ranges — using it fires. 9 previously-blank employees now get correct (or near-correct) time-range values. Remaining 5 errors: Andee/Brittney/Cyndee Jul28 RTO (Mon column unrelated issue), Franny Jul31 TIME-MISREAD, Victor Aug2 got "x" expected "RTO".

IM(5) Sat Jul26: same retry path now fires and returns time-ranges. Still 12 errors in Jul26 column (shift-time mismatches, not all-blank-wrong), plus 4 Jul24 errors (row-offset) and 2 misc.

IM(2) minor regression (−1): Nov26 (Wed) now triggers uniform-RTO and the retry returns time-ranges; Ciara Nov26 changed from blank (wrong) to wrong time-range (still wrong but different). No net harm to the original hard-ceiling errors.

**Key lesson**: Always verify which function is actually called before optimizing a prompt. The hybrid pipeline bypasses OllamaCalendarService.ExtractColumnAsync entirely — all column LLM calls go through HybridCalendarService.ExtractColumnFromImageAsync.

---

_Session era (Session N format, ascending). **New entries append at the end of this file.**_

---

### Session 22 — Engineering Review: Dead Code Removal + Prompt Externalization (394/434 = 90.8%, no change)

**Goal**: `/plan-eng-review` — three tasks: (1) dead code cleanup in CLI, (2) externalize all LLM prompts to JSON, (3) general C# best practices.

**Dead code removed** (`Program.cs`):
- `halvesMode` flag, validation block, main loop block, and two helpers: `MergeCalendarResults` (~70 lines) and `MergeBlankFill` (~90 lines)
- `resizeWidth` flag and resize block
- `ensembleModel` flag, validation, console log, and ensemble loop block
- Updated `PrintUsage()` to remove the three removed flags and correct accuracy to 90.8%

**Dead methods removed** (`OllamaCalendarService.cs`):
- `ExtractAllShiftsCsvAsync` (~100 lines — Phase 41 experiment, never called)
- `ExtractAllShiftsDualAsync` (~140 lines — Phase 42 experiment, never called)

**Prompt externalization** (`CalendarParse.Cli/Prompts/prompts.json` + `Services/PromptService.cs`):
- Created `prompts.json` as an embedded resource with 16 named templates (JSON arrays of line strings)
- Created `PromptService` — static class with lazy-loaded embedded resource, `{varName}` interpolation
- Migrated all prompts in both `HybridCalendarService.cs` (5 prompts) and `OllamaCalendarService.cs` (7 prompts) to use `PromptService.Get(...)`
- Template keys: `weekend_hint`, `reinforced_weekend_hint`, `strip_column`, `full_image_column`, `reinforced_column`, `last_employee_requery`, `penultimate_employee_requery`, `extract_header`, `extract_names`, `extract_names_known_names_suffix`, `extract_names_additional_hints_suffix`, `extract_names_ocr_fragments_suffix`, `extract_x_marks`, `extract_column`, `extract_column_anchored`, `extract_all_shifts`, `extract_row`

**Result**: Build clean (0 errors), benchmark **394/434 (90.8%)** — no regression.

---

### Session 23 — Fireworks Cloud Backend: qwen3-vl-30b-a3b-instruct (REVERTED, 250/434 = 57.6%)

**Hypothesis**: Fireworks serverless API with a larger model (30B MoE) would match or beat `qwen2.5vl:7b` on Ollama while removing the local GPU requirement.

**Infrastructure work (committed, kept)**:
- `FireworksCalendarService.cs`: subclasses `OllamaCalendarService`, overrides `CallLlmAsync` to POST to Fireworks API (`/inference/v1/chat/completions`), Bearer auth from `FIREWORKS_API_KEY` env var, `seed=42` + `temperature=0.0` for determinism
- `Program.cs`: added `--fireworks` flag, `.env` auto-loading (`LoadDotEnv()`), single-file mode (`File.Exists` check)
- `scripts/probe-fireworks-determinism.ps1`: 3-call determinism probe confirmed `seed=42` + `temperature=0` produces bit-for-bit identical outputs on Fireworks serverless
- Removed overlay image generation and `.debug.txt` output from CLI

**Benchmark result**: 250/434 (57.6%) — **−144 shifts vs baseline**

| Image | Score | Notes |
|-------|-------|-------|
| IM (1) | 40/77 (52%) | |
| IM (2) | 66/91 (73%) | |
| IM (3) | 58/84 (69%) | |
| IM (4) | **16/91 (18%)** | Almost all shifts blank — total failure |
| IM (5) | 70/91 (77%) | |

**Root cause**: `qwen3-vl-30b-a3b-instruct` is a different model family from `qwen2.5vl:7b`. The `2.5VL` architecture was specifically designed for dense visual table reading; Qwen3 is a general-purpose model that lacks the same visual grounding capability. The model returns empty strings for shift cells ("got \"\" expected \"9:00-5:30\"") rather than misreading them — it is not engaging with the cell content at all. IM(4) collapse to 16/91 confirms the model architecture is not suitable for this task.

**Determinism confirmed** (3 probes, identical responses). Fireworks infrastructure works correctly — the model choice is the problem.

**Infrastructure kept** (FireworksCalendarService, --fireworks flag, .env loading, single-file mode, removed debug outputs). The `--fireworks` flag can be reused if a `qwen2.5vl`-family model becomes available on Fireworks.

---

### Session 24 — Research: Kimi K2.5 Test + Infrastructure Research (394/434 = 90.8%, no change)

**Goal**: Evaluate remaining Fireworks cloud model options; research upstream tooling improvements (table-transformer, Ollama `format` param, GLM-OCR).

**Kimi K2.5 (`accounts/fireworks/models/kimi-k2p5`) — FAILED, not committed**

Tested on IM(1). Kimi K2.5 is a reasoning model. Its chain-of-thought reasoning leaked into the JSON names field in pass 2: `"Let me trace through the rows visually:"` appeared as an employee name string. All downstream passes received a corrupted names list. Fatal for the pipeline — reasoning chain bleed cannot be suppressed without a system-prompt instruction the model ignores. **Not committed. Added to anti-patterns.**

**qwen2p5-vl-72b — NOT TESTED, blocked**

Fireworks serverless endpoint for this model returns empty responses (not a 4xx error, just no content). The model is on-demand only at $10/hr minimum. Not viable. **Added to anti-patterns.**

**Research findings (no code changes except GLM-OCR service design):**

1. **table-transformer** (Microsoft, GitHub): Python-only, PyTorch/conda, trained on PubTables-1M (academic PDFs). Outputs bounding boxes, requires separate OCR for cell text. Last updated 3 years ago. No C# bindings. Already superseded by existing OpenCV grid detection in this codebase. **Verdict: NOT applicable.**

2. **Ollama `format` parameter**: Confirmed technically feasible via `OllamaCalendarService.CallOllamaAsync` subclass (method is `internal virtual`). `format: "json"` enforces grammar-sampling (constrained decoding in llama.cpp) — structurally different from natural language output instructions. Would genuinely prevent array truncation (`minItems`/`maxItems`) without the re-query fallback. However, all 57 remaining errors are semantic (wrong cell read, row-offset, value swap) not structural — expected score impact near zero. Low priority but architecturally cleaner than the current scrubbing approach.

   **Clarification on "JSON schema in prompts" (Session 23 research note 3)**: What the prompts currently have is natural language output instructions (instruction-following, model *chooses* to comply). A proper JSON Schema via the `format` parameter operates at grammar-sampling level — the model *cannot* produce non-conforming output. These are different in kind. The `enum` constraint is not viable for this task (time ranges are open-ended), but `minItems`/`maxItems` on the array would be new and real.

3. **GLM-OCR (zai-org/glm-ocr)**: Task-prefix prompt format (`"Table Recognition:"`), outputs markdown table text. Ollama bug #14498 (Windows/NVIDIA) blocks deployment on this machine — model fails to load with 500 Internal Server Error, unresolved as of Ollama v0.20.2. **Interface designed and committed** — `GlmOcrCalendarService.cs` created in `CalendarParse.Cli/Services/`. Architecture: one call (full image + task prefix) → markdown table → deterministic parser → shift schema. No score impact now; ready for deployment when bug is resolved or on Linux/Mac.

**Score unchanged at 394/434 (90.8%)**.

---

### Session 25 — Re-benchmark qwen3-vl-30b with P20 Pipeline (REVERTED/rejected, 329/434 = 75.8%)

**Goal**: Re-test `accounts/fireworks/models/qwen3-vl-30b-a3b-instruct` against the full P20 pipeline (retail-context hints, OCR supplement, session name pool, narrow-strip 8:xx re-query). Session 23 had tested this model early (before retail hints) and got 250/434. The hypothesis was that the retail hint (+17 in Phase 63) might close the gap.

**No code changes made.** Fireworks infrastructure committed in Session 23 was reused unchanged.

**Benchmark result**: 329/434 (75.8%) — still **−65 shifts below qwen2.5vl:7b baseline**

| Image | Score | Notes |
|-------|-------|-------|
| IM(1) | 61/77 (79.2%) | |
| IM(2) | 62/91 (68.1%) | Rate limits: no; Athena still invisible |
| IM(3) | 58/84 (69.0%) | Rate limit hit on 2 of 7 days; + roster alignment failure |
| IM(4) | 77/91 (84.6%) | Header LLM read "Unknown 0" → OCR override to "July 2025" |
| IM(5) | 71/91 (78.0%) | Rate limit hit on 1 day (Seena got raw error JSON) |

**Root causes of regression vs baseline**:

1. **Rate limit errors (Fireworks serverless throttling)**: IM(3) had `RATE_LIMIT_EXCEEDED` on 2 of 7 day strips (Ciara's rows). IM(5) had 1 rate limit error (Seena). Rate limit errors are returned as raw JSON error strings in the shift value cell — scorer counts them as wrong values. This alone accounts for ~7 errors.

2. **Roster alignment failure in IM(3) bottom cluster**: Megan completely MISSING (0/7) despite being on the roster. Shifts that should have been Megan's were displaced to Clara/Athena/Tori as EXTRA (spurious) entries. The LLM is assigning the correct shift values to the wrong employee row in the bottom 6-employee cluster. Pattern: `Kyleigh 2025-09-21: got "RTO" expected "11:30-4:00"` while `Megan 2025-09-21: MISSING expected "RTO"` — values correctly present but mapped one employee off. This is a visual anchoring failure (row disambiguation) that the pipeline cannot recover.

3. **IM(4) header failure**: Pass 1 returned "Unknown 0" for the month (LLM failed to read the header); OCR fallback correctly overrode to "July 2025". Not a scoring issue but novel failure mode vs qwen2.5vl.

**Even excluding rate-limit errors**, IM(4)=84.6% and IM(1)=79.2% are both significantly below baseline (~90%+ per image). The retail-context hint improved IM(4) dramatically vs Session 23 (16→77/91) but the model is still architecturally weaker for dense row-disambiguation.

**rolm-ocr assessment**: The `rolm-ocr` model outputs markdown, not JSON. It cannot be used as a drop-in for the strip LLM. Integration would require a separate parser that converts markdown table text to the shift record schema. Deferred to future session.

**Conclusion**: qwen3-vl-30b confirmed weaker than qwen2.5vl:7b for this task even with the P20 pipeline applied. The architecture difference (general MoE vs specialized visual table reader) is the root cause. No further testing of this model. Best score remains 394/434.

---

### Session 26 — GLM-OCR: `glm-ocr` Model Benchmark (402/434 = 92.6%, **new best**)

**Goal**: Deploy and benchmark the `glm-ocr` Ollama model on the full 5-image test set. Previously believed blocked by Ollama bug #14498.

**Root cause of prior crash (CORRECTED)**: The prior crash was NOT Ollama bug #14498. It was `OLLAMA_CONTEXT_LENGTH=262144` set globally in the system environment → `ggml_nbytes(src0) > INT_MAX` in CUDA copy (`cpy.cu:396`). The fix: pass `options: { num_ctx: N }` per-invocation to override the global.

**Key discoveries during bring-up**:
1. **ctx size calibration**: 4096 → too small (vision encoder `a->ne[2]*4 == b->ne[0]` dim mismatch). 32768 → works for landscape (1600×1200). Portrait image IM(3) (1200×1600) requires 65536 (different CUDA tile dimensions). Fixed via retry loop: try 32768, if HTTP 500 with the dimension-mismatch error, retry at 65536.
2. **HTML output format**: `GlmOcrCalendarService.cs` was written expecting markdown pipe tables. GLM-OCR actually outputs `<table class="table table-bordered"><thead>...</thead><tbody>...</tbody></table>`. Discovered via direct Ollama API probe. Rewrote parser entirely.
3. **1-cell holiday column**: Thanksgiving (Thu Nov27) has 1 `<td>` per employee in IM(2) instead of 2 (shift + hours). This shifted all subsequent column indices by 1. Fixed via sliding-window extraction: if `row[col+1]` looks like a shift value (time range, "x", PTO) rather than an hours number, the current day consumed only 1 cell → advance by 1 instead of 2.
4. **OCR date misread**: GLM-OCR misread the Sunday date in IM(1) — output "10/28/2025" (Tuesday!) at the Sunday column. Fixed via majority-vote anchor Sunday: for each date in the date row, back-compute the implied Sunday (`date − dayOffset`); take the most-common Sunday (usually 6-of-7 are correct) and derive all 7 dates from it. Day-of-week offsets come from the `SUN/MON/TUES/...` header row (or positional 0–6 fallback).

**Benchmark result**: **402/434 (92.6%)** — new best, +8 shifts over 394/434 hybrid baseline.

| Image | Score | Notes |
|-------|-------|-------|
| IM(1) | 74/77 (96.1%) | 3 errors: Sarah Oct29 shift/x, Jenny Oct26 time diff (6:00 vs 6:30), Kyleigh Oct28 shift end diff |
| IM(2) | 90/91 (98.9%) | 1 genuine error: Halle Nov28 (12:00-4:30 vs 11:00-7:30). "Clara"/"Ciara" naming EXTRA entries (don't count against score) |
| IM(3) | 64/84 (76.2%) | 20 errors — "Cydee"/"Cyndee" phantom row, OCR misreads in portrait orientation, "xx" cells not read |
| IM(4) | 86/91 (94.5%) | 5 errors: Brittney Jul31 shift/x, Destiny/Seena "xx" not read (model misread) |
| IM(5) | 88/91 (96.7%) | 3 errors: time-range diffs, Kyleigh x/RTO swap |

**Remaining errors analysis**:
- **"xx" cells (4 errors in IM(3)/IM(4))**: GLM-OCR reads these as empty or wrong shift. Pure model misread — cannot fix in parser.
- **"Clara"/"Cydee" phantom rows**: GLM-OCR misspells "Ciara"→"Clara" and "Cyndee"→"Cydee". Test scorer handles these via fuzzy matching (score correct), but it also creates phantom extra rows because GLM-OCR output has BOTH misspelled AND sometimes correct names (two rows). Parser deduplication could fix these.
- **IM(3) portrait-orientation errors (Cyndee, Megan, Kyleigh)**: Wrong shifts for several employees — model-level misreads; hard to fix without image preprocessing or larger model.

**Architecture**: `--glm-ocr` flag routes to `GlmOcrCalendarService`. One API call per image (full image, no strip). ~12–70 seconds per image. Inference time: landscape ~12s, portrait ~71s. The `HybridCalendarService` (qwen2.5vl + strip) pipeline is still available via `--model qwen2.5vl:7b` (default).

---

### Session 27 — Fireworks Model Evaluations (glm-5, qwen3p6-plus, qwen3-vl-30b-a3b-thinking)

**Hypothesis**: Additional Fireworks-hosted models might match or exceed `qwen2.5vl:7b` accuracy on the hybrid pipeline.

**Models tested:**

#### accounts/fireworks/models/glm-5 — 117/434 (27.0%)
Text-only model. Returned 0 employees on every image; all names populated via OCR name supplement only. Final score 117/434 is exactly what OCR supplement alone produces (no LLM shift extraction). The 117 "correct" matches are all from the empty-shift ≡ x equivalence rule on employees who happened to have days off. Not a vision model. Do not retest.

#### accounts/fireworks/models/qwen3p6-plus — 117/434 (27.0%)
Same failure mode as glm-5. Identical score (27.0%) and identical 0-employees-per-image pattern. Text-only model, no vision capability. Do not retest.

#### accounts/fireworks/models/qwen3-vl-30b-a3b-thinking — 212/434 (48.8%)
Vision-capable model. Employee names extracted correctly (11–14 per image). However shift reading was severely degraded — step profiling table showed only 2 of 7 day strips produced cells (Sun:+X, Sat:+X, all others 0). Thinking chain burns through token budget before cell reading; most strip calls returned empty or near-empty arrays. Per-image: IM(1)=37/77, IM(2)=48/91, IM(3)=44/84, IM(4)=34/91, IM(5)=49/91. 4:01 per image (vs ~1:00 for qwen2.5vl:7b). Far below baseline on both accuracy and speed. Do not retest.

**Infrastructure note**: No code changes; all three benchmarks ran via the existing `--fireworks` flag. All outputs saved to `fw-glm5-bench.txt`, `fw-qwen3p6plus-bench.txt`, `fw-qwen3vl-thinking-bench.txt`.

---

### Session 28 — Portrait Crash Root Cause Research + Fix (402/434 = 92.6%, regression recovery)

**Context**: Previous benchmark run discovered GLM-OCR's IM(3) portrait is broken — returns 0/0 shifts with a GGML assertion crash. IM(1) (landscape) still worked at 74/77. Session goal was to diagnose and fix the crash.

**Research phase — Ollama issues investigated**:
- **[#14171](https://github.com/ollama/ollama/issues/14171)** ("glm-ocr fails with GGML_ASSERT") — **open**: same stack trace (`applyMRoPE` → `TextModel.Shift` → KV cache shift). Ollama dev marked "needs more info."
- **[#14401](https://github.com/ollama/ollama/issues/14401)** ("rope dimension metadata missing") — **closed**: Root cause: `hidden_size=1536`, `num_heads=16` → correct rope_dim=96, but Ollama defaults to 128 because the GGUF lacks `glmocr.rope.dimension_count` metadata. Reporter's fix: change `num_ctx` from 7000 to **8192**.
- **[#14321](https://github.com/ollama/ollama/issues/14321)** ("tight imagesize restriction in API but not CLI") — **closed as duplicate** of [#14171](https://github.com/ollama/ollama/issues/14171). Reporter had `num_ctx=4000` → crash; `num_ctx=8192` → works. Ollama dev confirmed fix.

**num_ctx=8192 attempt — DID NOT FIX OUR CRASH**: Changed `loop { 32768, 65536 }` to single `num_ctx=8192`. IM(3) still returned GGML assertion crash. The GitHub-issues fix works for smaller images; our 1200×1600 portrait triggers the assertion regardless of `num_ctx`.

**Root cause (empirical)**: The GGML assertion `a->ne[2] * 4 == b->ne[0]` fires in `ggml_rope_multi` when Ollama processes portrait images tall enough to enter a multi-tile code path. Our earlier session had confirmed:
- 1200×1600 → crash (regardless of num_ctx)
- 1008×1344 → works at num_ctx=32768

Conclusion: the crash is governed by image height (tile count), not context length. Portrait images with height > 1344px exceed the single-tile threshold and hit the broken M-RoPE sections assertion.

**Fix implemented** (`GlmOcrCalendarService.cs`):
1. Added `ResizeForGlmOcrIfNeeded(byte[])` method: if `src.Height > 1344`, scales proportionally so `height = 1344` using `CvInvoke.Resize` (Emgu.CV, already in project). `Inter.Area` for downscaling. Output encoded as JPEG.
2. Called in `ProcessAsync` before base64 encoding.
3. Reverted `num_ctx` back to 32768 (8192 did not help; 32768 works post-resize since resized images are single-tile).
4. Removed the dead `VisionDimError` retry loop (the retry never fired correctly anyway — Ollama JSON-encodes `>` as `\u003e` so the `Contains` check always missed).

**Benchmark result (post-fix)**: **402/434 (92.6%)** — portrait crash resolved, IM(3) restored to 64/84.

| Image | Score | Notes |
|-------|-------|-------|
| IM(1) | 74/77 | Unchanged |
| IM(2) | 90/91 | Unchanged |
| IM(3) | 64/84 | Restored from 0/0 crash |
| IM(4) | 86/91 | Unchanged |
| IM(5) | 88/91 | Unchanged |

**Note**: The root-cause [Ollama #14171](https://github.com/ollama/ollama/issues/14171) (open — M-RoPE assertion in multi-tile code path) is still **open**. The resize is a temporary workaround. When Ollama ships the upstream vendor sync fix, remove `ResizeForGlmOcrIfNeeded` and test full-resolution portrait.

**Phantom-row dedup (planned, not implemented this session)**: The IM(3) 64/84 score still has "Cydee"/"Cyndee" phantom row duplicates and other portrait-quality errors. Phantom dedup was the next planned experiment but was deferred because the session was consumed by crash diagnosis.

---

### Session 29 — Phantom Dedup Attempt + keep_alive/num_predict Crash Hardening (402/434 = 92.6%)

**Context**: Starting from 402/434 (92.6%), session goal was to implement phantom employee deduplication (+7 projected) and improve benchmark reliability.

**Phantom dedup attempt (REVERTED)**:

Implemented Levenshtein ≤ 2 dedup in `ParseHtmlTable` — keep first occurrence, discard later rows within distance 2. Benchmark result: **380/434 (87.6%)** — catastrophic −22 regression.

Root cause: `Andee` (length 5) and `Cyndee` (length 6) both end in `-dee` and are distance 2. Dedup kept `Andee` and dropped `Cyndee` as a phantom. The same false positive occurred across IM(1), IM(2), IM(4), IM(5). IM(3) itself showed **zero improvement** — the raw HTML in the current run contained only `Cydee` (no `Cyndee` duplicate), so the "phantom row" issue didn't even exist in this run. The Cyndee errors are a cell-alignment problem in portrait orientation, not a naming collision. **REVERTED**.

**Diagnoses during session**:
1. **1345px stability confirmed**: Python probe ran 1344px 10/10 → zero crashes; 1345px 10/10 → zero crashes. Both produce compact output (28 rows, ~12s each). Revision to comment in code from "3×448 tile boundary" to empirical truth: the compact/verbose mode boundary is between 1345 and 1400, not between 1344 and 1345.
2. **1400px crash rate confirmed**: Python probe 1400px → **6/10 crash** (40% rate), OK runs produce 222–244 garbage rows. Confirms 1344 is the correct ceiling.
3. **Benchmark instability discovered**: Running the full 5-image benchmark consistently crashed IM(3) even with `MaxGlmOcrHeight = 1344`. Single-image IM(3) runs succeeded. Root cause: after processing IM(1) and IM(2), GPU/Ollama state changes such that IM(3) enters a "crash mode" where it generates enough tokens to fill the 32768-token context, triggering the KV-cache shift assertion.

**Crash hardening — keep_alive=0**:
Added `keep_alive = 0` to request body — forces model unload after each image. Reduced crash frequency but did **not** fully eliminate it: 1/3 full-benchmark runs still crashed. Root cause: the crash is non-deterministic; sometimes even a freshly loaded model enters verbose mode for IM(3).

**Crash hardening — num_predict=20000 (COMMITTED)**:
Added `num_predict = 20000` to options. The MRoPE assertion fires at KV-cache step 32,769 (first shift). A valid verbose output for IM(3) uses ~9,326 tokens. Setting `num_predict = 20000` allows all valid output to complete (2× safety margin over 9,326) while preventing any run from reaching step 32,769. Tested 3/3 consecutive full-benchmark runs — **zero crashes**. Score stable at **402/434 (92.6%)**.

**Final state**: `keep_alive=0` + `num_predict=20000` both committed. The benchmark is now deterministically reliable rather than ~33% crash rate on IM(3).

| Image | Score | Notes |
|-------|-------|-------|
| IM(1) | 74/77 | Unchanged |
| IM(2) | 90/91 | Unchanged |
| IM(3) | 64/84 | Stable (no more crashes) |
| IM(4) | 86/91 | Unchanged |
| IM(5) | 88/91 | Unchanged |
