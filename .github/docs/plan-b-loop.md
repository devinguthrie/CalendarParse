# Plan B — Meta-Agent Improvement Loop

_Written: 2026-03-26 (session 14). Updated: 2026-03-30 (session 16)._

## Goal

Automate the hypothesis-test-commit cycle that has been done manually across 52 phases.
Current: 230/252 (91.3%) with no `--known-names` required.
Target: 245/252 (97%+).

## Phase A — Error Taxonomy (UPDATED 2026-03-30)

Current errors against 252-shift test set (3 images):

**IM(1) — 9 errors**:
- Thu Oct30 column swaps (6): Cyndee, Victor, Halle, Kyleigh, Seena, Brittney — column boundary lands in Wed
- Ciara Nov01 blank (1): re-query fires but returns wrong time "9:30-2:00"; structural
- Jenny Oct26 TIME-MISREAD (1): Sun shift digit confusion
- Kyleigh Oct28 x/shift swap (1)

**IM(2) — 12 errors**:
- Athena: 7 MISSING — both OCR and LLM blind to her row (trainee-row styling); hard ceiling ~3/7 even if detected
- Tori: 3 errors (TIME-MISREAD / x-swap)
- Halle Nov28: 1 TIME-MISREAD
- Ciara Nov26: 1 x/shift swap

**IM(3) — 1 error**:
- Franny Sep24: single stochastic misread

Systematic patterns:
- **Athena invisibility** (7 of 12 IM(2) errors) — hard ceiling, structural
- **Thu Oct30 bad column** (6 IM(1) errors) — `ComputeDayColBoundsFromOcr()` midpoint bug
- **Ciara/Tori individual misreads** (5 errors) — last-row and row-boundary issues
- **TIME-MISREAD** (~3 errors) — LLM digit confusion

## Phase B — The Loop

### Architecture

```
benchmark-loop.ps1
│
├── run_benchmark()           → structured error list
├── build_context()           → errors + current prompt text + tried_changes.json
├── call_meta_agent()         → ProposedChange JSON (Claude API)
├── validate_change()         → search must match exactly 1 location in target file
│                               EARLY EXIT on malformed (no retries — saves API calls)
├── apply_change()            → git checkout -b improvement-loop-{N} + string-replace
├── dotnet build + test       → new score
├── compare()
│   ├── improvement: git merge --no-ff master, delete branch, append to loop-results.md
│   └── regression:  git branch -D improvement-loop-{N} (revert), add to tried_changes.json
└── loop until max_iterations or score ≥ target
```

### Files

| File | Purpose |
|------|---------|
| `benchmark-loop.ps1` | Orchestration script — main entry point |
| `tried_changes.json` | Deduplication store — never retry a failed hypothesis |
| `loop-results.md` | Machine-generated win log (do NOT manually edit) |

### ProposedChange Schema

```json
{
  "change_type": "prompt_string | threshold | regex | algorithm | ocr_logic | crop_geometry | postprocess",
  "file": "CalendarParse.Cli/Services/HybridCalendarService.cs",
  "search": "<exact string — must match exactly once>",
  "replace": "<replacement>",
  "targets_error_type": "WRONG_VALUE | WRONG_BLANK | SPURIOUS_VALUE | EXTRA_EMPLOYEE",
  "rationale": "One sentence"
}
```

### tried_changes.json Schema

```json
[
  {
    "id": 1,
    "change_type": "prompt_string",
    "file": "CalendarParse.Cli/Services/OllamaCalendarService.cs",
    "search_preview": "<first 60 chars>",
    "rationale": "...",
    "score_before": 227,
    "score_after": 225,
    "outcome": "reverted | malformed_proposal",
    "timestamp": "2026-03-29T22:00:00Z"
  }
]
```

### Structural Targets (priority order)

**1. Thu Oct30 column boundary (6 errors)** — `ComputeDayColBoundsFromOcr()` midpoint logic
or `CropAndStitch()` left-edge calculation causes the Thu strip to include part of the Wed column.
This is the single largest winnable cluster. Inspect the midpoint calculation for the Oct30 day.

**2. Ciara Nov01 blank (1 error)** — last-employee re-query fires correctly but returns
"9:30-2:00" instead of "12:00-5:30"; the strip crop for that column may need to be expanded
downward by a few pixels so the bottom row is fully visible.

**3. Tori TIME-MISREAD (3 errors, IM(2))** — may be related to row boundary near bottom of
schedule. Re-query changes are safer than main strip prompt changes (Tori is sensitive).

**4. TIME-MISREAD (individual: Jenny, Halle, Kyleigh)** — strip prompt doesn't require both
start AND end of time range; also check `TrailingHours` regex for unintended truncation.

**5. Numeric thresholds** (inline literals — verify exactly-once match before proposing):
- Holiday heuristic cutoff (currently 0.80) → try 0.70, 0.75, 0.85
- Levenshtein distance bound (currently ≤ 2)
- Y-band grouping threshold (currently 14px) — affects name fragment grouping

### Known Loop Failure Mode — Malformed Proposals

All 12 benchmark-loop iterations to date produced `malformed_proposal` outcomes. The meta-agent
generates `search` strings that either don't exist or appear more than once in `HybridCalendarService.cs`.
The system prompt needs to be updated with:
1. The current 22-error taxonomy (replacing the stale 25-error one)
2. Explicit instruction to use short, unique anchor strings (method names, not prose)
3. Warning that search must match EXACTLY ONCE — use surrounding context if needed

### Guardrails

1. **Branch isolation** — pre-delete `improvement-loop-{N}` if it exists before creating
2. **Deduplication** — `tried_changes.json` prevents retrying identical hypotheses
3. **Acceptance gate** — `new_score > current_score` (strict, ≥1 shift improvement)
4. **Dead-end exit** — if `max_iterations` iterations yield zero wins → write RANDOM_RESIDUAL
   to `loop-results.md`; errors are likely irreducible with prompt/heuristic changes
5. **Merge strategy** — `git merge --no-ff` (merge commit, not squash), then `git branch -D`
6. **Malformed proposal early-exit** — if search matches 0 or >1 locations → log `malformed_proposal`,
   immediately skip to next iteration WITHOUT retrying (saves 2 wasted API calls per malformed proposal)
7. **JSON parse errors DO retry** — transient API/formatting issues up to `$maxRetries` times
8. **Dry-run mode** — `-DryRun` shows proposed change without applying

### Invocation

```powershell
# Dry-run first (verify meta-agent quality)
.\benchmark-loop.ps1 -DryRun

# Overnight run
.\benchmark-loop.ps1 -MaxIterations 50 -TargetScore 245

# Score check after run
Get-Content loop-results.md
```

### Runtime Estimate

~6-12 min per iteration (3 images × ~55s/image = ~2.75 min benchmark + build time).
Expected: 40-80 iterations overnight.

**Note**: The `--known-names` flag is no longer passed by the benchmark runner — names are
discovered dynamically. The `$KnownNames` parameter in `benchmark-loop.ps1` is preserved for
backwards compatibility but should be removed from `Invoke-Benchmark` calls.

### Meta-Agent System Prompt — Key Sections

The script sends to `claude-sonnet-4-6`:

```
Current accuracy: {N}/252 ({pct}%)
Baseline: 227/252 (90.1%)
Target: 245/252

FAILING CELLS:
- {employee} {day}: got "{got}" expected "{expected}" [{type}]
...

TRIED CHANGES (do not repeat):
...

FULL CONTENT OF CalendarParse.Cli/Services/HybridCalendarService.cs:
---
{file content}
---
```

### Known Anti-Patterns (must stay in system prompt)

- Anti-shift warning in `ExtractColumnAsync` (harmful in single-col context; −8 shifts)
- Anti-shift warning at TOP of rules (−27 shifts; must stay at END)
- Vote reweighting at temp=0 (amplifies errors)
- ISO date keys (−11 pts)
- CSV/pipe output format (model copies examples)
- Pass 2b / name column strip as separate LLM crop (hallucinates variant names; zero net benefit)
- `--known-names` parameter injection (no longer architecturally needed; don't add back)
- Image preprocessing (grayscale, CLAHE) — destroys red ink signal
- Resize/downscale — destroys digit legibility
