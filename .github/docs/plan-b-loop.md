# Plan B — Meta-Agent Improvement Loop

_Written: 2026-03-26 (session 14). Full design in `~/.gstack/projects/devinguthrie-CalendarParse/devin-master-design-20260326-171612.md`_

## Goal

Automate the hypothesis-test-commit cycle that has been done manually across 49 phases.
Target: push from 89.9% (151/168) to 95%+ by having a meta-agent (Claude API) generate
prompt/heuristic changes, test them, and keep winners overnight.

## Phase A — Error Taxonomy (DONE)

Already completed from testing-strategy.md. The 17 errors are:
- **TIME-MISREAD (6):** Jenny Sun, Victor Tue, Kyleigh Tue, Brittney Thu, Halle Nov28, Tori Nov23
- **X-SWAP (6):** Thu Oct30 column — Cyndee, Victor, Halle, Kyleigh, Seena, Sarah
- **BLANK/truncation (3):** Ciara last-row — Oct29, Oct31, Nov01
- **SPURIOUS (1):** Franny Oct28
- **SPURIOUS (1):** Seena Oct29

Systematic patterns:
- Thu Oct30 is a bad column (6 of 15 IM1 errors)
- TIME-MISREAD is #1 error class (35% of all failures)
- Ciara is always last row — truncation is structural

## Phase B — The Loop

### Architecture

```
benchmark-loop.ps1
│
├── run_benchmark()           → structured error list
├── build_context()           → errors + current prompt text + tried_changes.json
├── call_meta_agent()         → ProposedChange JSON (Claude API)
├── validate_change()         → search must match exactly 1 location in target file
├── apply_change()            → git checkout -b improvement-loop-{N} + string-replace
├── dotnet build + test       → new score
├── compare()
│   ├── improvement: git merge --no-ff master, delete branch, append to loop-results.md
│   └── regression:  git branch -D improvement-loop-{N} (revert), add to tried_changes.json
└── loop until max_iterations or score ≥ target
```

### Files to Create

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
    "score_before": 151,
    "score_after": 149,
    "outcome": "reverted | malformed_proposal",
    "timestamp": "2026-03-26T22:00:00Z"
  }
]
```

### Structural Targets (highest priority)

The meta-agent may propose any type of code change. Priority order:

**1. Thu Oct30 column boundary (6 errors)** — `ComputeDayColBoundsFromOcr()` midpoint logic
or `CropAndStitch()` left-edge calculation causes the Thu strip to overlap Wed.

**2. Ciara last-row blank (3 errors)** — strip crop height or LLM stopping one row early;
try expanding crop, last-row prompt instruction, or `empIdx == names.Count - 1` guard.

**3. TIME-MISREAD (6 errors)** — strip prompt doesn't require both start AND end of range;
also check `TrailingHours` regex for unintended truncation.

**4. SPURIOUS (2 errors)** — post-processing filter for values that don't match
time-range / x / RTO / PTO format.

**Numeric thresholds** (inline literals — verify exactly-once match before proposing):
- Holiday heuristic cutoff (currently 0.80) → try 0.70, 0.75, 0.85
- Levenshtein distance bound (currently ≤ 2)
- TimeRangeRegex pattern

### Guardrails

1. **Branch isolation** — pre-delete `improvement-loop-{N}` if it exists before creating
2. **Deduplication** — `tried_changes.json` prevents retrying identical hypotheses
3. **Acceptance gate** — `new_score > current_score` (strict, ≥1 shift improvement)
4. **Dead-end exit** — if `max_iterations` iterations yield zero wins → write RANDOM_RESIDUAL
   to `loop-results.md`; errors are likely irreducible with prompt/heuristic changes
5. **Merge strategy** — `git merge --no-ff` (merge commit, not squash), then `git branch -D`
6. **Validate before apply** — if search matches 0 or >1 locations → log `malformed_proposal`,
   request new proposal (does not count against max_iterations)
7. **Dry-run mode** — `--dry-run` shows proposed change without applying

### Invocation

```powershell
# Dry-run first (verify meta-agent quality)
.\benchmark-loop.ps1 --dry-run --max-iterations 5

# Overnight run
.\benchmark-loop.ps1 --max-iterations 50

# Score check after run
Get-Content loop-results.md
```

### Runtime Estimate

**Calibration required before first overnight run** — time one full iteration.
Expected: ~6-12 min per iteration → 40-80 iterations overnight.
If >20 min/iteration: extract prompts to JSON config (eliminates dotnet build time).

### Meta-Agent Prompt (abbreviated)

The script sends to Claude API (claude-sonnet-4-6):

```
Current accuracy: {N}/168 ({pct}%)
Baseline: 151/168 (89.9%)

FAILING CELLS:
- {employee} {day}: got "{got}" expected "{expected}" [{type}]
...

TRIED CHANGES (do not repeat):
- {rationale}: {description} → scored {score}/168
...

CURRENT PROMPT for {function_name}:
---
{prompt_text}
---

RULES: Propose ONE change. Return JSON only. Target most common error type ({type}: N cases).
Valid error types: WRONG_VALUE, WRONG_BLANK, SPURIOUS_VALUE, EXTRA_EMPLOYEE.
Thresholds are named C# constants. Do not repeat tried changes.
```

### Known Anti-Patterns (do not propose)

The meta-agent system prompt must include the anti-patterns from `next-session-plan.md`
so it doesn't re-discover dead ends. Key ones:
- Anti-shift warning in `ExtractColumnAsync` (harmful in single-col context)
- Anti-shift warning at TOP of rules (−27 shifts)
- Vote reweighting at temp=0 (amplifies errors)
- ISO date keys (−11 pts)
- CSV/pipe output format (model copies examples)
