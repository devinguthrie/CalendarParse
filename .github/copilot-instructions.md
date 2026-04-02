# CalendarParse — Copilot Workspace Instructions

## Doc Update Rule (MANDATORY)

After **every** completed experiment — meaning any benchmark run that produces a final score — you MUST update both doc files before ending the session or declaring the task done:

1. **`.github/docs/experiment-log.md`** — add a row to the summary table and a narrative section under "Phase Details"
2. **`.github/docs/next-session-plan.md`** — update the current state table (score + test set), add the session summary row, rewrite the remaining errors section, update hard ceiling analysis, and add any new anti-patterns

**Do not wait until the user asks.** Treat doc updates as the final step of every experiment, the same as reverting bad code. If you're ending a session without having updated the docs, that is a bug.

If an experiment is REVERTED, still document it — reverted experiments belong in the summary table with a "REVERTED" note and a brief narrative explaining why.

## Project Context

- **Language**: C# / .NET 10, .NET MAUI + CLI
- **Goal**: Extract employee shift schedules from phone photos of printed grid calendars
- **Pipeline**: `HybridCalendarService.cs` — OCR → LLM names → per-day strip LLM → x-marks → OCR name supplement → holiday heuristic
- **Model**: `qwen2.5vl:7b` via Ollama, `temperature=0.0` (deterministic)
- **Test set**: 5 images, 434 shifts total. Current best: **377/434 (86.9%)**
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
