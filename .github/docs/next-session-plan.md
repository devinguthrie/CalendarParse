# CalendarParse — Next Session Plan

_Last updated: 2026-03-13 (session 8)_

---

## Current State

- **Best confirmed score**: 117/168 = **69.6%** (`qwen2.5vl:7b` + `--known-names`, Phase 30/P16+P18 guards, **deterministic × 3**)
- **Note**: Phase 21's 71.4% (120/168) was a single-run statistical outlier at temperature=0.1; the true mean at that config was ~66%.
- **Code state (session 8)**: `OllamaCalendarService.cs` is **P19** — P16 blank fallbacks + P18 quality guards active; OCR garbage sanitization active; systematic drift detection block present but non-firing in practice.
- **Temperature**: 0.0 (deterministic — every run identical).
- **benchmark.ps1**: `-KnownNames` parameter supported; always pass the full 13-name list.
- **Hardware**: 32 GB VRAM confirmed.
- **Dominant remaining failure**: WRONG-COL (~50% of errors) — systematic +1 right-shift affecting bottom-of-table employees and rows near the Thanksgiving blank column.

---

## Future Experiments

### Two-shot self-anchoring (Top priority)

**Hypothesis**: Split extraction into two sequential questions — Q1: "What are the exact date labels printed at the top of each of the 7 columns?" — Q2: "Using ONLY the labels you identified in Q1, read the shift cells below each label." This forces the model to explicitly anchor to visible text before reading values, breaking column-counting misalignment at its source.

**Potential impact**: Could fix systematic right-shift without additional API calls (replaces one of the 3 row runs). Low-cost to implement — just a prompt change.

**How to try**: Modify `ExtractAllShiftsAsync` to issue a preliminary `ExtractColumnHeadersAsync` call first, then inject the returned date labels verbatim into the main extraction prompt.

---

### CSV output format (High priority)

**Hypothesis**: Instead of JSON with day-name keys, ask the model to return a CSV with the column headers from the image (e.g. `10/26,10/27,10/28...`). Benefits: (a) tabular format mirrors image structure, (b) column counts are verifiable — a row with wrong column count is immediately detectable, (c) forces the model to use image-visible date labels as column identifiers rather than abstract day names.

**Risk**: CSV output reliability is lower than JSON in LLM training data; trailing empty cells are often silently dropped. Test carefully.

**Note**: Distinct from "Pipe/CSV output format" anti-pattern (which copied example values). This approach uses image-extracted headers rather than a fixed template.

---

### Dual output cross-reference (High priority)

**Hypothesis**: In a single call, ask for BOTH a per-employee row view AND a per-day column view (two JSON objects in one response). Cross-reference: if per-employee says Franny/Mon=`10:00-6:30` AND per-day says Mon[Franny's position]=`10:00-6:30`, high confidence. Disagreements flag drift cells for targeted re-query. No extra API calls.

**Risk**: If the model drifts, it may drift consistently in both views (confirming the wrong answer twice). Partially duplicates the existing 3-row + 2-column architecture — but doing it in a single call with explicit cross-reference logic in the prompt may catch edge cases the majority vote misses.

---

### `qwen2.5vl:72b` (Highest model priority)

**Hypothesis**: Same model family as current best (qwen2.5vl:7b), ~10× more parameters. Expected significantly better table comprehension, fewer WRONG-COL and TIME-MISREAD errors.

**How to run**:
```powershell
# Pull — Q4_K_M is default (~44 GB, may need CPU offload on 32 GB)
ollama pull qwen2.5vl:72b

# If VRAM is tight, try Q3_K_M (~30 GB) instead:
# ollama pull qwen2.5vl:72b:Q3_K_M  (check tag availability first)

# Single run first — if > 55% then run 3× to confirm
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:72b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Tee-Object benchmark-72b-run.txt
```

**Note**: Q4_K_M at ~44 GB will exceed 32 GB of VRAM. Ollama will automatically offload layers to CPU — inference will be slow (~10–20 min per image) but should still function. Try it; if it's too slow, look for a Q3_K_S or Q2_K quantization.

---

### `internvl3:38b` / `internvl2.5:38b` (Medium priority)

**Hypothesis**: InternVL series is specifically trained on document/table OCR. Q4 quantization ~24 GB — fits comfortably in 32 GB.

**How to run**:
```powershell
ollama search internvl   # check what's available and which tags exist
ollama pull internvl2.5:38b   # or internvl3:38b if listed

dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model internvl2.5:38b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Tee-Object benchmark-internvl-run.txt
```

---

### Other models worth checking

- `phi4-vision` (~14B, Q4 ~8 GB) — different architecture, cheap to test
- `mistral-small3.1:24b` — good structured output, Q4 ~14 GB

---

## Hybrid WinRT OCR Pipeline (high ceiling, more work)

**Why this matters**: ~50% of all remaining errors are WRONG-COL — the model visually miscounts columns. This is an inherent limitation of asking a vision model to count across a wide dense table. A deterministic approach bypasses it entirely.

**The approach**:
1. Run `WindowsWinRtOcrService.cs` on the image → get recognized text + bounding boxes for every fragment
2. Run `WindowsTableDetector.cs` (OpenCV grid detection) → get grid cell boundaries (row × col)
3. Intersect: assign each OCR fragment to its grid cell by spatial overlap
4. Cells matching `\d+:\d+[-–]\d+:\d+` (time ranges) → trust OCR result directly; no LLM call needed
5. Cells that are blank, contain a single-character symbol (x), or were unrecognized → pass the cell crop to the vision LLM for disambiguation

**What this fixes**: WRONG-COL errors disappear because physical pixel positions → column indices is deterministic. TIME-MISREAD on time ranges also decreases (WinRT OCR reads typed digits reliably).

**Caveats**:
- X marks (hand-drawn) will fall into the LLM path — that's fine
- Grid detection quality is the critical dependency; test with `WindowsTableDetector` debug output first
- More implementation work than a model swap; save for after model experiments are exhausted

**Starting point**: [CalendarParse.Cli/Services/WindowsWinRtOcrService.cs](../CalendarParse.Cli/Services/WindowsWinRtOcrService.cs) and [WindowsTableDetector.cs](../CalendarParse.Cli/Services/WindowsTableDetector.cs)

---

## Benchmark Script Notes

The `benchmark.ps1` script now supports `--known-names` via the `-KnownNames` parameter (added session 7):

```powershell
# Standard 3-run test with known names via benchmark script
.\benchmark.ps1 -Models "qwen2.5vl:7b" -Modes "none" -Runs 3 -KnownNames "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" -OutFile "benchmark-pXX.txt" | Tee-Object "benchmark-pXX-log.txt"
```

---

## Fine-Tuning (Deferred — not worth it yet)

With 32 GB VRAM, LoRA fine-tuning of `qwen2.5vl:7b` is hardware-feasible via [unsloth](https://github.com/unslothai/unsloth) (~20–24 GB for 4-bit LoRA). However, only 2 labeled images (~168 annotated cells) exist. Effective fine-tuning requires 50–200+ labeled examples. **Do not pursue until more labeled data exists.**

---

## Anti-Patterns — Never Retry

| Anti-pattern | Why |
|---|---|
| gemma3 (any size) | Year hallucination on IM(1) — architectural |
| llava:13b, minicpm-v, granite3.2-vision:2b | Wrong dates or all-x output — not viable |
| Anchor-guided re-extraction | Overwrites correct values; −35 pts |
| Pipe/CSV output format | Model copies example values for all cells |
| `--resize` downscaling | Destroys fine digit legibility; −13 pts |
| `--ensemble llama3.2-vision:11b` | Fills blanks with "x", not time ranges |
| CLAHE / grayscale / `current` preprocessing | Destroys red ink X-mark color signal or inverts image |
| Batched extraction (split employees across calls) | Second batch returns all blank |

---

## Quick Reference Commands

```powershell
# Standard 3-run test (always use --known-names)
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Tee-Object run-output.txt

# Score only (quick variance check)
1..3 | ForEach-Object {
    dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Select-String "Overall"
}

# Benchmark script (standard 3-run with known-names)
.\benchmark.ps1 -Models "qwen2.5vl:7b" -Modes "none" -Runs 3 -KnownNames "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" -OutFile "benchmark-pXX.txt" | Tee-Object "benchmark-pXX-log.txt"
```
