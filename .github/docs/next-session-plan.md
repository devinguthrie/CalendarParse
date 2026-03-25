# CalendarParse — Next Session Plan

_Last updated: 2026-03-24 (session 13)_

## Current State

| Metric | Value |
|--------|-------|
| **Best score (hybrid)** | **151/168 (89.9%)** — `--hybrid`, qwen2.5vl:7b + WinRT OCR, deterministic ×2 confirmed |
| Best score (vision) | 131/168 (78.0%) — `--vision`, Phase 36/P20, temperature=0.0 |
| Active hybrid pipeline | `HybridCalendarService.cs` — OCR column detect → per-day strip LLM → holiday heuristic |
| Vision pipeline | `OllamaCalendarService.cs` P20 — **DO NOT MODIFY** |
| Temperature | 0.0 (deterministic) |

### Remaining Errors (17 total)

**IM(1) — 15 errors**: Ciara last-row truncation (3 blanks); Thu Oct30 x/shift swaps (Cyndee, Victor, Halle, Kyleigh, Seena — 5 cells); Jenny Sun TIME-MISREAD; Sarah Wed x/shift; Franny Wed spurious; Brittney Fri TIME-MISREAD; Kyleigh Thu TIME-MISREAD

**IM(2) — 2 errors**: Halle Nov28 TIME-MISREAD; Tori Nov23 TIME-MISREAD

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

## Fine-Tuning (Deferred)

LoRA feasible on hardware (16 GB VRAM) but only 2 labeled images exist. Need 50–200+ before pursuing.

## Quick Reference Commands

```powershell
# Hybrid (current best — 89.9%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --hybrid --model qwen2.5vl:7b --test --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Tee-Object hybrid-run-output.txt

# Vision baseline (78.0%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Tee-Object run-output.txt

# Score only (quick check)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --hybrid --model qwen2.5vl:7b --test --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Select-String "Overall|IM \("

# Benchmark script
.\benchmark.ps1 -Models "qwen2.5vl:7b" -Modes "none" -Runs 3 -KnownNames "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" -OutFile "benchmark-pXX.txt" | Tee-Object "benchmark-pXX-log.txt"
```
