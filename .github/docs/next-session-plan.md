# CalendarParse — Next Session Plan

_Last updated: 2026-04-27 (session 22)_

## Current State

| Metric | Value |
|--------|-------|
| **Best score (hybrid)** | **394/434 (90.8%)** — qwen2.5vl:7b + WinRT OCR, **no --known-names required** |
| Test set | 5 images, 434 shifts: IM(1)=77, IM(2)=91, IM(3)=84, IM(4)=91, IM(5)=91 |
| Active hybrid pipeline | `HybridCalendarService.cs` — OCR first → fragments to LLM → session pool → per-day strip LLM → OCR name supplement → holiday heuristic |
| Vision pipeline | `OllamaCalendarService.cs` P20 — **DO NOT MODIFY** |
| Temperature | 0.0 (deterministic) |
| Known names | **Not required** — names discovered dynamically at runtime |

### Session 22 — Engineering Review: Dead Code + Prompt Externalization (394/434, no change)

| Attempt | Result | Outcome |
|---------|--------|---------|
| Remove dead flags/methods + externalize all prompts to prompts.json + PromptService | 394/434 (0) | COMMITTED — structural refactor, no score change |

**Changes**: Removed `halvesMode`, `resizeWidth`, `ensembleModel` flags and helpers (`MergeCalendarResults`, `MergeBlankFill`) from Program.cs. Removed `ExtractAllShiftsCsvAsync` and `ExtractAllShiftsDualAsync` dead methods from OllamaCalendarService. Created `Prompts/prompts.json` (16 templates, embedded resource) and `PromptService` static class for lazy-loaded `{var}` substitution. All 12 inline LLM prompt strings migrated to PromptService in both HybridCalendarService and OllamaCalendarService.

### Session 21 — Retail-Context Hint v3 (+17, 394/434 = 90.8%)

| Attempt | Result | Outcome |
|---------|--------|---------|
| Retail hint v1/v2 in `OllamaCalendarService.ExtractColumnAsync` | 377/434 (0) | Dead code — that function not called by hybrid pipeline |
| **Retail hint v3 in `HybridCalendarService.ExtractColumnFromImageAsync`** | **394/434 (+17)** | **COMMITTED** |

Key diagnostic: hybrid pipeline calls `ExtractColumnFromImageAsync` (not `OllamaCalendarService.ExtractColumnAsync`). Hint placed at start of initial prompt and at start of `reinforcedPrompt` retry broke the model's Saturday=RTO prior.

**Prior sessions** (result of Phases 56–62, all committed or reverted):
- 5-image expansion (IM(4)/IM(5) added): 374/434 baseline
- 8:xx contamination → narrow strip re-query: +3 (374→377/434)
- Name parenthetical normalization: neutral
- Phase 60 leading-RTO re-query: −5, reverted
- Phase 62 ocrTimeMap holiday-guard: 0, reverted (dead code)

### LLM Call Count Per Image
10–17 calls per image:
- Pass 1: header (1 call)
- Pass 2: names from full image with OCR fragments (1 call)
- Pass 3: one strip per day column (7 calls, some skipped if OCR pre-fills)
- Pass 4: x-marks clarification (1 call, skipped for holiday-blanked columns)
- Re-query: last-employee re-query fires when truncated or last-blank (up to 1/strip)
- Re-query: penultimate re-query fires when duplicate-value anomaly detected (up to 1/strip)
- Re-query: 8:xx narrow strip fires when ≥2 time-ranges start with "8:" (up to 1/strip)

### Remaining Errors (57 total)

**IM(1) — 9 errors** (68/77):
- Thu Oct30 cluster: 6 employees affected (Kyleigh, Brittney, Cyndee, Victor, Halle, Seena) — green highlighted Andee cell visual anchor causes row-swap
- Kyleigh: 1 additional error (Oct28 x/shift swap)
- Ciara: 1 blank (Nov01)
- Jenny: 1 TIME-MISREAD (Oct26 Sun)

**IM(2) — 13 errors** (78/91):
- Athena: 7/7 MISSING — OCR finds zero tokens in her row; LLM non-deterministically finds "Athena(train)" visually (~50% of runs) → normalized to "Athena", but still non-deterministic; cannot be reliably fixed
- Tori: 4 errors (TIME-MISREAD / x-swap)
- Halle: 1 TIME-MISREAD
- Ciara: 1 x/shift swap

**IM(4) — 5 errors** (86/91):
- Mon Jul28: 3 errors — Andee/Brittney/Cyndee get "RTO" instead of time-ranges (leading-RTO fix tried and reverted −5)
- Franny Jul31: 1 error (TIME-MISREAD, got "9:30-6:30" expected "9:30-6:00")
- Victor Aug2: 1 error (got "x" expected "RTO")

**IM(5) — 12 errors** (79/91):
- Jul26 Sat: still 7 errors — model now returns time-ranges from the retry but they are shift-time mismatches (Brittney 9:00-5:30 exp 9:30-6:00, Cyndee 9:30-6:00 exp 12:00-6:30, Sarah 12:00-8:30 exp 10:00-6:30, Franny 10:00-6:30 exp x, Victor got x exp RTO, Raul/Kyleigh may vary); partial improvement but time-range discrimination still imperfect
- Jul24: 4 errors — model returns 1-row offset for first 4 employees (Andee x/9:30-6:00, Brittney 9:30-6:00/12:00-8:30, Cyndee 12:00-8:30/9:00-5:30, Sarah 9:00-5:30/x)
- Victor Jul23: 1 error (got "10:00-2:30" expected "x")

### Hard Ceiling Analysis

**IM(1) Thu Oct30 cluster (6 errors)**: `ComputeDayColBoundsFromOcr()` boundaries are geometrically correct. Cause: green highlighted Andee cell acts as visual anchor producing "10:00-6:30" × 3 for rows 2, 5, 8. At temp=0, all re-queries return the same wrong answer. **Irreducible with re-query approaches** — would require image color-masking or fine-tuning.

**IM(2) Athena (7 errors)**: OCR confirmed blind to her row — no fragments at all. LLM finds "Athena(train)" in roughly half of runs (normalized correctly now), but still non-deterministic. No reliable fix path without training data or image-level intervention.

**IM(4) Aug2 Sat (~9 errors) — CONFIRMED HARD CEILING (Session 20)**: Normal work day — most employees have time-range shifts. Model returns all-RTO for the Aug2 Sat column (same failure mode as IM(5) Jul26). Holiday detector **false-fires** → blanks all 13 → OCR salvage recovers only Sarah=RTO and Victor=RTO. Jenny/Halle score correctly via blank≡x. 9 employees with time-range shifts remain blank (wrong). **Diagnostic confirmed (Session 20)**: `ocrTimeMap has 0 time-range(s)` for all 7 day columns of IM(4). WinRT OCR reads IM(4) time cells as fragment tokens ("9:00" + "5:30" separately), never as compound "9:00-5:30" strings — so the ocrTimeMap discriminator has no signal. Same root cause as IM(5) Jul26. **No fixable path without image preprocessing or model fine-tuning.**

**IM(4) Mon Jul28 RTO (3 errors)**: Leading-RTO heuristic tried and failed (−5). Root cause is model contamination from hours sub-column, but the "first 3 employees all RTO" detection pattern fires equally on model misread errors in other columns.

**IM(5) Jul26 (11 errors)**: Uniform-RTO fires on this column, then narrow strip retry produced no time-ranges, so holiday detector blanked the entire column. Most employees actually have time-ranges on Jul26. The holiday detection fired incorrectly. **CONFIRMED HARD CEILING (Session 19).**

**Detailed analysis (Session 19):** The LLM returns 12/13 RTO + Seena "2:30-8:00" for the Sat column. Strict `allSame` fails on Seena's outlier, so neither the uniform-RTO re-query nor the holiday detector fires. Tried 80% majority mode check for both. Result: holiday detector fires → blanks all → OCR salvage recovers only 2 cells (Victor=RTO, Seena=RTO) → Jenny+Kyleigh become blank (wrong, were correct RTO before) → net −1. Narrow retry returns `[SAT, RTO, RTO, RTO, RTO, x, RTO, ...]` — Franny=x correct but Seena missing, rest RTO. WinRT OCR finds **zero time-range tokens** in the Sat column (ocrTimeMap unfilled for day 6), so OCR salvage cannot recover the actual shift values. **No path forward without retraining or image preprocessing.**

**IM(5) Jul24 (4 errors)**: Clean 1-row offset for first 4 employees. Model reads row N+1 data for rows 1–4. No reliable detection heuristic identified without ground truth comparison.

## Next Most-Promising Targets

1. **IM(4) Mon Jul28 RTO ×3 (3 errors)**: Andee/Brittney/Cyndee get "RTO" for a normal Monday work day. May now be addressable via the narrow-strip fix (similar column structure to Sat); worth testing now that Sat is fixed.

2. **IM(5) Jul24 row-offset (4 errors)**: Clean 1-row offset for first 4 employees (Andee, Brittney, Cyndee, Sarah). Model reads row N+1's data for rows 1–4. Potentially detectable/correctable.

3. **IM(5) Jul26 Sat time-mismatches (7 errors)**: Retail hint fixed the all-blank failure, but model reads close-but-wrong times (e.g. "9:00-5:30" when expected "9:30-6:00"). May be a soft ceiling requiring image preprocessing for exact digit precision.

4. **IM(2) Athena (7 errors)**: OCR blind to her row, LLM non-deterministically misses her. Hard ceiling — no reliable fix path.

5. **IM(1) Thu Oct30 cluster (6 errors)**: Green highlighted Andee cell visual anchor. Hard ceiling at temp=0.

~~**IM(4) Aug2 Sat**~~ — **RESOLVED Session 21 (+8)**: Retail hint v3 broke through the Saturday=RTO prior. 

~~**IM(5) Jul26 all-blank**~~ — **RESOLVED Session 21 (+9, partial)**: Retry now returns time-ranges. Still 7 shift-time mismatch errors remain.

## Anti-Patterns — Never Retry

| Anti-pattern | Why |
|---|---|
| gemma3 (any size) | Year hallucination — architectural |
| llava:13b, minicpm-v, granite3.2-vision:2b | 0% — wrong dates or all-x |
| qwen2.5vl:32b (CPU offload) | −23 shifts, 75 min runtime |
| Anchor-guided re-extraction | Overwrites correct values; −35 pts |
| Pipe/CSV output format | Model copies example values |
| `--resize` downscaling | Destroys digit legibility; −13 pts |
| `--ensemble llama3.2-vision:11b` | Fills blanks with "x" not times |
| CLAHE / grayscale / `current` preprocessing | Destroys red ink X-mark color signal |
| Batched extraction (split employees) | Second batch all blank |
| Two-shot self-anchoring (Q1→Q2) | Biases away from cell reading; −4 shifts |
| Anti-shift warning in `ExtractColumnAsync` | Harmful in single-column context; −8 shifts |
| Anti-shift warning at TOP of rules | −27 shifts; must stay at END |
| Vote reweighting (temp=0 copies) | Amplifies errors; −63 shifts |
| Grid detector on phone photos | Detects text rows not grid lines; 0 cells |
| Strip crop nameXStart→dayXEnd (all cols) | Multi-column contamination |
| Positional `List<(XStart,XEnd)>` for days | Off-by-one; use `Dictionary<int,...>` |
| `--halves` composite merge | Zero effect — WRONG-COL isn't pixel-budget |
| `llm` preprocessing | Noise — 0.2 pts vs none |
| 5+2 votes (more row runs) | Locks in errors at temp=0; −8 pts |
| ISO date keys in JSON | Confuses model vs day-name keys; −11 pts |
| Numbered-column prompt | Destabilizes model; −3.4 pts |
| Suspect-blank re-query | Can't distinguish correct blanks from missed |
| Dual-view cross-reference (6th vote) | Correlated views + blank injection; −13 |
| CSV output for row passes | Parse errors cascade; −39 shifts |
| Drift detector threshold=1 | Fires on normal schedules; −8 pts |
| Pass 2b (name column strip LLM crop) | LLM hallucinates variant names; zero net benefit even with edit-distance filter |
| Leading-RTO re-query (first 3 all RTO/PTO) | −5; model erroneously returns RTO for first 3 on legitimate columns — pattern indistinguishable from contamination |
| 80% majority holiday/uniform-RTO check (replacing strict allSame) | IM(4) Aug2 already fires with strict check (model returns all-RTO, holiday detector false-fires); adding majority check additionally fires on IM(5) Jul26 → blanks Jenny+Kyleigh (correct RTO, were already correct before) → net −1. OCR salvage can't recover time-ranges from Jul26 Sat column (zero tokens found). |
| ocrTimeMap holiday-guard (skip holiday blank when ocrTimeMap ≥1 time-range in column) | IM(4) ocrTimeMap = 0 for ALL 7 columns. WinRT reads time cells as separate fragments ("9:00", "5:30") not compound strings — TimeRangeRegex never matches. Guard is dead code for this dataset. IM(4) Aug2 and IM(5) Jul26 are both hard ceilings for the same reason. |
| Retail hint in `OllamaCalendarService.ExtractColumnAsync` | Dead code — that function is never called by the hybrid pipeline. `HybridCalendarService.ExtractColumnFromImageAsync` is the real call site. Injecting hints there (with `isWeekendCol` condition) works correctly. |

## Quick Reference Commands

```powershell
# Standard test — no --known-names needed (90.8%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object hybrid-run-output.txt

# Score only (quick check)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Select-String "Overall|IM \("

# Single image test (faster iteration)
dotnet run --project CalendarParse.Cli --no-build -- "tmp-im5" --test --model qwen2.5vl:7b

# Benchmark loop (meta-agent improvement)
.\benchmark-loop.ps1 -MaxIterations 20 -TargetScore 400

# Dry-run first to verify meta-agent proposal quality
.\benchmark-loop.ps1 -DryRun
```

## Next Steps (Priority Order)

1. **Ciara Nov01 blank (1 error)** — re-query fires correctly but returns "9:30-2:00" instead of "12:00-5:30"; strip crop for that column may need to be expanded downward by a few pixels so the bottom row is fully visible
2. **Tori misreads (3 errors, IM(2))** — TIME-MISREAD / x-swap; Tori is sensitive to main strip prompt changes; targeted re-query approaches safer
3. **Jenny Oct26 TIME-MISREAD (1 error)** — got "10:30-6:00" expected "9:30-6:30"; individual digit confusion on Sun strip
4. **Kyleigh Oct28 x/shift swap (1 error)** — individual x↔shift confusion
5. **Update benchmark-loop.ps1 system prompt** — currently produces only malformed proposals (12/12 failures); update with current 22-error taxonomy, corrected Thu Oct30 root cause (highlighted cell, not column boundary), and guidance on exact-match search strings
6. **Thu Oct30 cluster (6 errors)** — ~~column boundary bug~~ (**confirmed irreducible with re-query**); visual anchor (green highlighted Andee cell) cannot be overcome at temp=0; would require image color-masking preprocessing or fine-tuning; defer unless more training data available
7. **OCR pre-fill (deferred)** — blocked until all employees have reliable Y anchors; do not attempt until `nameToYEarly` is populated for all 11 IM(1) employees
