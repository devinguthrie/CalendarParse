# CalendarParse CLI — Testing Strategy

_Last updated: 2026-03-30 (session 16)_

## Goal

Maximize correct cells / 252 total (IM(1): 77 + IM(2): 91 + IM(3): 84).

| Pipeline | Score | Details |
|----------|-------|---------|
| **Hybrid (active)** | **230/252 (91.3%)** | qwen2.5vl:7b + WinRT OCR, no --known-names required |
| Vision (frozen) | 131/168 (78.0%) on 2-image set | `--vision`, Phase 36/P20, DO NOT MODIFY |

## Test Execution

```powershell
# Standard hybrid test — no --known-names needed
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b
```

- Hybrid at temp=0.0 is deterministic — one run is sufficient
- `--known-names` is no longer required; names are discovered dynamically via OCR fragments + session pool
- Accept improvement only if score improves vs current 230/252 baseline

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

## Current Error Breakdown — Hybrid Pipeline (230/252, 22 errors)

**IM(1) — 9 errors**:
- Ciara: 1 blank (Nov01 — re-query returns wrong time; structural)
- Kyleigh: 2 errors (Oct28 x/shift swap; Oct30 Thu cluster)
- Seena: 1 error (Oct30 Thu cluster)
- Victor: 1 error (Oct30 Thu cluster)
- Halle: 1 error (Oct30 Thu cluster)
- Brittney: 1 error (Oct30 Thu cluster)
- Cyndee: 1 error (Oct30 Thu cluster)
- Jenny: 1 TIME-MISREAD (Oct26 Sun)

**IM(2) — 12 errors**:
- Athena: 7 MISSING — OCR + LLM both blind to her row (trainee-row styling); hard ceiling
- Tori: 3 TIME-MISREAD / x-swap (4/7)
- Halle: 1 TIME-MISREAD (Nov28)
- Ciara: 1 x-swap (Nov26)

**IM(3) — 1 error**:
- Franny Sep24: single stochastic misread (x vs 1:30-6:00)

**Dominant remaining**: Athena invisibility (7) + Thu Oct30 column cluster (6) + Tori misreads (3) + individual misreads (3) + Ciara Nov01 (1)

## Employee Scores (latest run — session 16)

| Employee | IM(1) | IM(2) | IM(3) | Total |
|----------|-------|-------|-------|-------|
| Andee | 7/7 | 7/7 | 7/7 | 21/21 (100%) |
| Athena | — | 0/7 | — | 0/7 (0%) ← hard ceiling |
| Brittney | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
| Ciara | 5/7 | 6/7 | — | 11/14 (79%) |
| Cyndee | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
| Franny | 7/7 | 7/7 | 6/7 | 20/21 (95%) |
| Halle | 6/7 | 6/7 | 7/7 | 19/21 (90%) |
| Jenny | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
| Kyleigh | 5/7 | 7/7 | 7/7 | 19/21 (90%) |
| Megan | — | — | 7/7 | 7/7 (100%) |
| Raul | — | — | 7/7 | 7/7 (100%) |
| Sarah | 7/7 | 7/7 | 7/7 | 21/21 (100%) |
| Seena | 6/7 | 7/7 | 7/7 | 20/21 (95%) |
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
- Last-employee re-query (truncation + blank-last cases) — fixed Ciara Oct31/Oct29
- Penultimate duplicate-value re-query (row-swap detection) — fixed Seena Oct29

## Anti-Patterns — OCR Pre-Fill

**OCR dash-drop repair is blocked**: WinRT OCR drops dashes from time ranges ("12:00-4:30" → "12:00430"). Implementing `OcrDashDropRegex` to reconstruct these causes regression because:
- 5 of 11 IM(1) employees have no Y anchor in `nameToYEarly` (OCR can't read those names)
- Their shift cells fall within ≤20px of other employees' Y-centers causing false assignments
- Fix requires computing cell boundaries from OCR row detection, not just name-column Y matching
- **Do not attempt until all employees have reliable Y anchors**
