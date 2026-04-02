# Plan B — Meta-Agent Improvement Loop

_Written: 2026-03-26 (session 14). Updated: 2026-04-01 (session 18)._

## Goal

Automate the hypothesis-test-commit cycle that has been done manually across 60 phases.
Current: 377/434 (86.9%) with no `--known-names` required.
Target: 400/434 (92%+).

## Phase A — Error Taxonomy (UPDATED 2026-04-01)

Current errors against 434-shift test set (5 images):

**IM(1) — 9 errors** (68/77):
- Thu Oct30 highlighted-cell cluster (6): Kyleigh, Brittney, Cyndee, Victor, Halle, Seena — **irreducible with re-query** (confirmed irreducible: green highlighted Andee cell is visual anchor; temp=0 = same wrong answer always)
- Kyleigh Oct28 x/shift swap (1)
- Ciara Nov01 blank (1)
- Jenny Oct26 TIME-MISREAD (1)

**IM(2) — 13 errors** (78/91):
- Athena: 7 MISSING — OCR blind to her row (zero fragments); LLM non-deterministically finds "Athena(train)" → normalized, but not reliable; hard ceiling
- Tori: 4 errors (TIME-MISREAD / x-swap)
- Halle: 1 TIME-MISREAD
- Ciara: 1 x/shift swap

**IM(3) — 1 error** (83/84):
- Franny Sep24: single stochastic misread

**IM(4) — 13 errors** (78/91):
- Aug2 Sat: ~9 errors — holiday detector blanks column; ground-truth is all "RTO"; OCR salvage recovers some
- Mon Jul28: 3 errors — Andee/Brittney/Cyndee get "RTO" instead of time-ranges; leading-RTO re-query tried and REVERTED (−5)
- Franny Thu: 1 TIME-MISREAD

**IM(5) — 21 errors** (70/91):
- Jul26: 11 errors — uniform-RTO fires → narrow strip → no time-ranges → holiday detector blanks; most employees actually had time-ranges
- Jul24: 4 errors — clean 1-row offset for first 4 employees
- Misc: 6 individual TIME-MISREADs

Systematic patterns:
- **Thu Oct30 cluster** (6 errors, IM(1)) — highlighted-cell visual anchor; **irreducible with re-query**
- **IM(5) Jul26 holiday misfire** (11 errors) — largest fixable cluster; holiday detector fired on non-holiday column
- **Athena invisibility** (7 errors, IM(2)) — OCR + LLM both blind; hard ceiling
- **IM(4) Aug2 Sat** (~9 errors) — holiday blanking overwrites correct "RTO" ground-truth
- **Individual TIME-MISREADs** (~6 errors) — LLM digit/boundary confusion

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
| `loop-results.md` | Machine-generated win log — auto-created by script, do NOT manually edit |

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

**1. ~~Thu Oct30 cluster (6 errors)~~ — CONFIRMED IRREDUCIBLE WITH RE-QUERY** — Root cause is a green highlighted Andee cell that acts as visual anchor. At temp=0, all re-queries return same wrong answer. Do NOT propose re-query changes targeting this cluster.

**2. IM(5) Jul26 column (11 errors) — HIGHEST PRIORITY FIXABLE CLUSTER** — Holiday detector misfire: uniform-RTO fires → narrow strip retry returns no time-ranges → holiday-blanks the whole column. Most employees actually had time-ranges on Jul26. Investigate: does narrow strip still contain hours sub-column for IM(5)? Does the model genuinely return all-RTO even narrow?

**3. IM(4) Aug2 Sat (~9 errors)** — Holiday blanking overwrites correct "RTO" ground-truth. Possible fix: if OCR finds "RTO" in ≥N shift cells after blanking, restore as OCR-confirmed RTO instead of blank. Risk: may break IM(2) Nov27 Thanksgiving (where holiday-blank is correct).

**4. Tori TIME-MISREAD (4 errors, IM(2))** — may be related to row boundary near bottom of schedule. Re-query changes are safer than main strip prompt changes.

**5. Ciara Nov01 blank (1 error)** — last-employee re-query fires but returns wrong time. Strip crop may need to be expanded downward.

**6. Numeric thresholds** (inline literals — verify exactly-once match before proposing):
- Holiday heuristic cutoff (currently 0.80) → try 0.70, 0.75, 0.85
- Levenshtein distance bound (currently ≤ 2)
- Y-band grouping threshold (currently 14px) — affects name fragment grouping

### Known Loop Failure Mode — Malformed Proposals

All 12 benchmark-loop iterations to date produced `malformed_proposal` outcomes. The meta-agent
generates `search` strings that either don't exist or appear more than once in `HybridCalendarService.cs`.
The system prompt needs to be updated with:
1. The current 57-error taxonomy (replacing the stale 22-error one — now on 5 images/434 shifts)
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
.\benchmark-loop.ps1 -MaxIterations 50 -TargetScore 400

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
Current accuracy: {N}/434 ({pct}%)
Baseline: 374/434 (86.2%)
Target: 400/434

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
- Adjacent-duplicate re-query (all pairs) — false positives on legitimate shared values (−10 pts)
- Triplet detection re-query (≥3 same value, even TimeRangeRegex-restricted) — correct detection but same wrong answer returned at temp=0 due to highlighted-cell visual anchor; also false positives in IM(3)/IM(1) (−5 pts)
- Leading-RTO re-query (first 3 employees all RTO/PTO) — −5; model erroneously returns RTO for first 3 on legitimate columns; pattern indistinguishable from contamination
