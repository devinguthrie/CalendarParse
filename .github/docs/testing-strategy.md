# CalendarParse CLI — Testing Strategy

_Last updated: 2026-03-24 (session 13)_

## Goal

Maximize correct cells / 168 total (IM(1): 77 + IM(2): 91).

| Pipeline | Score | Details |
|----------|-------|---------|
| **Hybrid (active)** | **151/168 (89.9%)** | `--hybrid`, qwen2.5vl:7b + WinRT OCR, deterministic |
| Vision (frozen) | 131/168 (78.0%) | `--vision`, Phase 36/P20, DO NOT MODIFY |

## Test Execution

```powershell
# Standard hybrid test
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --hybrid --test --model qwen2.5vl:7b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori"
```

- Hybrid at temp=0.0 is deterministic — one run is sufficient
- Accept improvement only if **average** improves across 3+ runs (for non-deterministic modes)
- Always pass `--known-names` with the full 13-name list

## Error Categories

| Category | Description |
|----------|-------------|
| BLANK | Model returned `""` for a non-blank cell |
| SPURIOUS | Model returned a value for a blank cell |
| X-SWAP | Confused `x`↔`""` (scored equivalent) or `x`↔shift |
| TIME-MISREAD | Correct structure, wrong digits (e.g. 6:30→6:00) |
| WRONG-ROW | Value from a different employee's row |
| WRONG-COL | Correct row but shifted column |
| NAME-MISS | Employee name extracted incorrectly |
| LABEL-WRONG | Wrong label type (RTO↔PTO) |

## Current Error Breakdown — Hybrid Pipeline (151/168, 17 errors)

**IM(1) — 15 errors**:
- Ciara: 3 blanks (last-row LLM array truncation)
- Thu Oct30: x/shift swaps across Cyndee, Victor, Halle, Kyleigh, Seena (5 cells)
- Jenny Sun: TIME-MISREAD
- Sarah Wed: x/shift swap
- Franny Wed: SPURIOUS shift
- Brittney Fri: TIME-MISREAD
- Kyleigh Thu: TIME-MISREAD

**IM(2) — 2 errors**:
- Halle Nov28: TIME-MISREAD
- Tori Nov23: TIME-MISREAD

**Dominant remaining**: TIME-MISREAD (~7) + x/shift swaps (~7) + array truncation (~3)

## Quality Thresholds

| Threshold | Meaning |
|-----------|---------|
| > 80% | Near-production |
| > 90% | Excellent — only rare OCR ambiguities |
| 100% | Theoretical max — unlikely with OCR noise |

## Invariants — Do Not Change

- `NormalizeDate()` call in both services + `Program.cs`
- `RepairTruncatedJson()` fallback
- `EnsureModelLoadedAsync()` warmup
- `keep_alive = -1`
- `--known-names` flag on all test runs (full 13-name list)
- Anti-WRONG-COL warning at END of rules in `ExtractAllShiftsAsync` only
- Scorer blank≡x equivalence (`ShiftsMatch()`)
- Fuzzy name matching (Levenshtein ≤ 2) in scorer
