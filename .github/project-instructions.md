# CalendarParse – Copilot Instructions

## Project Overview

.NET MAUI app (Android primary) that photographs work schedule tables and extracts per-employee shift data as JSON. CLI tool (`CalendarParse.Cli`) drives benchmarking against ground-truth `.answer.json` files via Ollama vision models.

## Build & Run

```bash
dotnet build -f net10.0-android          # Primary target
dotnet build -f net10.0-windows10.0.19041.0
```

> Windows uses `WindowsPackageType=None` (unpackaged). `XA0141` warnings from ML Kit — ignore.

## Repository Structure

```
CalendarParse.Cli/
  Program.cs                      Entry point + scoring logic
  Services/
    HybridCalendarService.cs      ACTIVE — hybrid OCR+LLM pipeline (89.9%)
    OllamaCalendarService.cs      Vision-only pipeline (78.0%) — DO NOT MODIFY
    WindowsWinRtOcrService.cs     WinRT OCR — provides element positions for hybrid
    WindowsImagePreprocessor.cs   OpenCV preprocessing (unused in current pipelines)
    WindowsOcrService.cs          Tesseract OCR wrapper
    WindowsTableDetector.cs       DEAD CODE — grid detection fails on phone photos
    DebugImageWriter.cs           Debug image output

CalendarParse.Core/               Shared models + service interfaces
CalendarParse/
  calander-parse-test-imgs/
    IM (1).jpg + .answer.json     11 employees, 77 shifts, Oct 26–Nov 1 2025
    IM (2).jpg + .answer.json     13 employees, 91 shifts, Nov 23–29 2025
```

## Active Pipeline — Hybrid (`HybridCalendarService.cs`, 89.9%)

OCR-based column detection + per-day strip LLM. No grid detector.

1. **WinRT OCR** → element positions → find day-name header tokens → column x-boundaries via `DayIndexMap` + `DayPrefixes` (30+ variants, 3-char prefix matching)
2. **Per-day**: crop [name column] + [target day column] → `CropAndStitch` HConcat → send to LLM
3. **Post-processing**: ISO date cascade fix (strip date-as-first-element); RTO/PTO uniform-column holiday heuristic (≥80% same value → blank all)
4. **Model**: `qwen2.5vl:7b`, temperature=0.0 (deterministic)

## Vision Pipeline — (`OllamaCalendarService.cs`, 78.0%) — DO NOT MODIFY

5-pass: header → names → 3× row majority vote + 2× column vote → smart re-query → x-marks binary pass. Anti-WRONG-COL warning at end of `ExtractAllShiftsAsync` rules. temperature=0.0.

## Scoring Logic (`Program.cs`)

- Name match: case-insensitive + Levenshtein ≤ 2 fuzzy fallback
- Date match: `NormalizeDate()` zero-pads `YYYY-M-D` → `YYYY-MM-DD`
- Shift match: `ShiftsMatch()` — blank (`""`) ≡ `"x"` (both = "not working"); all other values exact match
- Missing employee = 7 wrong; missing date = 1 wrong

## Ollama Config

- Base URL: `http://localhost:11434`
- `num_ctx`: 16384
- `temperature`: 0.0
- `keep_alive`: -1 (prevent unload between calls)

## Models Tested

| Model | Size | Result |
|-------|------|--------|
| **qwen2.5vl:7b** | 6 GB | **Only viable model** — 89.9% hybrid, 78.0% vision |
| qwen2.5vl:32b | 21 GB | 64.3% — CPU offload kills accuracy+speed |
| llama3.2-vision:11b | 7.8 GB | 23.4% peak — example copying, blank cells |
| gemma3:4b / 27b | 3–17 GB | 0–17% — year hallucination, architectural failure |
| llava:13b, minicpm-v, granite3.2-vision:2b | 2–8 GB | 0% — not viable |

## Invariants — Do Not Change

- `NormalizeDate()` in both services + `Program.cs`
- `RepairTruncatedJson()` fallback
- `EnsureModelLoadedAsync()` warmup
- `keep_alive = -1`
- `--known-names` on all test runs (13 names: Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori)
- Anti-WRONG-COL warning at END of rules in `ExtractAllShiftsAsync` (not top, not in `ExtractColumnAsync`)

## Ground Truth

- **IM (1)**: Oct 26–Nov 1, 2025 — 11 employees, 77 shifts
- **IM (2)**: Nov 23–29, 2025 — 13 employees, 91 shifts (includes Athena, Tori)
- **Corrections applied**: Seena Sun, Kyleigh Tue, Ciara Tue, Victor Tue+Thu
- Shift types: `HH:MM-H:MM`, `RTO`, `PTO`, `x`, `""` (blank)
- Ignore secondary summary table below last employee row

## Documentation Policy

| File | Purpose |
|------|---------|
| `.github/docs/experiment-log.md` | Every phase: model, architecture, score, outcome |
| `.github/docs/next-session-plan.md` | Current state, remaining errors, anti-patterns, commands |
| `.github/docs/testing-strategy.md` | Error categories, current error breakdown, invariants |

Update docs whenever: new model tested, architecture changed, new score baseline, answer.json corrected, or anti-pattern confirmed.
