# CalendarParse — Copilot Workspace Instructions

## Doc Update Rule (MANDATORY)

After **every** completed experiment — meaning any benchmark run that produces a final score — you MUST perform ALL of the following steps before ending the session or declaring the task done:

1. **CREATE** `.github/docs/memory/sessions/session-N.md` — a new file for this session (never edit prior session files). Include: score before/after, what was tried, outcome (committed/reverted), root cause analysis.
2. **APPEND** one JSON line to `experiments.jsonl` (repo root, append-only). Fields: `id`, `change_type`, `description`, `score_before`, `score_after`, `total_shifts`, `outcome`, `notes`, `timestamp`.
3. **UPDATE** `.github/docs/memory/state.md` **in-place** — update the Current Scores table, Per-Image breakdown, and Remaining Errors section.
4. **APPEND** a row to `.github/docs/memory/anti-patterns.md` if any new anti-patterns were discovered.
5. **APPEND** a row to `.github/docs/memory/rejected-models.md` if any new models were evaluated and rejected.
6. **APPEND** to `.github/docs/memory/key-lessons.md` if any new architectural lessons were learned.

**Do not wait until the user asks.** Treat doc updates as the final step of every experiment, the same as reverting bad code.

If an experiment is REVERTED, still perform all steps above with `outcome: reverted`. The session file and JSONL entry capture the narrative. Do NOT edit `next-session-plan.md` for session history.

## Project Context

- **Language**: C# / .NET 10, .NET MAUI + CLI
- **Goal**: Extract employee shift schedules from phone photos of printed grid calendars
- **Pipeline**: `HybridCalendarService.cs` — OCR → LLM names → per-day strip LLM → x-marks → OCR name supplement → holiday heuristic
- **Model**: `qwen2.5vl:7b` via Ollama, `temperature=0.0` (deterministic)
- **Test set**: 5 images, 434 shifts total. Current best: **408/434 (94.0%)** (`glm-ocr`, `--glm-ocr` flag)
- **Experiment records**: `experiments.jsonl` (repo root, append-only JSONL)
- **Session files**: `.github/docs/memory/sessions/session-N.md`
- **Current state**: `.github/docs/memory/state.md`
- **Anti-patterns**: `.github/docs/memory/anti-patterns.md`
- **Rejected models**: `.github/docs/memory/rejected-models.md`
- **Next session plan**: `.github/docs/next-session-plan.md` (forward-looking only — Tier 1–4 + housekeeping)

## Benchmark Commands

```powershell
# GLM-OCR full benchmark (best: 408/434 = 94.0%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --glm-ocr --test --model glm-ocr 2>&1 | Tee-Object benchmark-output.txt

# Score summary only
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --glm-ocr --test --model glm-ocr 2>&1 | Select-String "Overall|IM \("

# Hybrid pipeline (parity regression: 267/434 as of session 43)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object hybrid-output.txt

# Single image (faster iteration)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs\IM (3).jpg" --glm-ocr --test
```

## One-Variable Rule

Change exactly **one thing** per experiment. If a benchmark regresses with two changes applied, you cannot attribute the cause — you'll waste a full session isolating it.
