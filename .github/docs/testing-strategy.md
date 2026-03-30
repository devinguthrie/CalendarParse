# CalendarParse CLI — Testing Strategy

_Last updated: 2026-03-29 (session 15)_

## Goal

Maximize correct cells / 252 total (IM(1): 77 + IM(2): 91 + IM(3): 84).

| Pipeline | Score | Details |
|----------|-------|---------|
| **Hybrid (active)** | **227/252 (90.1%)** | qwen2.5vl:7b + WinRT OCR, no --known-names required |
| Vision (frozen) | 131/168 (78.0%) on 2-image set | `--vision`, Phase 36/P20, DO NOT MODIFY |

## Test Execution

```powershell
# Standard hybrid test — no --known-names needed
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b
```

- Hybrid at temp=0.0 is deterministic — one run is sufficient
- `--known-names` is no longer required; names are discovered dynamically via OCR fragments + session pool
- Accept improvement only if score improves vs current 227/252 baseline

## Error Categories

| Category | Description |
|----------|-------------|
| BLANK | Model returned `""` for a non-blank cell |
| SPURIOUS | Model returned a value for a blank cell |
| X-SWAP | Confused `x`↔`""` (scored equivalent) or `x`↔shift |
| TIME-MISREAD | Correct structure, wrong digits (e.g. 6:30→6:00) |
| WRONG-ROW | Value from a different employee's row |
| WRONG-COL | Correct row but shifted column |
| NAME-MISS | Employee name not extracted (entire row missing from output) |
| LABEL-WRONG | Wrong label type (RTO↔PTO) |

## Current Error Breakdown — Hybrid Pipeline (227/252, 25 errors)

**IM(1) — 17 errors**:
- Ciara: 3 blanks (last-row strip truncation — Oct29, Oct31, Nov01)
- Kyleigh: 2 errors (71% total)
- Seena: 2 errors (71% total)
- Halle: 1 error
- Victor: 1 error
- Brittney: 1 error
- Jenny: 1 error
- Franny: 1 error (IM(3) only, not IM(1))
- Various individual shift misreads

**IM(2) — 8 errors**:
- Athena: 7 MISSING — OCR + LLM both blind to her row (trainee-row styling); hard ceiling
- Tori: 3 TIME-MISREAD / x-swap (4/7)
- Halle: 1 TIME-MISREAD
- Ciara: 1 x-swap

**IM(3) — 1 error**:
- Franny Sep24: single stochastic misread (x vs 1:30-6:00)

**Dominant remaining**: Athena invisibility (7) + Thu Oct30 column swaps (~5) + Ciara truncation (3) + TIME-MISREAD (~5) + Tori misreads (3)

## Employee Scores (latest run)

| Employee | IM(1) | IM(2) | IM(3) | Total |
|----------|-------|-------|-------|-------|
| Andee | 7/7 | 7/7 | 7/7 | 21/21 (100%) |
| Athena | — | 0/7 | — | 0/7 (0%) ← hard ceiling |
| Brittney | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
| Ciara | 4/7 | 6/7 | — | 10/14 (71%) |
| Cyndee | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
| Franny | 7/7 | 7/7 | 6/7 | 20/21 (95%) |
| Halle | 6/7 | 6/7 | 7/7 | 19/21 (90%) |
| Jenny | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
| Kyleigh | 5/7 | 7/7 | 7/7 | 19/21 (90%) |
| Megan | — | — | 7/7 | 7/7 (100%) |
| Raul | — | — | 7/7 | 7/7 (100%) |
| Sarah | 7/7 | 7/7 | 7/7 | 21/21 (100%) |
| Seena | 5/7 | 7/7 | 7/7 | 19/21 (90%) |
| Tori | — | 4/7 | — | 4/7 (57%) |
| Victor | 6/7 | 7/7 | 7/7 | 20/21 (95%) |

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
- Anti-WRONG-COL warning at END of rules in `ExtractAllShiftsAsync` only
- Scorer blank≡x equivalence (`ShiftsMatch()`)
- Fuzzy name matching (Levenshtein ≤ 2) in scorer
- OCR runs **before** pass 2 (name extraction) — required for fragment grounding
- Session name pool (`_sessionNames`) accumulation across images in same run
- Non-orthogonal coordinate hint in all LLM prompts
