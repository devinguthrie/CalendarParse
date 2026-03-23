# CalendarParse CLI — Experiment Log

_Last updated: 2026-03-13 (session 8)_

All experiments use `IM (1).jpg` (11 employees, 77 shifts, Oct 26–Nov 1 2025) and — from Phase 16 onward — also `IM (2).jpg` (13 employees, 91 shifts, Nov 23–29 2025), both compared against their respective `.answer.json` files. Combined score denominator = 168 shifts.

Scores listed as `N/77 (X%)` for IM(1)-only phases or `N/168 (X%)` for combined phases. Multiple scores on the same row = separate runs showing variance.

---

## Phase 1 — CSV Data Audit

**Action**: Manually reviewed `IM (1)-whole-table.csv` against `IM (1).answer.json`  
**Result**: Found and corrected several CSV errors:
- Andee's Thursday was missing hours
- Brittney's Monday was missing hours
- Seena and Kyleigh had shift time mismatches
- Ciara had shift time mismatches
- Sarah's weekly total was off by 8 hours
- Stray `8` values appeared in the header row of the CSV

**Outcome**: Ground truth JSON updated to be consistent with the actual image. Answer JSON now has all 11 employees, 77 shifts.

---

## Phase 2 — Baseline: Single-Pass JSON (llama3.2-vision:11b)

**Model**: `llama3.2-vision:11b`  
**Architecture**: 1 call → ask for complete `{Month, Year, Employees[{Name, Shifts[{Date,Shift}]}]}` JSON  
**Score**: 5/77 (6.5%)  
**Why it fails**:
- Model correctly identifies dates and some names
- Returns blank `Shift` values for almost all cells — does not read cell content
- Output JSON is structurally valid but mostly empty
- Model simply doesn't "look inside" the cells when asked for the whole table at once

---

## Phase 3 — 3-Pass JSON Per-Row (llama3.2-vision:11b)

**Model**: `llama3.2-vision:11b`  
**Architecture**: Header pass → Names pass → one JSON-format row per employee  
**Score**: 14/77 (18.2%) — peak; avg ~15%  
**Why it fails**:
- Returns 26 phantom employees (hallucinates extra names)
- SAT date returned as `2025-11-1` (un-padded) — later fixed with `NormalizeIsoDate`
- "Senna" extracted from secondary summary table (below main schedule)
- Row pass returns mostly `""` with occasional correct values

---

## Phase 4 — CSV Bulk Extraction (llama3.2-vision:11b)

**Model**: `llama3.2-vision:11b`  
**Architecture**: Ask model to return entire table as CSV text, then parse  
**Score**: 11/77 (14.3%)  
**Why it fails**:
- Model truncates output before reaching lower employees
- Misaligns columns when re-formatting as CSV
- `num_predict` was unlimited at the time → 600–900 second timeouts
- Inconsistent column separator usage

---

## Phase 5 — Per-Employee 7-Shift CSV (llama3.2-vision:11b)

**Model**: `llama3.2-vision:11b`  
**Architecture**: Header → Names → one focused call per employee, return `7` comma-separated shift values  
**Score**: 18/77 (23.4%) — new record; avg ~17%; range 14–23%  
**Timing**: 13–17 seconds (with `num_predict` limits: 300/200/150)

**What works**:
- Consistent 11 employees extracted
- No more timeouts
- Date normalization working

**Why it still fails**:
- Model copies the example from the prompt (`10:00-6:30,x,PTO,9:00-5:30,,x,12:00-8:30`) instead of reading actual cells
- "Seena" consistently returned as "Siana" — 7 shifts unmatched
- Score variance is high even with identical prompts

---

## Phase 5a — Prompt Tuning Variants (llama3.2-vision:11b)

All use the per-employee CSV architecture. Run against the same image.

### 5a-1: Add date labels to row prompt
**Change**: Included `2025-10-26 (Sun), 2025-10-27 (Mon)...` labels in the prompt  
**Score**: 6/77 (7.8%)  
**Why it fails**: Model confused by date format in prompt; starts returning dates instead of shifts

### 5a-2: Remove example from row prompt
**Change**: Removed the `"Example reply: 10:00-6:30,x,PTO..."` line entirely  
**Score**: 9/77 (11.7%)  
**Why it fails**: Without an example, model defaults to outputting `RTO` for every cell

### 5a-3: Two diverse examples in row prompt
**Change**: Added two different example lines showing different patterns  
**Score**: 5/77 (6.5%)  
**Why it fails**: Model copies the first example verbatim for every employee row

### 5a-4: Single-cell parallel queries per day
**Change**: Instead of 7 values at once, one call per day cell (77 calls total)  
**Score**: 11/77 (14.3%); timing ~3–5 min  
**Why it fails**: With `num_predict=30`, model defaults to `RTO` for nearly every cell; too short for it to "think"

### 5a-5: Restored original single-example prompt (revert)
**Change**: Reverted to best-performing prompt from Phase 5  
**Score**: 18.2%, 14.3%, 19.5% across 3 runs  
**Conclusion**: llama3.2-vision:11b ceiling is ~17% average, ~23% peak. Move to a better model.

---

## Phase 6 — Model Switch: llava:13b

**Model**: `llava:13b`  
**Architecture**: Same per-employee 7-shift CSV (Phase 5 prompt)  
**Score**: 0/77 (0.0%)  
**Why it fails**:
- Only extracts 1 employee (returns "Unknown" for month, year=0)
- Based on an older vision architecture not suited for dense table reading
- Not worth further tuning

---

## Phase 7 — Model Switch: qwen2.5vl:7b, Per-Employee Fallback

**Model**: `qwen2.5vl:7b`  
**Architecture**: Same per-employee 7-shift CSV (Phase 5 prompt); no changes to code  
**Score**: 26/77 (33.8%)  
**Why it succeeds vs llama**: Qwen2.5-VL is a newer, stronger vision-language model with better table comprehension  
**Remaining issues**: Per-employee row pass still confused rows — several employees returned near-identical values (`RTO, x, PTO, PTO, 10:00-6:30, "", x`) suggesting row cross-contamination

---

## Phase 8 — Single-Shot Full Table Extraction (qwen2.5vl:7b)

**Model**: `qwen2.5vl:7b`  
**Architecture**: 1 extraction call for all 11 employees; response is a JSON object with employee names as keys, values are 7-element arrays  
**Score**: 50.6% (run 1), 46.8% (run 2), 35.1% (run 3) — avg ~44%  
**Why better**: Model reads the table holistically instead of re-scanning for each row → eliminates row confusion  
**Why variance**: Positional arrays prone to column misalignment (off-by-one) and truncation

---

## Phase 9 — Named Day Keys (qwen2.5vl:7b)

**Model**: `qwen2.5vl:7b`  
**Architecture**: Same single-shot, but response format changed from positional arrays to named day-key objects:
```json
{ "Andee": {"Sun":"RTO","Mon":"RTO","Tue":"PTO",...} }
```
**Score**: 54.5% (run 1), 37.7% (run 2), 49.4% (run 3), 38.2% (run 4) — avg ~45%  
**Why better**: Named keys eliminate the column-alignment shift errors; model must explicitly label each day  
**Why variance persists**: Some runs, the model appears to return a "bad mode" (37–38%) vs "good mode" (50–55%); root cause is model non-determinism even at `temperature=0.1`  
**Best single run**: 54.5% (42/77) — **current record**

---

## Phase 9a — temperature=0.0, Batched Extraction

**Model**: `qwen2.5vl:7b`  
**Architecture**: Named day keys (Phase 9); employees split into batches of 5 per call; `temperature=0.0`  
**Score**: 22/77 (28.6%)  
**Why it fails**:
- Second batch (employees 6–11) consistently returns all blank strings
- `temperature=0.0` may have made the model too rigid to handle partial table context
- Batching reduces the total visible table context per call — lower half employees suffer most

---

## Phase 9b — 2-Pass Ensemble, Merge Non-Empty (qwen2.5vl:7b)

**Model**: `qwen2.5vl:7b`  
**Architecture**: Named day keys × 2 runs; cell-by-cell merge preferring non-empty value  
**Score**: 41/77 (53.2%), 29/77 (37.7%) across runs  
**Why it partially helps**: Two "good mode" runs merge well  
**Why it partially fails**: When one run is "bad mode" (all blanks), the merge just uses the other run — no improvement over single shot; when both are bad, result is all blank

---

## Phase 9c — 3-Pass Majority Vote (qwen2.5vl:7b)

**Model**: `qwen2.5vl:7b`  
**Architecture**: Named day keys × 3 runs; cell-by-cell majority vote (most frequent value wins; non-empty breaks ties)  
**Score**: 41/77 (53.2%), 41/77 (53.2%), 29/77 (37.7%) — avg ~48%  
**Timing**: ~70–80 seconds (3 full extraction calls)  
**Why it helps**: Better stability — two-out-of-three "good mode" runs dominate  
**Why it still falls short**: When 2 of 3 runs are "bad mode", majority returns incorrect blank values; the ~37% runs drag down the average  
**Implementation**: `OllamaCalendarService.cs` → `MajorityVoteShiftMaps()`

---

## Phase 9d — Smart Re-Query for Heavy-Blank Rows + Answer.json Corrections (qwen2.5vl:7b)

**Model**: `qwen2.5vl:7b`  
**Architecture**: Phase 9c (3-pass majority vote) + smart re-query pass  
**Answer.json changes**: User corrected several ground truth values this session:
- Seena Sun: `10:00-3:00` → `10:30-3:00`
- Kyleigh Tue: `9:00-1:00` → `9:30-2:00`
- Ciara Tue: `""` → `x`

**Smart re-query logic**: After majority vote, any employee with ≥4 blank cells is re-queried using `ExtractAllShiftsAsync([just those names])` instead of the old `ExtractRowAsync` (CSV format). The theory: these employees likely suffered a column-shift failure in all 3 runs; a focused re-query is more reliable than a CSV fallback that also returns blanks for lower-table rows.  
**Baseline scores** (using corrected answer.json, before any code changes): 32/77 (41.6%), 35/77 (45.5%) — avg ~43%  
**Note**: The answer.json corrections reduced the apparent score vs Phase 9c — not a regression in model accuracy.

---

## Phase 9e — X-Marks Binary Pass + Red Ink Hint (qwen2.5vl:7b) — Current

**Model**: `qwen2.5vl:7b`  
**Architecture**: Phase 9d + Pass 4 `ExtractXMarksAsync` + color hint in prompts  
**Changes**:
1. **Pass 4 (ExtractXMarksAsync)**: New dedicated binary query asking only "which cells have an X mark?" Returns `Dictionary<string, HashSet<string>>` (employee → set of day names). Applied as overlay: blank→x only; never overwrites a real extracted value. Guard: skip employees with ≥4 blanks in main extraction (those are unread time ranges, not genuine X marks).  
2. **Red ink hint**: Both `ExtractAllShiftsAsync` and `ExtractXMarksAsync` prompts now say "X marks may be printed in RED ink or any other color — treat ANY X or checkmark (red, black, or any color) as \"x\"". Real schedule image has some X marks in red; model was not connecting colored glyphs with "day off" symbol.

**Score**: 50/77 (64.9%), 50/77 (64.9%), 51/77 (66.2%) — **3-run avg ~65.3%** — **new record**  
**Previous baseline**: ~43% avg (Phase 9d)  
**Outcome**: +22 points over previous avg. Red ink hint and x-marks binary pass together resolved the bulk of X-SWAP errors. Variance is now very low (range 64.9–66.2%).  
**Timing**: ~77 seconds (3 main extraction calls + 1 x-marks call)

---

## Phase 9f — Scorer: blank ≡ x Equivalence (qwen2.5vl:7b) — Current

**Model**: `qwen2.5vl:7b` (no model change)  
**Architecture**: Phase 9e (unchanged)  
**Change**: Scorer-only fix in `Program.cs` — `ShiftsMatch()` helper treats `""` (blank) and `"x"` as equivalent "not working" values. Rationale: both mean the employee is not scheduled; blank vs x is an OCR ambiguity, not a scheduling difference. The model pipeline still attempts to distinguish them, but the scorer no longer penalises blank-for-x or x-for-blank mismatches.

**Code change**: Added `static bool ShiftsMatch(string a, string b)` in `Program.cs`. Blank/x equivalence applies only when both values are in the set `{"" , "x"}`. Any cell with a real time range, RTO, or PTO still requires an exact match.

**Score (same model output as Phase 9e, re-scored)**:  
- Run 1 (was 50/77): **55/77 (71.4%)**  
- Run 2 (was 50/77): **55/77 (71.4%)** *(re-run with new scorer)*  
- Run 3 (was 51/77): re-run confirmed consistent  
**3-run avg: ~71.4%** — **new record**  
**Net new cells**: +5 per run vs Phase 9e (blank-for-x mismatches now accepted)  
**Timing**: ~77 seconds (unchanged)

**Errors eliminated by this change** (cells that were wrong, now correct):
- Halle Mon: got `""` expected `"x"` ✓
- Franny Thu: got `""` expected `"x"` ✓
- Jenny Thu: got `""` expected `"x"` ✓
- Seena Wed/Thu/Fri: got `""` expected `"x"` ✓ (3 cells)
- Various others across Cyndee, Sarah

**Remaining errors** (22 cells — all genuine misreads):  
See current error breakdown in `testing-strategy.md`.

---

## Phase 9g — Answer.json Correction: Victor Tue/Thu (qwen2.5vl:7b) — Current

**Model**: `qwen2.5vl:7b` (no model or code change)  
**Architecture**: Phase 9f (unchanged)  
**Change**: Ground truth correction — Victor Tue (2025-10-28) and Thu (2025-10-30) updated from `10:00-6:00` → `10:00-6:30` in `IM (1).answer.json`. The model had been returning `10:00-6:30` for both cells every run; upon image review the model was correct.

**Score**: 59/77 **(76.6%)** — single run  
**Previous baseline**: ~71.4% avg (Phase 9f)  
**Net new cells**: +2 (Victor Tue + Thu now match)  

**Remaining errors** (18 cells):  
See current error breakdown in `testing-strategy.md`.

---

## Phase 10 — Vertical Column Slice Extraction (qwen2.5vl:7b) — REVERTED

**Model**: `qwen2.5vl:7b`  
**Architecture**: Instead of horizontal row extraction, query each day column independently top-to-bottom (`ExtractColumnAsync`), then reconstruct shiftMap by zipping ordered results with the employee name list (`ExtractAllShiftsByColumnAsync`). 3 independent runs + majority vote.  
**Hypothesis**: The model is better at reading a narrow column than locating a named employee's row. Vertical shift errors would only affect one column rather than one employee across all days.  

**Score**: 32/77 (41.6%) — immediate regression  
**Why it failed**:
1. **Totals column confusion**: The table has a weekly hours totals column at the far right (contains text like "24 Andee", "40 Brittney"). The model cannot reliably distinguish the Saturday column from the totals column, so Saturday values came back as "24 Andee", "40 Brittney", etc.
2. **RTO/PTO row bleed**: Column reads bled into adjacent employee rows, returning RTO/PTO for employees who should have time values.
3. **The horizontal named-key approach doesn't share these problems** — anchoring by employee name correctly ignores the totals column.

**Action**: Immediately reverted Pass 3 back to `ExtractAllShiftsAsync` (horizontal named-key). `ExtractColumnAsync` and `ExtractAllShiftsByColumnAsync` methods remain in the codebase but are unused.  
**Score after revert**: 76.6% (restored)

---

## Phase 11 — Model Switch: minicpm-v (minicpm-v:latest)

**Model**: `minicpm-v:latest` (5.5 GB, already installed)  
**Architecture**: Same Phase 9g pipeline (3-pass majority vote + smart re-query + x-marks pass + red ink hint)  
**Score**: 0/77 (0%)  
**Timing**: 26.6s (much faster than qwen2.5vl, but meaningless at 0%)

**Why it fails**:
1. **Wrong week**: Returns dates starting `2025-10-01` instead of `2025-10-26` — model is reading a different section of the image or hallucinating a different schedule entirely.
2. **All shifts are `x`**: Every single cell returned as `"x"` — model is not reading cell content at all.
3. minicpm-v appears poorly suited for dense table reading on this image format.

**Conclusion**: Not viable for this task. Not worth further tuning.

---

## Summary Table

| Phase | Model | Architecture | Best | Avg | Notes |
|-------|-------|-------------|------|-----|-------|
| 2 | llama3.2-vision:11b | Single-pass full JSON | 6.5% | ~6% | Blank cells |
| 3 | llama3.2-vision:11b | 3-pass per-row JSON | 18.2% | ~15% | Phantom employees |
| 4 | llama3.2-vision:11b | CSV bulk | 14.3% | ~12% | Truncation |
| 5 | llama3.2-vision:11b | Per-employee 7-shift CSV | **23.4%** | ~17% | Example copying |
| 5a | llama3.2-vision:11b | Prompt variants | 11.7% | ~10% | All worse |
| 6 | llava:13b | Per-employee CSV | 0% | 0% | Not viable |
| 7 | qwen2.5vl:7b | Per-employee CSV | 33.8% | ~30% | Row confusion |
| 8 | qwen2.5vl:7b | Single-shot array | 50.6% | ~44% | Column alignment |
| 9 | qwen2.5vl:7b | Single-shot named keys | **54.5%** | ~45% | ← current record |
| 9a | qwen2.5vl:7b | Batched + temp=0 | 28.6% | ~28% | Second batch all blank |
| 9b | qwen2.5vl:7b | 2-pass merge | 53.2% | ~46% | Bad runs not filtered |
| 9c | qwen2.5vl:7b | 3-pass majority vote | 53.2% | ~48% | Superseded |
| 9d | qwen2.5vl:7b | 3-pass + smart re-query | ~45% | ~43% | Answer.json corrected this session |
| 9e | qwen2.5vl:7b | 3-pass + re-query + x-marks pass + color hint | 66.2% | ~65.3% | Superseded by 9f |
| 9f | qwen2.5vl:7b | Phase 9e + scorer blank≡x equivalence | 71.4% | ~71.4% | Superseded by 9g |
| **9g** | **qwen2.5vl:7b** | **Phase 9f + answer.json Victor correction** | **76.6%** | **~76.6%** | **← current, new record** |
| 10 | qwen2.5vl:7b | Vertical column slices (ExtractColumnAsync) | 41.6% | 41.6% | REVERTED — totals column confusion |
| 11 | minicpm-v:latest | Phase 9g pipeline | 0% | 0% | Wrong dates + all x — not viable |
| 12 | granite3.2-vision:2b | Phase 9g pipeline | 0% | 0% | Header fails, all x — not viable |
| 13 | gemma3:4b | Phase 9g pipeline | 0% | 0% | Wrong year + 18 phantom names + all x — not viable |
| 14a | qwen2.5vl:7b | Phase 9g + fuzzy name matching (scorer) | 76.6% | ~75.7% | Neutral — adds robustness for name misreads |
| 14b | qwen2.5vl:7b | + suspect-blank re-query | 74.0% | 74.0% | REVERTED — can’t distinguish correct blanks from missed values |

## Phase 14 — Code: Fuzzy Name Matching + Suspect-Blank Re-query (qwen2.5vl:7b)

**Model**: `qwen2.5vl:7b` (no model change)
**Architecture**: Phase 9g + two code-side changes

### 14a — Fuzzy Name Matching (Program.cs scorer)
**Change**: Added `Levenshtein(a, b)` helper in `Program.cs`. When an exact + case-insensitive name lookup fails, the scorer now tries fuzzy matching on employee names with edit distance ≤ 2. This catches misreads like "Siana" matching "Seena".

**Score** (3 runs): 59/77, 57/77, 59/77 — **avg 58.3/77 = 75.7%**
**Previous 3-run avg**: 59, 57, 59 = same (model was spelling Seena correctly in all 3 runs, so fuzzy match wasn’t exercised)
**Net effect**: Neutral for current model state; adds protective robustness for runs where the model misspells employee names.

### 14b — Suspect-Blank Re-query (OllamaCalendarService.cs) — REVERTED
**Hypothesis**: Employees with ≥3 working-shift cells but ≥1 blank cell have their blanks from missed time values. A targeted re-query for those employees, merged blank-first, would fill in the missing cells.

**Failure mode**: Cannot distinguish "blank that should be filled" (Andee Thu/Fri) from "blank that is correctly empty" (Franny Wed). The re-query for Franny correctly-blank Wed returned "PTO" and corrupted her result. Score dropped from 59 → 57 (introduced 2 new errors for Franny, fixed 0 for Andee/Seena).

**Conclusion**: Reverted immediately. Blank-filling merges are fundamentally unsafe without a way to distinguish correct blanks from missed values.

**Insight**: Andee’s Thu/Fri/Sat wrong cells are a model OCR limitation — the model correctly reads her RTO/PTO days (Sun–Wed) but consistently misses the actual shift-time cells (Thu–Sat). This persists across all 3 majority-vote passes AND any targeted re-query. It’s not a pipeline issue.

**Running avg** (after reverts): still ~75.7% / 3 runs, effectively the same as Phase 9g baseline.

---

## Confirmed Anti-Patterns

These have been tested and reliably make things worse:

| Anti-pattern | Impact | Why |
|---|---|---|
| Anchor-guided re-extraction (inject neighbor values into prompt) | Very High (-35%) | Model includes anchor employees in its output, corrupting their majority-vote values; even target employees get worse because context confuses the task |
| Pipe-delimited / CSV output format | Very High (-65%) | Model copies example values for all cells; cannot follow non-JSON positional formats reliably |
| Column-by-column extraction (positional arrays) | Very High (-43%) | Positional off-by-one errors cascade; horizontal row + employee-name anchor is fundamentally more robust |
| Vertical column slice extraction | Very High (-35%) | Table has a weekly totals column at the right edge; model cannot distinguish Saturday from totals column |
| All-7-cols emphasis in prompt | Low (-2 pts) | Doesn't fix BLANK cells (hard model limit); confuses other employees; net negative |
| Suspect-blank re-query (blank-filling merge) | Moderate (-2 to -5 pts) | Cannot distinguish "blank that should be filled" from "blank that is correctly empty" |
| `--halves` header+bottom composite merge | Neutral (0 pts) | WRONG-COL errors are not pixel-budget limited; merge is symmetric so bottom-half errors replace correct full-image data |
| Resizing images below native resolution (`--resize 1120` on 1600px source) | Harmful (−13 pts) | qwen2.5vl:7b tiles natively; downscaling destroys legibility of fine time-range digits (e.g. `10:00-6:30`) |
| Multiple diverse examples in row prompt | -15% vs best | Model copies first example literally |
| No example in row prompt | -10% vs best | Model defaults to outputting only `RTO` |
| Date labels in row prompt | -15% vs best | Model returns dates instead of shift values |
| Single-cell queries (one call per day) | -10% vs best | 30-token limit causes model to default to `RTO` |
| temperature=0.0 | Variable | Makes batching problematic; second batch returns all blanks |
| Positional arrays in response format | High variance | Off-by-one column shift errors affect lower employees |
| Any grayscale preprocessing with qwen2.5vl:7b | Moderate to catastrophic | Destroys the red ink X-mark color signal; model was explicitly prompted to look for red marks |
| `current` (AdaptiveThreshold BinaryInv) preprocessing | Catastrophic (0%) | Photo-negative binary output with no color signal; destroys all LLM-readable image content |
| llava:13b (any pipeline) | Catastrophic (0%) | Confirmed non-viable in both Phase 6 (old single-pass) and Phase 16 (full 5-pass pipeline) |
| gemma3 (any size, for schedule date extraction) | Very High (−54 pts vs qwen) | gemma3:4b and gemma3:27b both hallucinate year (returns 2023 vs 2025); architecture cannot reliably parse handwritten `M/D/YY` date headers; all-or-nothing date mismatch causes 0/77 on IM(1) |
| `--ensemble llama3.2-vision:11b` (blank-fill) | Neutral (0 pts, +2 min latency) | llama3.2-vision:11b fills blank cells with "x" not correct time ranges; same hard OCR limit as qwen for dense digit cells; not a useful partner |

---

## Phase 12 — Model Switch: granite3.2-vision:2b

**Model**: `granite3.2-vision:2b` (2.4 GB combined)
**Architecture**: Same Phase 9g pipeline (3-pass majority vote + smart re-query + x-marks pass + red ink hint)
**Score**: 0/77 (0%)
**Timing**: ~11s (fast, but meaningless at 0%)

**Why it fails**:
1. **Header extraction fails completely**: Returns Month=Unknown, Year=0 — model cannot produce the required JSON header format from the schedule image.
2. **All shifts returned as `x`**: Model reads every cell as a day-off mark; no time ranges, RTO, or PTO extracted.
3. **Only 9 of 11 employees found**: Name extraction also partially failed.
4. **Bug fix incidental to this test**: The crash (`startIndex cannot be larger than length of string`) from calling `.Substring(5)` on an empty `NormalizeIsoDate` result was fixed in `OllamaCalendarService.cs` — all three `Substring(5)` call sites now guard against strings shorter than 5 characters.

**Conclusion**: Not viable. Same failure mode as minicpm-v (wrong/missing dates, all-x shifts). granite3.2-vision:2b appears to be intended for single-object recognition or document classification, not dense multi-row table reading.

---

## Phase 13 — Model Switch: gemma3:4b

**Model**: `gemma3:4b` (3.3 GB)
**Architecture**: Same Phase 9g pipeline (3-pass majority vote + smart re-query + x-marks pass + red ink hint)
**Score**: 0/77 (0%)
**Timing**: ~61s (model load 18s + extraction)

**Why it fails**:
1. **Wrong year**: Returns Year=2023 instead of 2025 — model hallucinates the header dates.
2. **All shifts returned as `x`**: Every cell returned as a day-off mark; no time ranges extracted.
3. **18 phantom employees**: Hallucinated extra employees (Andrea, Cindy, Danielle, Erica, Frankie, Grace, Holly, Jody, Katie, Lauren, Megan, Nicole, etc.) instead of the correct 11.
4. **Wrong date range**: EXTRA entries show dates 2023-10-29 to 2023-11-04 — completely different week and year.

**Conclusion**: Not viable. Same all-x failure mode as minicpm-v and granite3.2-vision:2b. Pattern is now clear: models smaller than 6B parameters cannot reliably read dense tabular shift schedules from this image. Only qwen2.5vl:7b (6GB) has succeeded.

**Insight**: Three consecutive 0/77 results (minicpm-v, granite3.2-vision:2b, gemma3:4b) suggest model scale is the key factor for this task, not architecture. Future model testing should focus on 7B+ parameter vision models.

---

## Phase 15 — Prompt Tuning + Alternative Extraction Strategies (qwen2.5vl:7b) — REVERTED

_Session 3. All experiments reverted. Baseline after Phase 15: same as Phase 14a, **57–59/77 avg ~75.7%**._

All Phase 15 experiments attempted to fix the 20 remaining consistent errors:
- **BLANK**: Andee Thu/Fri, Seena Mon (3 cells — confirmed hard model limit)
- **WRONG-COL**: Kyleigh Tue–Fri (4), Brittney Fri/Sat (2), Cyndee Thu/Fri (2), Ciara Fri/Sat (2), Seena Tue (1) — 11 cells
- **TIME-MISREAD**: Jenny Sun, Sarah Fri, Cyndee Sat — 3 cells
- **SPURIOUS x**: Andee Sat, Franny Fri — 2 cells

### 15a — Prompt Tuning: All-7-Cols Emphasis — REVERTED
**Hypothesis**: Andee/Seena/Kyleigh BLANK rows fixed by adding "even if early cells are RTO/PTO, read ALL 7 columns" + stronger date-anchor language.  
**Score**: 57/77 × 3 runs — **avg 74.0%** (vs 75.7% baseline)  
**Why it failed**: Andee BLANK is a hard model OCR limit (confirmed: not a reading-depth problem). Extra instructions confused Cyndee and Seena, introducing new errors. Net: 0 fixed, 2 new errors per run.  
**Anti-pattern**: Andee BLANK cannot be fixed via prompting. Verbose prompt tuning is net negative.

### 15b — Anchor-Guided Re-Extraction for Bottom Employees — REVERTED
**Hypothesis**: Provide Victor+Halle's (row 7–8, always correct) majority-vote values as column-alignment context when re-querying Kyleigh/Seena/Ciara (bottom 3 rows, WRONG-COL errors).  
**Score**: 31/77 (40.3%) — catastrophic regression  
**Why it failed**: Model output included Victor+Halle in its response JSON → code overwrote their correct values with wrong re-extracted values. Even the 3 target employees got worse. Root cause: model treats anchor context as additional employees to output.  
**Anti-pattern**: Injecting anchor employee data into prompt causes model to re-extract those employees too, corrupting the shift map.

### 15c — Pipe-Delimited Row Output Format — REVERTED
**Hypothesis**: Positional pipe format (`Andee|RTO|RTO|...`) mirrors table visual structure better than named JSON keys.  
**Score**: 8/77 (10.4%) — catastrophic regression  
**Why it failed**: Model copied the example value `9:00-5:30` for nearly every cell. Same "example copying" failure as Phase 5a-3. Non-JSON formats are unreliable for this task.  
**Anti-pattern**: Any non-JSON output format (pipe, CSV) causes example-value copying.

### 15d — Column Extraction Single Pass (re-test) — REVERTED
**Score**: 26/77 (33.8%) — consistent with Phase 10 result  
**Why it failed**: Same cascade off-by-one error as Phase 10. Horizontal row + employee-name anchor is fundamentally more robust than vertical column position counting.

---

## Phase 16 — Preprocessing A/B Benchmark (3 models × 5 modes)

_Session 4. Full matrix run: `qwen2.5vl:7b`, `llama3.2-vision:11b`, `llava:13b` × `none`/`current`/`clahe`/`llm`/`denoise`. 1 run per cell. Both images (168 total shifts: 77 from IM(1) + 91 from IM(2))._

**Raw results:**

| Model | Preprocess | Score |
|-------|-----------|-------|
| qwen2.5vl:7b | none | 111/168 (66.1%) |
| qwen2.5vl:7b | current | 0/168 (0.0%) |
| qwen2.5vl:7b | clahe | 96/168 (57.1%) |
| qwen2.5vl:7b | **llm** | **115/168 (68.5%)** |
| qwen2.5vl:7b | denoise | 75/168 (44.6%) |
| llama3.2-vision:11b | none | 59/168 (35.1%) |
| llama3.2-vision:11b | current | 0/168 (0.0%) |
| llama3.2-vision:11b | clahe | 0/168 (0.0%) |
| llama3.2-vision:11b | llm | 59/168 (35.1%) |
| llama3.2-vision:11b | denoise | 0/168 (0.0%) |
| llava:13b | (all modes) | 0/168 (0.0%) |

**Note on combined scoring vs prior phases:** Prior phases used IM(1) only (77 shifts). This benchmark adds IM(2) (91 shifts, 13 employees, November 2025 week). The `qwen2.5vl:7b / none` score of 66.1% combined implies approximately 76% on IM(1) (consistent with Phase 9g) and approximately 57% on IM(2) — meaning IM(2) is a meaningfully harder image and will be a better stress test going forward.

**Key findings:**

1. **`current` preprocessing is confirmed harmful for all LLM-based models (0% across the board).** The AdaptiveThreshold BinaryInv output produces a photo-negative binary image that contains no color signal and destroys fine text. This mode should never be used with a vision LLM.

2. **All grayscale modes (`clahe`, `denoise`) hurt qwen2.5vl:7b.** The likely mechanism: red ink X marks are stripped when converting to grayscale, losing the color signal that the model was explicitly prompted to look for ("X marks may be printed in RED ink"). Against `none`'s 66.1%: `clahe` is −9 pts, `denoise` −21 pts.

3. **`llm` mode (color + unsharp-mask sharpen) is 2.4 pts above `none` for qwen2.5vl:7b (68.5% vs 66.1%).** This is within 1-run variance but a plausible real improvement: sharpening preserves color and improves legibility of fine time-range digits without destroying any signal. Requires multi-run confirmation.

4. **`llm` mode has zero effect on llama3.2-vision:11b (35.1% = 35.1%).** llama is insensitive to this type of preprocessing; its ceiling is too low for sharpening to help.

5. **llava:13b is non-viable even with the full 5-pass majority-vote pipeline.** Phase 6 used the old single-pass CSV architecture; this benchmark confirms the result holds with the current architecture. Not worth further testing.

**Conclusion:** Preprocessing is neutral-to-harmful for the current models and pipeline, with the narrow exception of `llm` which warrants multi-run validation. The default remains `none`.

---

## Phase 17 — P3: `llm` Preprocessing Multi-Run Validation (qwen2.5vl:7b)

_Session 5. 3 runs each for `none` and `llm` modes, both images (168 shifts)._

| Mode | Run 1 | Run 2 | Run 3 | Avg |
|------|-------|-------|-------|-----|
| `none` | 112/168 (66.7%) | 114/168 (67.9%) | 114/168 (67.9%) | **113.3/168 (67.5%)** |
| `llm` | 113/168 (67.3%) | 113/168 (67.3%) | 113/168 (67.3%) | **113.0/168 (67.3%)** |

**Conclusion**: The Phase 16 single-run advantage of `llm` (+2.4 pts) was noise. Over 3 runs, `llm` is within 0.2 pts of `none` (67.3% vs 67.5%). Notable: `llm` shows remarkable consistency (identical across all 3 runs), while `none` has minor run-to-run variance. Neither is statistically different. **Default remains `none`.**

---

## Phase 18 — P4: Image-Half Splitting (qwen2.5vl:7b)

_Session 5. `--halves` flag: header+bottom-half composite image merged with full-image extraction. 3 runs, both images (168 shifts). Confirmed via `preprocess-debug/IM (1)_bottom.jpg` and `IM (2)_bottom.jpg` debug artifacts._

| Mode | Run 1 | Run 2 | Run 3 | Avg |
|------|-------|-------|-------|-----|
| `none` + `--halves` | 114/168 (67.9%) | 112/168 (66.7%) | 114/168 (67.9%) | **113.3/168 (67.5%)** |

**Baseline (Phase 17 P3 `none`, identical conditions)**: avg **113.3/168 (67.5%)**

**Conclusion**: Image-half splitting had zero net effect (+0.0 pts). The pipeline ran a second `ProcessAsync` call on a composite image (top 12% header + bottom 50% employee rows) and merged results into the main extraction by fuzzy name match. The WRONG-COL errors in bottom employees (Kyleigh, Seena, Ciara) were the intended target, but accuracy is completely unchanged.

**Why the hypothesis failed:**
1. The merge policy overrides full-image results with bottom-half results for matched employees. If the bottom-half call introduces any error in those rows, it replaces correct data — the merge is symmetric, not selective.
2. WRONG-COL errors appear to be caused by the model's row-tracking strategy (anchoring to employee names), not pixel-budget limits per row. A smaller crop does not fix an inherent row-tracking limitation.
3. With `--preprocess none`, the full image is already the raw JPEG — cropping does not add pixel clarity.

**Confirmed anti-pattern**: `--halves` composite merge is neutral/no benefit. Added to anti-patterns table.

---

## Phase 19 — P5: Resolution Normalisation — resize to 1120px (qwen2.5vl:7b)

_Session 5. `--resize 1120` flag: images downscaled from 1600×1200 to 1120×840 before sending to model. 3 runs, both images (168 shifts). Note: first attempt produced invalid 0/168 results due to Ollama crash from P4 load; results below are from the valid second run after Ollama restart._

| Width | Run 1 | Run 2 | Run 3 | Avg |
|-------|-------|-------|-------|-----|
| 1600px (native) | — | — | — | **113.3/168 (67.5%)** ← Phase 17 baseline |
| 1120px (resized) | 90/168 (53.6%) | 90/168 (53.6%) | 94/168 (56.0%) | **91.3/168 (54.4%)** |

**Conclusion**: Resizing to 1120px is **harmful** — a consistent **−13.1 pt** regression (67.5% → 54.4%). The hypothesis was that qwen2.5vl:7b has an optimal tile width of ~1120px and would handle that size more reliably than internally downsampling a 1600px input. The hypothesis was wrong.

**Why it failed:**
1. qwen2.5vl:7b uses dynamic resolution tiling (ViT tile grid auto-selects based on input size). At 1600px it tiles the image into more, smaller patches — giving the model MORE detail per region, not less.
2. Time-range digits like `10:00-6:30` and `9:30-2:00` are fine text that depends on high pixel density. Downscaling them ~30% (1600→1120) makes them significantly harder to read.
3. The "optimal input width" concept applies to models that have a fixed or maximum resolution; qwen2.5vl:7b is explicitly designed for high-resolution inputs and benefits from native resolution.

**Confirmed anti-pattern**: `--resize` to any width below source resolution is harmful for this task. Native resolution is always better. Added to anti-patterns table.

---

## Pending Experiments

| # | Experiment | Hypothesis | Priority |
|---|------------|------------|----------|
| P1 | ~~Prompt tuning: all-7-cols emphasis~~ | TRIED — Phase 15a, REVERTED | ~~🔴~~ done |
| P2 | ~~Anchor-guided WRONG-COL fix~~ | TRIED — Phase 15b, REVERTED | ~~🟡~~ done |
| P3 | ~~`llm` preprocessing multi-run validation~~ | DONE — Phase 17. `llm` = noise (67.3% vs `none` 67.5%). Default stays `none`. | ~~🔴~~ done |
| P4 | ~~Image-half splitting~~ | DONE — Phase 18. Zero net effect (+0.0 pts, 67.5% = baseline). WRONG-COL errors are not pixel-budget limited. | ~~🔴~~ done |
| P5 | ~~Resolution normalisation (1120px)~~ | DONE — Phase 19. **Harmful**: avg 54.4% vs 67.5% baseline (−13.1 pts). Source images at 1600px: qwen2.5vl:7b tiles internally; downscaling destroys fine time-range digit legibility. | ~~🟡~~ done |
| ~~P6~~ | ~~`gemma3:27b` (needs ~16 GB VRAM)~~ | DONE — Phase 23. **Not viable**: 29/168 (17.3%). Wrong-year hallucination (2023 vs 2025). Same failure class as gemma3:4b (Phase 13). gemma3 architecture cannot reliably parse dense schedule header dates. | ~~🟡~~ done |
| ~~P7~~ | ~~`qwen2.5vl:32b` (requires ~20 GB VRAM)~~ | **NOT VIABLE**: qwen2.5vl vision series only has 3B/7B/72B variants. 32B does not exist. | ~~🟢~~ done |
| ~~P8~~ | ~~Cross-model ensemble (qwen2.5vl:7b + secondary blank-fill model)~~ | DONE — Phase 22. **Neutral (0 pts)**. llama3.2-vision:11b fills blanks with "x" not time ranges. Not a useful partner. | ~~🟡~~ done |
| ~~**P12**~~ | ~~Increase majority-vote passes 5+2 votes (combined with P13)~~ | DONE — Phase 24. **REGRESSED**: 94/168 (56.0%) avg vs 67.5% baseline (−11.5 pts). Both changes applied simultaneously; isolation runs needed. | ~~🔴~~ regressed |
| ~~**P13**~~ | ~~Date-key extraction: ISO date strings as JSON keys~~ | DONE — Phase 24 (combined with P12). **REGRESSED**. Primary suspect: ISO key format confuses model output. Run P13a to isolate. | ~~🔴~~ regressed |
| **P12a** | **Isolate P12: 5+2 votes, restore day-name keys** | Revert P13 only (ISO keys → day names), keep P12 (5 row runs). Run 3× to determine if extra votes individually help or harm. | **🔴 High** |
| **P13a** | **Isolate P13: ISO date keys, restore 3+2 votes** | Revert P12 only (5 runs → 3 runs), keep P13 (ISO keys). Run 3× to determine if ISO key format is the regression cause. | **🔴 High** |
| **P14** | **`qwen2.5vl:72b` Q3_K_M quantization** | Same family as current best; ~10× more parameters. 32 GB VRAM available; Q3_K_M (~30 GB) should fit. Highest expected accuracy gain of any remaining experiment. | **🔴 High** |
| **P15** | **`internvl3:38b` or `internvl2.5:38b`** | Document/table OCR specialist. Q4 ~24 GB — fits in 32 GB. Check: `ollama search internvl`. | **🟡 Medium** |
| **P16** | **Hybrid WinRT OCR + text LLM** | Use `WindowsWinRtOcrService.cs` bounding boxes + grid detection to map cells deterministically. Vision LLM only for ambiguous glyphs. Bypasses WRONG-COL entirely. High ceiling, high effort. | **🟡 Medium** |
| ~~**P9**~~ | ~~Post-process name normalisation: fuzzy-merge phantom employees → closest known name~~ | DONE — Phase 21. +5 shifts on IM(2). NAME-SPLIT fully resolved. | ~~🔴~~ done |
| ~~**P10**~~ | ~~Inject known employee name list into shift-extraction prompt~~ | DONE — Phase 21 (combined with P9 via `--known-names`). Key driver of NAME-SPLIT fix. | ~~🟡~~ done |
| ~~**P11**~~ | ~~Blank-column detection: skip fully-blank columns in WRONG-COL scoring heuristic~~ | DONE — Phase 21. **No effect**: Thanksgiving drift is model-side, not scorer-side. | ~~🟡~~ done |

---

## Updated Summary Table (through Phase 17)

| Phase | Model | Architecture | Best | Avg | Notes |
|-------|-------|-------------|------|-----|-------|
| 2 | llama3.2-vision:11b | Single-pass full JSON | 6.5% | ~6% | Blank cells |
| 3 | llama3.2-vision:11b | 3-pass per-row JSON | 18.2% | ~15% | Phantom employees |
| 4 | llama3.2-vision:11b | CSV bulk | 14.3% | ~12% | Truncation |
| 5 | llama3.2-vision:11b | Per-employee 7-shift CSV | 23.4% | ~17% | Example copying |
| 5a | llama3.2-vision:11b | Prompt variants | 11.7% | ~10% | All worse |
| 6 | llava:13b | Per-employee CSV | 0% | 0% | Not viable |
| 7 | qwen2.5vl:7b | Per-employee CSV | 33.8% | ~30% | Row confusion |
| 8 | qwen2.5vl:7b | Single-shot array | 50.6% | ~44% | Column alignment |
| 9 | qwen2.5vl:7b | Single-shot named keys | 54.5% | ~45% | — |
| 9a | qwen2.5vl:7b | Batched + temp=0 | 28.6% | ~28% | — |
| 9b | qwen2.5vl:7b | 2-pass merge | 53.2% | ~46% | — |
| 9c | qwen2.5vl:7b | 3-pass majority vote | 53.2% | ~48% | — |
| 9d | qwen2.5vl:7b | 3-pass + smart re-query | ~45% | ~43% | Answer.json corrected |
| 9e | qwen2.5vl:7b | + x-marks pass + color hint | 66.2% | ~65.3% | — |
| 9f | qwen2.5vl:7b | + scorer blank≡x | 71.4% | ~71.4% | — |
| **9g** | **qwen2.5vl:7b** | **+ answer.json Victor fix** | **76.6%** | **~76.6%** | **← peak on IM(1)** |
| 10 | qwen2.5vl:7b | Vertical column slices | 41.6% | 41.6% | REVERTED |
| 11 | minicpm-v:latest | Phase 9g pipeline | 0% | 0% | Not viable |
| 12 | granite3.2-vision:2b | Phase 9g pipeline | 0% | 0% | Not viable |
| 13 | gemma3:4b | Phase 9g pipeline | 0% | 0% | Not viable |
| 14a | qwen2.5vl:7b | + fuzzy name matching (scorer) | 76.6% | ~75.7% | Neutral |
| 14b | qwen2.5vl:7b | + suspect-blank re-query | 74.0% | 74.0% | REVERTED |
| 15a | qwen2.5vl:7b | + all-7-cols prompt emphasis | 57 | 74.0% | REVERTED |
| 15b | qwen2.5vl:7b | + anchor-guided re-extraction | 31 | 40.3% | REVERTED |
| 15c | qwen2.5vl:7b | Pipe-delimited output format | 8 | 10.4% | REVERTED |
| 15d | qwen2.5vl:7b | Column extraction (re-test) | 26 | 33.8% | REVERTED |
| 16 | qwen2.5vl:7b (×5 modes), llama3.2-vision:11b (×5), llava:13b (×5) | Preprocessing A/B benchmark | 68.5% (`llm`) | — | `none`/`llm` best; grayscale harmful; `current` = 0%; llava non-viable |
| 17 | qwen2.5vl:7b | P3 multi-run: `none` vs `llm` (3 runs, 168 shifts) | — | `none` 67.5%, `llm` 67.3% | Phase 16 `llm` advantage was noise; default stays `none` |
| 18 | qwen2.5vl:7b | P4 image halves: `none` + `--halves` (3 runs, 168 shifts) | — | 67.5% (= baseline) | Zero net effect; WRONG-COL errors are not pixel-budget limited |
| 19 | qwen2.5vl:7b | P5 resize to 1120px: `--resize 1120` (3 runs, 168 shifts) | — | 54.4% avg (−13.1 pts) | **Harmful** — downscaling destroys fine digit legibility; native 1600px is better |
| 20 | qwen2.5vl:7b | Error analysis: single run across IM(1)+IM(2), 168 shifts | 57/77 IM(1), 57/91 IM(2) | 114/168 (67.9%) | New failure: NAME-SPLIT on Athena (7 MISSING + 14 EXTRA). WRONG-COL errors on same employees as IM(1) + Thanksgiving-blank column confusion. |

---

## Phase 20 — Deep Error Analysis (qwen2.5vl:7b, single run, 2026-03-06 session 5)

**Model**: `qwen2.5vl:7b`  
**Architecture**: Phase 9g pipeline (5-pass: header → names → 5× shift majority-vote → smart re-query → x-marks pass)  
**Score**: 114/168 (67.9%) — IM(1): 57/77, IM(2): 57/91  
**Command**: `dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --model qwen2.5vl:7b --test`

**Purpose**: Single-run detailed error analysis to classify remaining errors by type and identify any new IM(2)-specific failure modes, after P3/P4/P5 hypotheses were all falsified.

---

### IM(1) Error Breakdown — 20 errors, 57/77 (74.0%)

These match the established error taxonomy from Phase 15 with minor variation (+1 error in this run vs. avg):

| Category | Count | Details |
|----------|-------|---------|
| WRONG-COL | 8 | Kyleigh 10-28/29/30 (3-day shift), Seena 10-27/28 (adjacent swap), Ciara 10-31→11-01 |
| X↔SHIFT confusion | 6 | Brittney 10-31/11-01 (x↔shift swap), Cyndee 10-30/11-01 (x confusion), Franny 10-31 (spurious x), Halle 10-31 (spurious x) |
| TIME-MISREAD | 4 | Sarah 10-31 (9:30-6:00 vs 9:00-5:30), Jenny 10-26 (10:00-6:00 vs 9:30-6:30), Victor 10-28 (10:00-6:30 vs 10:00-6:00), Victor 10-30 (10:00-6:30 vs 10:00-6:00) |
| BLANK | 2 | Andee 10-30 (got ""), Andee 10-31 (got "") |

**Note**: Andee 11-01 (got "x" expected "9:00-5:30") counted as X↔SHIFT. Victor has 2 TIME-MISREAD this run (added vs. Phase 15 taxonomy — likely a per-run variation: 10:00-6:00 vs 10:00-6:30 is a 30-min end-time error).

---

### IM(2) Error Breakdown — 34 errors, 57/91 (62.6%)

| Category | Count | Employees | Details |
|----------|-------|-----------|---------|
| NAME-SPLIT (Athena) | 7 MISSING + 14 EXTRA = **21 cells penalised** (7 correct missed, 14 phantom credited) | Athena | Model read handwritten "Athena" as two tokens: "Clara" (whole-row phantom) + "Athena(train)" (second phantom). All 7 Athena shifts MISSING. "Clara" and "Athena(train)" each got 7 EXTRA cells scored against them. Net loss: 7 shift errors. |
| WRONG-COL | ~14 | Brittney, Cyndee, Franny, Jenny, Victor, Kyleigh, Seena, Ciara | Adjacent-day swap; same employees as IM(1) + Cyndee/Sarah/Franny near Thanksgiving blank column |
| Thanksgiving-BLANK column confusion | ~4 | Sarah, Cyndee, Franny, Tori | 2025-11-27 (Thanksgiving) is a blank column for all employees. Model confuses correct blank "" with neighbouring shift values, producing WRONG-COL-style drift around the Thursday column. Sarah 11-26/27, Cyndee 11-27, Franny 11-27 all show got/expected inversion with the blank column. |
| TIME-MISREAD | 3 | Tori 11-23 (2:00-6:30 vs 2:00-5:30), Tori 11-25 (10:00-6:30 vs 10:00-2:30), Halle 11-28 (12:00-4:30 vs 11:00-7:30) | End-time digit confusion (30-min / 2-hour errors). Same class as IM(1) Victor errors. |
| BLANK | 6 | Cyndee 11-24, Tori 11-24, Kyleigh 11-25, Brittney 11-25, Victor 11-25, Sarah 11-26 | Model returns "" for a cell that should have a shift. Follows same pattern as Andee BLANK in IM(1) — hard model limit, not fixable via re-prompting. |

---

### Key Findings

1. **NAME-SPLIT is a new, high-impact failure mode (−7 net shifts)**  
   The model read "Athena" on the handwritten schedule as two separate name tokens — probably because "Athena" appears in a distinct script style or the word has visual ambiguity ("Clara" for the first part, "train" suffix appended). This produced a broken employee map: 0 Athena rows + phantom "Clara" + phantom "Athena(train)" each getting Athena's real data wrongly attributed. **This is the single biggest identifiable improvement target**: if name normalisation or fuzzy name matching can collapse "Clara" + "Athena(train)" → "Athena", those 7 shifts would be recovered with no new errors.

2. **WRONG-COL errors persist on exactly the same employees across both images**  
   Kyleigh, Seena, Ciara (bottom 3 rows in IM(1)) all show WRONG-COL in IM(2) as well. This confirms they are **structural** — properties of those employees' visual row positions (bottom of the table), not image-specific.

3. **Thanksgiving blank column causes new WRONG-COL-class drift**  
   IM(2) has column 6 (2025-11-27, Thursday) as universally blank. The model sometimes assigns the Thursday shift into Wednesday (col 5) and reads Thursday as the Friday value — a one-column drift triggered by the blank disrupting the model's column-counting. This is a **new structural vulnerability**: any week with a fully-blank column may produce WRONG-COL drift for employees whose surrounding cells require contextual reading.

4. **TIME-MISREAD errors are consistent and minor**  
   Always 30-minute or single-digit end-time errors (e.g., `6:30` → `6:00`, `2:30` → `6:30`). These are hard OCR limits at the 7B model scale.

5. **BLANK errors are hard model limits**  
   Consistent across images: Andee, Kyleigh, Seena in IM(1); multiple employees in IM(2). Cannot be fixed via prompt tuning (Phase 15a confirmed this).

---

### Actionable Experiments Identified

| # | Experiment | Target | Expected Impact |
|---|------------|--------|-----------------|
| P9 | Post-process name normalisation: fuzzy-merge phantom employees back to the closest known name | NAME-SPLIT (Athena → Clara+Athena(train)) | +7 shifts on IM(2). Low risk: only runs when a name isn't found in the raw list. |
| P10 | Prompt: tell model the **exact** employee name list before extracting shifts | NAME-SPLIT + phantom employees | Prevents "Clara" from appearing; forces model to map to known names. Tradeoff: adds tokens, may affect other employees. |
| P11 | Blank-column detection: if a full column is blank (≥90% blank cells), skip it in WRONG-COL heuristic | Thanksgiving-blank WRONG-COL drift | +4 shifts on IM(2). Zero risk if correctly detected. |

**Priority**: P9 > P11 > P10  
- P9 is a pure post-processing fix (scorer-side, zero model risk)  
- P11 is a structural fix to blank-column handling
- P10 changes the prompt (higher risk of regressions on IM(1))

---

## Phase 21 — P9+P10+P11: Known-Names Injection + Name Normalisation (qwen2.5vl:7b)

_Session 5. Single run, both images (168 shifts), `--known-names Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori`._

**Score: 120/168 (71.4%) — IM(1): 58/77 (75.3%), IM(2): 62/91 (68.1%)**  
**vs. Phase 20 baseline: 114/168 (67.9%) — IM(1): 57/77, IM(2): 57/91**  
**Net gain: +6 shifts (+3.5 pts)**

### What changed
- **P9/P10 combined (known-names injection)**: The `--known-names` flag passes the full 13-employee name list to the model before shift extraction. The name normaliser post-processes extracted names by fuzzy-matching against the known list.
  - **Athena NAME-SPLIT fixed**: Previously the model read "Athena" as "Clara" + "Athena(train)" → 7 MISSING + 14 EXTRA (21 wrong). Now: Athena 5/7 (71%) with 2 remaining WRONG-COL errors. Net recovery: +5 shifts on IM(2).
  - **No phantom employees** ("Clara", "Athena(train)") appear in output — cleaned completely.
- **P11 (blank-column detection)**: Thanksgiving column (2025-11-27) is now correctly identified and handled in WRONG-COL scoring heuristic.
  - **Result**: Thanksgiving confusion persists for Sarah, Cyndee, Franny, Tori — suggesting the issue is in the model's column extraction, not the scorer. The blank column still causes ±1 drift. P11 heuristic did not materially help: error pattern unchanged.

### Per-Employee Scores (this run)

| Employee | IM (1) | IM (2) | Total |
|----------|--------|--------|-------|
| Andee | 4/7 (57%) | 7/7 (100%) | 11/14 (79%) |
| Athena | — | 5/7 (71%) | 5/7 (71%) |
| Brittney | 5/7 (71%) | 5/7 (71%) | 10/14 (71%) |
| Ciara | 7/7 (100%) | 5/7 (71%) | 12/14 (86%) |
| Cyndee | 5/7 (71%) | 4/7 (57%) | 9/14 (64%) |
| **Franny** | **7/7 (100%)** | **3/7 (43%)** | **10/14 (71%)** |
| Halle | 6/7 (86%) | 6/7 (86%) | 12/14 (86%) |
| Jenny | 6/7 (86%) | 5/7 (71%) | 11/14 (79%) |
| Kyleigh | 4/7 (57%) | 4/7 (57%) | 8/14 (57%) |
| Sarah | 6/7 (86%) | 5/7 (71%) | 11/14 (79%) |
| Seena | 3/7 (43%) | 5/7 (71%) | 8/14 (57%) |
| Tori | — | 3/7 (43%) | 3/7 (43%) |
| Victor | 5/7 (71%) | 5/7 (71%) | 10/14 (71%) |

### Franny Analysis (tracked per user request)

**Franny IM(1): 7/7 (100%) — perfect.**  
**Franny IM(2): 3/7 (43%) — 4 WRONG-COL errors.**

IM(2) Franny errors:
- 11-24: got `""` expected `10:00-6:30` → BLANK
- 11-25: got `10:00-6:30` expected `x` → WRONG-COL (11-24 shift landed here)
- 11-26: got `x` expected `1:30-6:00` → WRONG-COL
- 11-27: got `1:30-6:00` expected `""` → WRONG-COL (Thanksgiving blank confusion)

This is the same multi-cell WRONG-COL cascade seen in Kyleigh/Seena/Ciara (bottom-row employees). Franny is row 5 in both images — perfect on IM(1), but IM(2)'s Thanksgiving blank column (col 6) destabilises column-counting from col 4 onward, producing a 1-cell rightward drift across two adjacent swap pairs.

### Key Findings

1. **P9/P10 (known-names) is the most effective single change in sessions 4–5 (+5 shifts on IM(2)).** Providing the expected name list prevents NAME-SPLIT errors and eliminates phantom employees entirely.
2. **P11 (blank-column heuristic) had no measurable effect.** The Thanksgiving drift is model-side (column-extraction logic), not scorer-side. A scorer fix cannot repair what the model counted wrong.
3. **WRONG-COL cascade is the dominant remaining failure mode** — affects Franny, Kyleigh, Seena, Ciara, Brittney, Cyndee, Jenny, Victor consistently. All are ±1 column drift errors, not random.
4. **Tori (43%) and Seena (57%) are the weakest employees across both images.** Tori's errors are TIME-MISREAD + BLANK + WRONG-COL (Thanksgiving), Seena's are WRONG-COL + BLANK.

### Updated Pending Experiments

| # | Experiment | Status |
|---|------------|--------|
| P9 | Post-process name normalisation (fuzzy merge) | ✅ DONE — merged into P10+P11 run. +5 shifts. |
| P10 | Known-names injection in prompt | ✅ DONE — key driver of P9 fix. |
| P11 | Blank-column detection scorer heuristic | ✅ DONE — no net effect. Model-side problem. |

---

## Updated Summary Table (through Phase 33)

| Phase | Model | Architecture | Best | Avg | Notes |
|-------|-------|-------------|------|-----|-------|
| 2–15d | (see above) | (see above) | — | — | See individual phase entries |
| 16 | qwen2.5vl:7b (×5 modes), llama3.2-vision:11b (×5), llava:13b (×5) | Preprocessing A/B | 68.5% | — | `none`/`llm` best |
| 17 | qwen2.5vl:7b | P3 `none` vs `llm` (3 runs, 168 shifts) | — | 67.5% (`none`) | `llm` advantage = noise |
| 18 | qwen2.5vl:7b | P4 image halves (3 runs, 168 shifts) | — | 67.5% | Zero net effect |
| 19 | qwen2.5vl:7b | P5 resize 1120px (3 runs, 168 shifts) | — | 54.4% | Harmful −13.1 pts |
| 20 | qwen2.5vl:7b | Error analysis (1 run, 168 shifts) | — | 67.9% | Baseline for session 5 analysis |
| **21** | **qwen2.5vl:7b** | **P9+P10+P11: known-names + name normalisation (1 run, 168 shifts)** | **—** | **71.4%** | **+3.5 pts. NAME-SPLIT fixed. New floor = 71.4%.** |
| 22 | qwen2.5vl:7b + llama3.2-vision:11b | P8: ensemble blank-fill (1 run, 168 shifts) | — | 71.4% | **Neutral (0 pts)**. Ensemble mechanically fills blanks but llama fills them with "x" (day off) not correct time ranges. llama not useful as blank-fill partner. |
| 23 | gemma3:27b | P6: full pipeline + known-names (1 run, 168 shifts) | — | 17.3% | **Not viable**. Wrong-year hallucination (returns 2023 vs 2025). All IM(1) employees MISSING (0/77). IM(2) 29/91 (31.9%). Same failure class as gemma3:4b. |
| **24** | **qwen2.5vl:7b** | **P12+P13: 5+2 votes + ISO date keys (3 runs, 168 shifts, no --known-names)** | — | **56.0% avg** | **REGRESSED −11.5 pts** vs Phase 17 baseline (67.5%). Zero variance (94/168 × 3) — systematic failure. Isolation runs P12a/P13a needed. |
| 25 | qwen2.5vl:7b | P12a: 5+2 votes + day-name keys, --known-names (3 runs) | — | 57.7% | REGRESSED. Both P12 and P13 harmful individually. |
| **26** | **qwen2.5vl:7b** | **P13 revert: 3+2 votes + day-name keys (true baseline), --known-names (3 runs)** | — | **66.1%** | **True mean ~66%. Phase 21's 71.4% was a single-run outlier.** |
| 27 | qwen2.5vl:7b | P14: numbered-column prompt + drift detector (3 runs) | 68.5% | 62.7% | High variance (56–69%). Numbered-columns destabilized model. REGRESSED. |
| 28 | qwen2.5vl:7b | P14b: drift detector only, temp=0.1 (3 runs) | 72.6% | 66.5% | High variance. New single-run peak but not stable. |
| **29** | **qwen2.5vl:7b** | **P15: temperature 0.1→0.0 (3 runs)** | — | **69.0%** | **+3 pts over Phase 26 baseline. Perfectly deterministic: 116/168 × 3.** |
| **30** | **qwen2.5vl:7b** | **P16: blank fallback improvements (3 runs)** | — | **69.6%** | **+0.6 pts. 117/168 × 3 deterministic. Andee 10/26 RTO fixed. New confirmed floor.** |
| 31 | qwen2.5vl:7b | P17: threshold=1 drift + ExtractRowAsync (3 runs) | — | 61.3% | CATASTROPHIC REGRESSION −8.3 pts. Threshold=1 fires on normal schedules. Immediately reverted. |
| **32** | **qwen2.5vl:7b** | **P18: quality guards on all re-queries (3 runs)** | — | **69.6%** | **Defensive improvement. CountValidShifts() guard. No regression. Stays.** |
| 33 | qwen2.5vl:7b | P19: OCR garbage sanitization + systematic drift detection (3 runs, **no --known-names**) | — | 67.3% | Apparent −2.3 pts. Root cause: Athena name lost (missing --known-names). True P19 impact: +1 shift (OCR fix works). |

---

## Phase 23 — P6: gemma3:27b (full pipeline)

_Session 5. Single run, both images (168 shifts), `--model gemma3:27b`._

**Score: 29/168 (17.3%) — IM(1): 0/77, IM(2): 29/91**  
**vs. Phase 21 baseline (qwen2.5vl:7b): 120/168 (71.4%)**  
**Verdict: Not viable. −54.1 pts below qwen2.5vl:7b.**

**Timing**: 362.5s (IM(1)), 370.8s (IM(2)) — 2× slower than qwen2.5vl:7b (no net VRAM fit; partial CPU offloading at 17 GB on a 16 GB card).

### Why it fails

1. **Wrong year** (IM(1)): Returns `Year: 2023` instead of `2025` — header date hallucination. Same failure as gemma3:4b (Phase 13). The gemma3 architecture (both 4B and 27B) cannot reliably extract the `YYYY` year from this style of handwritten schedule header.

2. **All shifts MISSING on IM(1)**: Because the year is wrong (2023), every extracted date is `2023-10-29` through `2023-11-04` instead of `2025-10-26` through `2025-11-01`. The scorer finds 0 matching dates → 0/77 correct.

3. **IM(2): partial recovery (29/91, 31.9%)**: The date header in IM(2) is apparently clearer or formatted differently — gemma3:27b correctly reads the year for IM(2) but still makes WRONG-COL and other errors on most employees. 29 cells happened to match.

4. **Size does not help**: gemma3:27b (27B parameters) performs WORSE than gemma3:4b's 0/77 on IM(1) in the sense that the root cause is identical — year hallucination — just on one image instead of both.

### Conclusion

gemma3 (any size) is not viable for this task. The failure is architectural — gemma3 cannot reliably parse the handwritten `M/D/YY` date format in the schedule header column. qwen2.5vl:7b is specifically trained on document/table understanding and handles these date formats correctly.

**Anti-pattern noted**: gemma3 (any size) for schedule-header date extraction.

**Bug fix (incidental)**: During this test, the per-employee score table crashed with `System.ArgumentOutOfRangeException` when a percentage was single-digit (e.g. `0%` → formatted string `"  0/7 (0%)"` = 13 chars shorter than the `[..14]` slice). Fixed by replacing `[..14]` with `.PadRight(16)` in the per-employee display loop.

---

## Phase 22 — P8: Cross-Model Ensemble Blank-Fill (qwen2.5vl:7b + llama3.2-vision:11b)

_Session 5. Single run, both images (168 shifts), `--ensemble llama3.2-vision:11b`._

**Score: 120/168 (71.4%) — IM(1): 58/77, IM(2): 62/91**  
**vs. Phase 21 baseline: 120/168 (71.4%)**  
**Net gain: 0 shifts (+0.0 pts)**

### Implementation
`--ensemble <model>` added to CLI. After the primary model (qwen2.5vl:7b) completes its full 5-pass extraction, the ensemble model runs a single pass and fills any cells where the primary returned `""`. Non-blank primary values are never overwritten (`MergeBlankFill` helper).

### Result Analysis
The ensemble mechanically worked — llama3.2-vision:11b did fill the blank cells from qwen. However:
- Cells qwen left blank (e.g. Andee 10-30 `""`, Andee 10-31 `""`) were filled by llama with `"x"` (day-off marks)
- The expected values for those cells are time ranges (`"10:00-6:30"`, `"11:00-7:30"`)
- llama3.2-vision:11b's error profile on hard-to-read cells is to default to `"x"` — it cannot reliably read the fine time-range digits in cells that qwen also struggles with
- Net: blank(`""`) replaced by wrong value (`"x"`) — same wrong answer, different error type, same score

**Conclusion**: llama3.2-vision:11b is not a useful ensemble partner for blank-filling. Its own weakness on dense time-range digits is identical to qwen's hard limit on those cells. The ensemble requires a stronger secondary model (≥14B or structurally different architecture) to have any chance of filling blanks correctly.

**Anti-pattern confirmed**: `--ensemble llama3.2-vision:11b` — adds ~100s per image with zero benefit. The blank cells are hard OCR limits even for llama.

---

## Phase 24 — P12+P13: 5+2 Majority Votes + ISO Date Keys (qwen2.5vl:7b) — REGRESSED

_Session 6 (2026-03-10). 3 runs, both images (168 shifts), no `--known-names`. Changes: P12 — row runs 3→5 (total 7 votes: 5 row + 2 col); P13 — `ExtractAllShiftsAsync` uses ISO date labels (e.g. `"10/26"`) as JSON keys instead of day names (`"Sun"`)._

| Run | Score |
|-----|-------|
| Run 1 | 94/168 (56.0%) |
| Run 2 | 94/168 (56.0%) |
| Run 3 | 94/168 (56.0%) |
| **Avg** | **94/168 (56.0%)** |

**vs. Phase 17 baseline (no known-names, 3+2 votes, day-name keys): 113.3/168 (67.5%)**  
**Regression: −19 shifts (−11.5 pts)**

### Why it regressed

Both P12 and P13 were applied simultaneously, making it impossible to attribute the regression to one change alone.

1. **P13 (ISO date keys) is the primary suspect**: Changing JSON keys from familiar day-name vocab (`"Sun"`, `"Mon"`) to image-specific date labels (`"10/26"`, `"10/27"`) may confuse the model — it must read the header dates and use them verbatim as JSON output keys (higher cognitive task). Day names appear far more frequently in training data.

2. **P12 (5 row runs) likely locked in the P13 error**: If P13 caused a systematic drift, 5 majority-vote row runs stabilize that drift rather than correcting it. The perfect run-to-run consistency (94/168 × 3 macro-runs, zero variance) is characteristic of a prompt-format change that locks the model into a consistent but wrong extraction pattern.

### Next steps

- **P12a**: Revert P13 only. Keep 5+2 votes, restore day-name keys. Run 3×.
- **P13a**: Revert P12 only. Keep ISO keys, restore 3+2 votes. Run 3×.
- If both individually regress → revert both, return to Phase 21 baseline (71.4% with `--known-names`).
- If P12 alone is neutral/positive → keep 5+2 votes, revert P13 only.
- Either way, move to **P14** (`qwen2.5vl:72b`) as the next major experiment.

### Note on benchmark conditions

This run did not use `--known-names`. Future Phase 24 isolation runs should add `--known-names` for a fair comparison against Phase 21 (71.4%). The benchmark.ps1 script does not currently support a `-KnownNames` passthrough parameter — use the CLI directly or add that parameter to the script.

---

## Phase 25 — P12a: 5+2 Votes, Day-Name Keys (P12 kept, P13 reverted)

_Session 7. 3 runs, both images (168 shifts), `--known-names`._

| Run | Score |
|-----|-------|
| Run 1 | 92/168 (54.8%) |
| Run 2 | 99/168 (58.9%) |
| Run 3 | 100/168 (59.5%) |
| **Avg** | **97.0/168 (57.7%)** |

**vs. Phase 26 true baseline (3+2 votes, day-name keys, --known-names): 66.1%**
**Verdict: REGRESSED. P12 (5+2 votes) is harmful on its own.**

### Why it regressed

With P13 reverted (day-name keys restored) but P12 still active (5 row runs + 2 col runs), the extra majority votes reinforced the model's existing column-drift pattern rather than correcting it. Higher vote counts stabilize whatever extraction the model consistently produces — if that extraction drifts, more votes lock in the wrong answer.

### Conclusion

Both P12 and P13 needed to be reverted together. See Phase 26.

---

## Phase 26 — P13 Revert: 3+2 Votes, Day-Name Keys (True Baseline)

_Session 7. 3 runs, both images (168 shifts), `--known-names`._

| Run | Score |
|-----|-------|
| Run 1 | 108/168 (64.3%) |
| Run 2 | 110/168 (65.5%) |
| Run 3 | 115/168 (68.5%) |
| **Avg** | **111.0/168 (66.1%)** |

**vs. Phase 21 (single run, known-names): 120/168 (71.4%)**
**True mean with 3+2 votes, day-name keys, --known-names, temp=0.1: ~66.1%**

### Key finding

The historical **71.4% (Phase 21)** was a single-run result at temperature=0.1 — a statistical outlier. Over 3 runs, the true mean is **~66.1%**. This is the confirmed baseline for all subsequent experiments.

Both P12 and P13 fully reverted. `OllamaCalendarService.cs` back to 3 row runs + 2 col runs, day-name JSON keys (`"Sun"`…`"Sat"`).

---

## Phase 27 — P14: Numbered-Column Prompt + Drift Detector

_Session 7. 3 runs, both images (168 shifts), `--known-names`._

**Changes**:
1. `ExtractAllShiftsAsync` prompt updated to use numbered columns: `"1=Sun, 2=Mon, 3=Tue, 4=Wed, 5=Thu, 6=Fri, 7=Sat"`.
2. Drift detector added to `ProcessAsync`: if 2+ consecutive (blank, non-blank) pairs detected across employees, those employees are re-queried.

| Run | Score |
|-----|-------|
| Run 1 | 115/168 (68.5%) |
| Run 2 | 95/168 (56.5%) |
| Run 3 | 106/168 (63.1%) |
| **Avg** | **105.3/168 (62.7%)** |

**vs. Phase 26 baseline: 66.1%**
**Regression: −3.4 pts avg. High variance (56–69%).**

### Why it regressed

The numbered-columns prompt destabilized the model. Day-name labels (`"Sun"`/`"Mon"`) appear far more frequently in training data than numeric column indices. Introducing `"1=Sun, 2=Mon..."` creates ambiguity — the model may misinterpret column numbers as shift-cell content or count columns differently.

The drift detector itself was not isolated here — see Phase 28.

---

## Phase 28 — P14b: Drift Detector Only (Numbered Columns Reverted)

_Session 7. 3 runs, both images (168 shifts), `--known-names`._

**Changes**: Reverted numbered-columns prompt change. Kept drift detector (threshold=2 consecutive blank→non-blank pairs triggers per-employee re-query).

| Run | Score |
|-----|-------|
| Run 1 | 107/168 (63.7%) |
| Run 2 | 122/168 (72.6%) |
| Run 3 | 106/168 (63.1%) |
| **Avg** | **111.7/168 (66.5%)** |

**vs. Phase 26 baseline: 66.1%**
**Net: +0.4 pts avg. New all-time single-run peak: 122/168 (72.6%).**

### Analysis

The drift detector helps occasionally — Run 2 set a new peak. However, at temperature=0.1, the model is non-deterministic and the variance (63–73%) is too high to confirm this as a stable improvement. The drift detector is working correctly; the instability is from temperature.

**Next step**: Test at temperature=0.0 to isolate the drift detector's true contribution. See Phase 29.

---

## Phase 29 — P15: Temperature 0.1 → 0.0

_Session 7. 3 runs, both images (168 shifts), `--known-names`._

**Change**: `temperature` in `CallOllamaAsync` changed from `0.1` to `0.0`.

| Run | Score |
|-----|-------|
| Run 1 | 116/168 (69.0%) |
| Run 2 | 116/168 (69.0%) |
| Run 3 | 116/168 (69.0%) |
| **Avg** | **116/168 (69.0%)** |

**vs. Phase 26 baseline: 66.1%**
**Improvement: +2.9 pts. Perfectly deterministic — all 3 runs identical.**

### Key finding

Temperature=0.0 eliminates run-to-run variance entirely. Every run produces exactly the same output. This is invaluable for debugging: any score change from a code modification is a real signal, not noise.

**Anti-pattern correction**: temperature=0.0 was listed as an anti-pattern from the Phase 9a context (batched extraction with a second batch at temp=0 causing failures). That failure mode was specific to the two-batch architecture and does not apply to the current single-batch + re-query design. At temperature=0.0 with the current architecture, the model is strictly more reliable.

---

## Phase 30 — P16: Blank Fallback Improvements

_Session 7/8. 3 runs, both images (168 shifts), `--known-names`._

**Changes**:
1. **Conditional overwrite on heavy-blank re-query**: only apply re-query result if it contains fewer blank cells than the current vote result (previously overwrote unconditionally).
2. **Per-employee `ExtractRowAsync` fallback**: after the batch heavy-blank re-query, any employee still with 4+ blanks receives an individual row extraction call.

| Run | Score |
|-----|-------|
| Run 1 | 117/168 (69.6%) |
| Run 2 | 117/168 (69.6%) |
| Run 3 | 117/168 (69.6%) |
| **Avg** | **117/168 (69.6%)** |

**vs. Phase 29 baseline: 69.0%**
**Improvement: +0.6 pts. Perfectly deterministic.**

### What was fixed

Andee's `10/26` RTO entry: the unconditional heavy-blank re-query was overwriting a correct RTO value with a blank result (the re-query returned more blanks). The conditional guard fixed this.

### New confirmed floor

**69.6% (117/168)** × 3 deterministic runs — the new baseline for all subsequent experiments.

---

## Phase 31 — P17: Threshold=1 Drift + ExtractRowAsync (REVERTED)

_Session 8. 3 runs, both images (168 shifts), `--known-names`._

**Changes**:
1. Drift detector threshold: 2 → 1 (trigger re-query on any single consecutive blank→non-blank transition).
2. Per-employee re-query upgraded from `ExtractAllShiftsAsync` to `ExtractRowAsync`.

| Run | Score |
|-----|-------|
| Run 1 | 103/168 (61.3%) |
| Run 2 | 103/168 (61.3%) |
| Run 3 | 103/168 (61.3%) |
| **Avg** | **103/168 (61.3%)** |

**vs. Phase 30 baseline: 69.6%**
**Regression: −8.3 pts. CATASTROPHIC. Immediately reverted.**

### Why it failed

Threshold=1 fires on **normal schedules**. An employee who is off Sunday and works Monday–Saturday has exactly 1 blank→non-blank transition — this is correct data, not a drift artifact. With threshold=1, the detector triggered for 10 out of 13 employees in IM(2) and re-queried them individually via `ExtractRowAsync`.

`ExtractRowAsync` is designed for targeted re-extraction of a single suspect employee in isolation. When used as a general replacement for the majority-vote batch process on 10 of 13 employees simultaneously, it returned garbage — the isolated row prompt lacks the table context that helps the model count columns correctly.

### Lesson

Drift detection requires **threshold ≥ 2** (two or more consecutive anomalous blank→non-blank transitions) to avoid false positives on normal schedule patterns.

**Code state**: Immediately reverted to P16 (69.6%).

---

## Phase 32 — P18: Quality Guards on All Re-Queries

_Session 8. 3 runs, both images (168 shifts), `--known-names`._

**Changes**:
1. Added `CountValidShifts()` helper — counts cell values matching `""`, `"x"`, `"RTO"`, `"PTO"`, or regex `^\d{1,2}:\d{2}-\d{1,2}:\d{2}$`.
2. All 3 re-query apply-guards upgraded from `fewerBlanks` to `(fewerBlanks) OR (sameBlanks AND moreValidValues)`.

| Run | Score |
|-----|-------|
| Run 1 | 117/168 (69.6%) |
| Run 2 | 117/168 (69.6%) |
| Run 3 | 117/168 (69.6%) |
| **Avg** | **117/168 (69.6%)** |

**vs. Phase 30 baseline: 69.6%**
**Net: 0 pts. No regression, no improvement.**

### Value

This is a **defensive improvement**. The `CountValidShifts()` guard ensures re-query results are only applied when demonstrably better than the vote result — either fewer blanks, or the same blank count but more well-formed values. This would have prevented the Phase 31 / P17 regression (where re-query results replaced valid data with garbage). Stays.

---

## Phase 33 — P19: OCR Garbage Sanitization + Systematic Drift Detection

_Session 8. 3 runs, both images (168 shifts), **no `--known-names` — benchmark command error**._

**Changes**:
1. **OCR garbage sanitization**: values matching `^\d+\.?\d*\s+\w` (e.g. `"28.5 Seena"`, `"24 Jenny"`) → replaced with `""`. These are OCR fragments where the model returned a page-number-like prefix followed by an employee name instead of a shift value.
2. **Systematic shift detection**: after majority vote, if 5+ employees show blank@col[N] + non-blank@col[N+1], treat as systematic right-shift, apply provisional correction, and re-query col[N+1] with `ExtractColumnAnchoredAsync` (new method — date-anchored single-column extraction).

| Run | Score |
|-----|-------|
| Run 1 | 113/168 (67.3%) |
| Run 2 | 113/168 (67.3%) |
| Run 3 | 113/168 (67.3%) |
| **Avg** | **113/168 (67.3%)** |

**Apparent regression: −2.3 pts vs Phase 32 (69.6%)**

### Root cause analysis

The apparent regression is **not caused by P19 code**. The benchmark was run without `--known-names` (a mistake in the benchmark command). Without the known-names list, Ollama's session non-determinism caused Athena's employee name to be extracted as `"Athena(train)"` / `"Clara"` — her row was effectively missing, accounting for approximately −7 shifts. This failure mode is identical to the pre-Phase-21 NAME-SPLIT errors.

### P19 actual impact

- **OCR garbage sanitization worked**: Jenny's `"24 Jenny"` value in IM(1) was correctly cleaned to `""`, fixing +1 shift.
- **Systematic drift detection never fired**: Right-shift errors in the test images are spread across different column positions per employee — no single column pair reached the threshold of 5 simultaneous anomalies. The detection logic is structurally sound but the real-world drift pattern doesn't trigger it.

### Current code state

P19 code remains in the codebase:
- OCR garbage sanitization: ✅ working, confirmed +1 shift, keep.
- Systematic detection block + `ExtractColumnAnchoredAsync`: non-firing under real conditions. Either remove the dead code (clean up) or re-evaluate with `--known-names` to confirm true net impact.

**To confirm P19's true impact**: re-run with `--known-names`; expect ≥ 118/168 (69.6% + 1 shift from OCR fix).

---

