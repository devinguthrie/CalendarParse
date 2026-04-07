# CalendarParse — Copilot Workspace Instructions

## Doc Update Rule (MANDATORY)

After **every** completed experiment — meaning any benchmark run that produces a final score — you MUST update both doc files before ending the session or declaring the task done:

1. **`.github/docs/experiment-log.md`** — add a narrative section under "Phase Details" with: what was tried, the score result, root cause analysis, and why it was committed or reverted.
2. **`.github/docs/next-session-plan.md`** — update **in-place**: Current State table (score), Remaining Errors section, Hard Ceiling Analysis table, and Anti-Patterns table. **Do NOT add session summary sections** — next-session-plan.md is forward-looking only; history lives in experiment-log.md.

**Do not wait until the user asks.** Treat doc updates as the final step of every experiment, the same as reverting bad code. If you're ending a session without having updated the docs, that is a bug.

If an experiment is REVERTED, still document it in experiment-log.md with a "REVERTED" note and brief narrative. Update next-session-plan.md only if the revert changed the score, remaining errors, or anti-patterns.

## Project Context

- **Language**: C# / .NET 10, .NET MAUI + CLI
- **Goal**: Extract employee shift schedules from phone photos of printed grid calendars
- **Pipeline**: `HybridCalendarService.cs` — OCR → LLM names → per-day strip LLM → x-marks → OCR name supplement → holiday heuristic
- **Model**: `qwen2.5vl:7b` via Ollama, `temperature=0.0` (deterministic)
- **Test set**: 5 images, 434 shifts total. Current best: **394/434 (90.8%)**
- **Experiment log**: `.github/docs/experiment-log.md`
- **Next session plan**: `.github/docs/next-session-plan.md`

## Benchmark Commands

```powershell
# Full benchmark (all 5 images)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object "CalendarParse\calander-parse-test-imgs\benchmark-output.txt"

# Score summary only
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Select-String "Overall|IM \("

# Single image (faster iteration) — tmp dirs live under calander-parse-test-imgs
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs\tmp-im4" --test --model qwen2.5vl:7b
```

## One-Variable Rule

Change exactly **one thing** per experiment. If a benchmark regresses with two changes applied, you cannot attribute the cause — you'll waste a full session isolating it.
