# CalendarParse — Claude CLI Instructions

## Doc Update Rule (MANDATORY)

After **every** completed experiment (any benchmark run with a final score), update both:
1. `.github/docs/experiment-log.md` — add summary table row + narrative section
2. `.github/docs/next-session-plan.md` — update score, remaining errors, anti-patterns

If reverted, still document it. Do not wait until the user asks.

## Quick Reference

- **Current best**: 377/434 (86.9%) — `qwen2.5vl:7b`, temp=0, no `--known-names`
- **Anti-patterns**: see `.github/docs/next-session-plan.md` before proposing anything
- **One-variable rule**: change exactly one thing per experiment
- **DO NOT MODIFY**: `OllamaCalendarService.cs` (frozen vision pipeline)

## Benchmark Commands

```powershell
# Full benchmark
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object "CalendarParse\calander-parse-test-imgs\benchmark-output.txt"

# Score summary only
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Select-String "Overall|IM \("

# Single image — tmp dirs live under calander-parse-test-imgs
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs\tmp-im4" --test --model qwen2.5vl:7b
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
