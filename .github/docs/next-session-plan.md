# CalendarParse — Next Session Plan

_Last updated: 2026-03-29 (session 15)_

## Current State

| Metric | Value |
|--------|-------|
| **Best score (hybrid)** | **227/252 (90.1%)** — qwen2.5vl:7b + WinRT OCR, **no --known-names required** |
| Test set | 3 images, 252 shifts: IM(1)=77, IM(2)=91, IM(3)=84 |
| Active hybrid pipeline | `HybridCalendarService.cs` — OCR first → fragments to LLM → session pool → per-day strip LLM → OCR name supplement → holiday heuristic |
| Vision pipeline | `OllamaCalendarService.cs` P20 — **DO NOT MODIFY** |
| Temperature | 0.0 (deterministic) |
| Known names | **Not required** — names discovered dynamically at runtime |

### LLM Call Count Per Image
10 calls per image (down from 11 after pass 2b removed):
- Pass 1: header (1 call)
- Pass 2: names from full image with OCR fragments (1 call)
- Pass 3: one strip per day column (7 calls, some skipped if OCR pre-fills)
- Pass 4: x-marks clarification (1 call)

Possible future optimization: reduce pass 3 calls via better OCR pre-fill.

### Remaining Errors (25 total)

**IM(1) — 17 errors**:
- Ciara: 3 blanks (last-row strip truncation — Oct29, Oct31, Nov01)
- Kyleigh: 2 errors (Thu TIME-MISREAD, one x/shift swap)
- Brittney: 1 TIME-MISREAD (Thu)
- Victor: 1 x/shift swap (Thu Oct30)
- Halle: 1 x/shift swap (Thu Oct30)
- Seena: 2 x/shift swaps (Thu Oct30 area)
- Jenny: 1 TIME-MISREAD (Sun)
- Halle: 1 TIME-MISREAD
- Various: remaining individual misreads

**IM(2) — 8 errors**:
- Athena: 7/7 MISSING — OCR finds no tokens in her row; LLM also misses her from all views; trainee-row styling likely makes her invisible
- Tori: 3 shifts wrong (TIME-MISREAD)
- Halle: 1 TIME-MISREAD
- Ciara: 1 x/shift swap

**IM(3) — 1 error**:
- Franny Sep24: got "x" expected "1:30-6:00" (single stochastic misread)

### Hard Ceiling Analysis

Athena (IM(2)) is the single largest opportunity: 7 points available. Both OCR and LLM are blind to her row — she appears to be in a trainee-row format that neither engine can read. Even if detected via name, OCR can't read her shift cells, so max gain is ~3/7 (x-days via empty≡x rule). Realistically ~218-220/252 ceiling without Athena.

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
# Standard test — no --known-names needed (90.1%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object hybrid-run-output.txt

# Score only (quick check)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Select-String "Overall|IM \("

# Benchmark loop (meta-agent improvement)
.\benchmark-loop.ps1 -MaxIterations 20 -TargetScore 245

# Dry-run first to verify meta-agent proposal quality
.\benchmark-loop.ps1 -DryRun
```

## Next Steps (Priority Order)

1. **Commit current 227/252 checkpoint** to git
2. **Update benchmark-loop.ps1 system prompt** — error taxonomy and structural targets are now stale (based on old 151/168 errors); update to reflect current 25 errors against 252 shifts
3. **Thu Oct30 column boundary** — still the biggest IM(1) cluster (~5-6 errors); `ComputeDayColBoundsFromOcr()` midpoint may land inside the Wed column for that specific image
4. **Ciara last-row blank** — structural; 3 errors in IM(1); strip crop height or LLM stopping one row early
5. **Reduce LLM call count** — 10 calls/image × 3 images = 30 calls per test run; OCR pre-fill currently covers only time-range patterns; extending it to handle x-marks or RTO/PTO could skip some pass 3 calls
