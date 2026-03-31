# CalendarParse — Next Session Plan

_Last updated: 2026-03-30 (session 16)_

## Current State

| Metric | Value |
|--------|-------|
| **Best score (hybrid)** | **230/252 (91.3%)** — qwen2.5vl:7b + WinRT OCR, **no --known-names required** |
| Test set | 3 images, 252 shifts: IM(1)=77, IM(2)=91, IM(3)=84 |
| Active hybrid pipeline | `HybridCalendarService.cs` — OCR first → fragments to LLM → session pool → per-day strip LLM → OCR name supplement → holiday heuristic |
| Vision pipeline | `OllamaCalendarService.cs` P20 — **DO NOT MODIFY** |
| Temperature | 0.0 (deterministic) |
| Known names | **Not required** — names discovered dynamically at runtime |

### Session 16 Wins (+3 from 227→230)

| Commit | Change | Gain |
|--------|--------|------|
| `1d55892` | Targeted re-query when LLM array truncates to n-1 values | +1 (Ciara Oct31) |
| `9495ec5` | Extended re-query to fire when last employee is blank in full array | +1 (Ciara Oct29) |
| `6161c24` | Penultimate duplicate-value re-query (row-swap absorption detection) | +1 (Seena Oct29) |

### LLM Call Count Per Image
10–13 calls per image (up to +3 for re-queries):
- Pass 1: header (1 call)
- Pass 2: names from full image with OCR fragments (1 call)
- Pass 3: one strip per day column (7 calls, some skipped if OCR pre-fills)
- Pass 4: x-marks clarification (1 call)
- Re-query: last-employee re-query fires when truncated or last-blank (up to 1/strip)
- Re-query: penultimate re-query fires when duplicate-value anomaly detected (up to 1/strip)

### Remaining Errors (22 total)

**IM(1) — 9 errors**:
- Ciara: 1 blank (Nov01 — re-query consistently returns wrong time "9:30-2:00"; structural)
- Kyleigh: 2 errors (Oct28 x/shift swap; Oct30 Thu cluster row-swap)
- Brittney: 1 row-swap (Thu Oct30 cluster)
- Cyndee: 1 row-swap (Thu Oct30 cluster)
- Victor: 1 row-swap (Thu Oct30 cluster)
- Halle: 1 row-swap (Thu Oct30 cluster)
- Seena: 1 row-swap (Thu Oct30 cluster)
- Jenny: 1 TIME-MISREAD (Oct26 Sun)

**IM(2) — 12 errors**:
- Athena: 7/7 MISSING — OCR finds no tokens in her row; LLM also misses her from all views; trainee-row styling likely makes her invisible
- Tori: 3 shifts wrong (TIME-MISREAD / x-swap)
- Halle: 1 TIME-MISREAD (Nov28)
- Ciara: 1 x/shift swap (Nov26)

**IM(3) — 1 error**:
- Franny Sep24: got "x" expected "1:30-6:00" (single stochastic misread)

### Hard Ceiling Analysis

Athena (IM(2)) is the single largest opportunity: 7 points available. Both OCR and LLM are blind to her row — she appears to be in a trainee-row format that neither engine can read. Even if detected via name, OCR can't read her shift cells, so max gain is ~3/7 (x-days via empty≡x rule).

Thu Oct30 cluster (6 errors in IM(1)): systematic row-swap affecting 5-6 employees in that specific column strip. Root cause is `ComputeDayColBoundsFromOcr()` midpoint landing in Wed column. All 6 errors fire on the same image/day combination.

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

## Fine-Tuning (Deferred)

LoRA feasible on hardware (16 GB VRAM) but only 3 labeled images exist. Need 50–200+ before pursuing.

## Quick Reference Commands

```powershell
# Standard test — no --known-names needed (91.3%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object hybrid-run-output.txt

# Score only (quick check)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Select-String "Overall|IM \("

# Benchmark loop (meta-agent improvement)
.\benchmark-loop.ps1 -MaxIterations 20 -TargetScore 245

# Dry-run first to verify meta-agent proposal quality
.\benchmark-loop.ps1 -DryRun
```

## Next Steps (Priority Order)

1. **Thu Oct30 column boundary (6 errors)** — biggest remaining IM(1) cluster; `ComputeDayColBoundsFromOcr()` midpoint lands inside the Wed column for that image; inspect OCR-detected column boundaries for the Oct30 day column specifically
2. **Ciara Nov01 blank (1 error)** — re-query returns "9:30-2:00" (structurally valid but wrong); the last-employee re-query fires correctly but reads the wrong row; may need the strip crop for that specific column to be expanded downward by a few pixels
3. **Tori misreads (3 errors, IM(2))** — TIME-MISREAD / x-swap; Tori is sensitive to main strip prompt changes; re-query changes are safer
4. **Update benchmark-loop.ps1 system prompt** — currently produces only malformed proposals (12/12 failures); the search strings don't match exactly once; update with current 22-error taxonomy so proposals target actual remaining errors
5. **OCR pre-fill (deferred)** — blocked until all employees have reliable Y anchors; do not attempt until `nameToYEarly` is populated for all 11 IM(1) employees
