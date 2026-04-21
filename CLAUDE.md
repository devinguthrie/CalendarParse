# CalendarParse — Claude CLI Instructions

## Doc Update Rule (MANDATORY)

After **every** completed experiment (any benchmark run with a final score):
1. **CREATE** `.github/docs/memory/sessions/session-N.md` — new file, never edit prior ones. Include score before/after, what was tried, outcome, root cause.
2. **APPEND** one JSON line to `experiments.jsonl` (repo root, append-only). Fields: `id`, `change_type`, `description`, `score_before`, `score_after`, `total_shifts`, `outcome`, `notes`, `timestamp`.
3. **UPDATE** `.github/docs/memory/state.md` in-place — scores, per-image breakdown, remaining errors.
4. **APPEND** to `.github/docs/memory/anti-patterns.md` if new anti-patterns found.
5. **APPEND** to `.github/docs/memory/rejected-models.md` if new models rejected.
6. **APPEND** to `.github/docs/memory/key-lessons.md` if new architectural lessons learned.

If reverted, still perform all steps with `outcome: reverted`. Do not wait until the user asks.

## Quick Reference

- **Current best**: 408/434 (94.0%) — `glm-ocr` via Ollama, `--glm-ocr` flag, temp=0
- **Anti-patterns**: `.github/docs/memory/anti-patterns.md`
- **Rejected models**: `.github/docs/memory/rejected-models.md`
- **Remaining errors**: `.github/docs/memory/state.md`
- **One-variable rule**: change exactly one thing per experiment
- **DO NOT MODIFY**: `OllamaCalendarService.cs` (frozen vision pipeline)

## Benchmark Commands

```powershell
# GLM-OCR full benchmark (best: 408/434 = 94.0%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --glm-ocr --test --model glm-ocr 2>&1 | Tee-Object benchmark-output.txt

# Score summary only
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --glm-ocr --test --model glm-ocr 2>&1 | Select-String "Overall|IM \("

# Hybrid pipeline (parity regression: 267/434 as of session 43)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object hybrid-output.txt
```

---

# gstack

Use the `/browse` skill from gstack for all web browsing. Never use `mcp__claude-in-chrome__*` tools directly.

Available gstack skills:
- `/office-hours` - Office hours session
- `/plan-ceo-review` - CEO review planning
- `/plan-eng-review` - Engineering review planning
- `/plan-design-review` - Design review planning
- `/design-consultation` - Design consultation
- `/review` - Code review
- `/ship` - Ship a feature
- `/land-and-deploy` - Land and deploy
- `/canary` - Canary deployment
- `/benchmark` - Benchmarking
- `/browse` - Web browsing (use this for all web browsing)
- `/qa` - QA testing
- `/qa-only` - QA only
- `/design-review` - Design review
- `/setup-browser-cookies` - Set up browser cookies
- `/setup-deploy` - Set up deployment
- `/retro` - Retrospective
- `/investigate` - Investigation
- `/document-release` - Document a release
- `/codex` - Codex
- `/cso` - CSO
- `/autoplan` - Automated planning
- `/careful` - Careful mode
- `/freeze` - Freeze
- `/guard` - Guard
- `/unfreeze` - Unfreeze
- `/gstack-upgrade` - Upgrade gstack
