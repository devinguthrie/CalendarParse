# CalendarParse — Next Session Plan

> **This file is forward-looking only.**
> - Session summaries and experiment narratives belong in `experiment-log.md` — do NOT duplicate them here.
> - After every session: update **Current State** in-place, update **Remaining Errors** in-place, add new anti-patterns, update Research Pipeline status. That is all.
> - Do NOT add "Session N — what happened" sections here.

_Last updated: session 29_

## Current State

| Metric | Value |
|--------|-------|
| **Best score** | **402/434 (92.6%)** — `glm-ocr` model via Ollama, `--glm-ocr` flag |
| Previous best | 394/434 (90.8%) — qwen2.5vl:7b + WinRT OCR hybrid |
| Test set | 5 images, 434 shifts: IM(1)=77, IM(2)=91, IM(3)=84, IM(4)=91, IM(5)=91 |
| Per-image (glm-ocr) | IM(1)=74/77 (96%), IM(2)=90/91 (99%), IM(3)=64/84 (76%), IM(4)=86/91 (95%), IM(5)=88/91 (97%) |
| GLM-OCR pipeline | `GlmOcrCalendarService.cs` — one full-image HTML OCR call, HTML table parser, majority-vote date anchoring, sliding window for 1-cell holiday cols |
| Hybrid pipeline | `HybridCalendarService.cs` — still available via `--model qwen2.5vl:7b` |
| Frozen file | `OllamaCalendarService.cs` P20 — **DO NOT MODIFY** |
| Temperature | 0.0 (deterministic) |
| GLM-OCR ctx size | 32768 — portrait images auto-resized to ≤1344px height (`ResizeForGlmOcrIfNeeded`, Emgu.CV). [Ollama #14171](https://github.com/ollama/ollama/issues/14171) (open — M-RoPE assertion in multi-tile path); remove workaround when fixed. |
| GLM-OCR crash hardening | `keep_alive=0` + `num_predict=20000` in every request. KV-cache shift assertion fires at step 32,769; valid verbose output is ~9,326 tokens; 20,000 cap prevents crash while allowing all valid output. 3/3 benchmark runs stable at 402/434. |

## LLM Call Count Per Image

10–17 calls per image:
- Pass 1: header (1 call)
- Pass 2: names from full image with OCR fragments (1 call)
- Pass 3: one strip per day column (7 calls, some skipped if OCR pre-fills)
- Pass 4: x-marks clarification (1 call, skipped for holiday-blanked columns)
- Re-query: last-employee fires when truncated or last-blank (up to 1/strip)
- Re-query: penultimate fires when duplicate-value anomaly detected (up to 1/strip)
- Re-query: 8:xx narrow strip fires when ≥2 time-ranges start with "8:" (up to 1/strip)

## Remaining Errors (32 total, 402/434, glm-ocr pipeline)

**IM(1) — 3 errors** (74/77):
- Sarah Oct29: got shift expected "x" — model misread
- Jenny Oct26: 9:30-6:00 vs 9:30-6:30 — digit OCR difference
- Kyleigh Oct28: 9:30-6:00 vs 9:30-2:00 — wrong shift end time

**IM(2) — 1 genuine error** (90/91):
- Halle Nov28: got "12:00-4:30" expected "11:00-7:30" — model misread
- (7 "Clara" EXTRA entries for Ciara misspelling — do NOT count against score)

**IM(3) — 20 errors** (64/84):
- Cyndee: 6 wrong shifts — GLM-OCR outputs "Cydee" row (correct) AND phantom "Cyndee" row (wrong); exact match picks wrong row
- Megan: 4 wrong shifts — similar cell-assignment error in portrait orientation
- Kyleigh: 3 shifted/swapped values (got x expected shift, got shift expected x)
- Seena: 3 "xx" cells not read + 1 wrong shift
- Franny: 2 errors (got shift expected x)
- Brittney: 1 error (got shift expected x)

**IM(4) — 5 errors** (86/91):
- Brittney Jul31: got shift expected "x"
- Destiny Jul31: got "" expected "xx" — "xx" not read by model
- Seena Jul30/Jul31/Aug1: got "" expected "xx" — "xx" not read by model

**IM(5) — 3 errors** (88/91):
- Cyndee Jul26: 12:00-8:30 vs 12:00-6:30 — digit OCR difference
- Kyleigh Jul21: 1:00-6:30 vs 1:00-3:30 — wrong shift end time
- Kyleigh Jul25: got "x" expected "RTO" — x/RTO confusion

## Hard Ceiling Analysis (GLM-OCR pipeline)

| Error cluster | Count | Root cause | Fix path |
|---|---|---|---|
| IM(3) "xx" cells (Seena/Destiny) | 4 | Model doesn't read double-X marks as "xx" — outputs empty or wrong shift | Image preprocessing or fine-tuning |
| IM(3) portrait orientation misreads (Cyndee, Megan, Kyleigh) | ~13 | GLM-OCR tile computation different for portrait; cell-alignment errors cause wrong shift values regardless of naming | Phantom dedup is NOT the fix — "Cydee" row itself has wrong shifts. Need better portrait resolution or different tiling. |
| Digit precision diffs (IM(1) Jenny/Kyleigh, IM(5) Cyndee) | 3 | Model reads last digit incorrectly (6:00 vs 6:30, 6:30 vs 2:00) | Fine-tuning or image sharpening |
| Brittney Jul31 / Sarah Oct29 x/shift confusion | 2 | Model reads shift where "x" is printed | Unknown |

**Total remaining: 32 errors. Actual ceiling unclear — IM(3) errors are portrait cell-alignment, not naming; phantom dedup does not help.**

## Next Steps (Priority Order)

1. **"xx" cells (4 errors, IM(3)/IM(4))**: Seena/Destiny show empty where "xx" is expected. Try hybrid fallback: if GLM-OCR cell is empty AND the corresponding WinRT OCR crop contains any "x" token, substitute "xx".
2. **IM(3) portrait quality**: The 20 IM(3) errors are primarily wrong shift VALUES (cell alignment), not naming issues. Phantom dedup does not help — confirmed session 29. Only fix is Ollama bug resolution (full-res portrait) or a different tiling strategy.
3. **Ollama bug revert (future)**: When [Ollama #14171](https://github.com/ollama/ollama/issues/14171) (open — M-RoPE assertion in multi-tile path) is fixed, remove `ResizeForGlmOcrIfNeeded` and test full-resolution portrait. Expected: IM(3) may improve significantly at native resolution.
4. **Remove keep_alive=0 + num_predict after #14171 fix**: Once the Ollama crash is patched upstream, test whether `keep_alive=0` and `num_predict=20000` can be removed without regressions.

## Anti-Patterns — Never Retry

> **Scope**: Pipeline, prompt, and architecture mistakes that would hurt accuracy regardless of model or environment. Things someone could accidentally re-introduce while iterating. Model evaluations ("model X scored below baseline") do NOT belong here — those live in `experiment-log.md` and the Rejected Models table below.

| Anti-pattern | Why |
|---|---|
| Anchor-guided re-extraction | Overwrites correct values; −35 pts |
| Pipe/CSV output format | Model copies example values |
| `--resize` downscaling | Destroys digit legibility; −13 pts |
| CLAHE / grayscale / `current` preprocessing | Destroys red ink X-mark color signal |
| Batched extraction (split employees) | Second batch all blank |
| Two-shot self-anchoring (Q1→Q2) | Biases away from cell reading; −4 shifts |
| Anti-shift warning in `ExtractColumnAsync` | Harmful in single-column context; −8 shifts |
| Anti-shift warning at TOP of rules | −27 shifts; must stay at END |
| Vote reweighting (temp=0 copies) | Amplifies errors; −63 shifts |
| Grid detector on phone photos | Detects text rows not grid lines; 0 cells |
| Strip crop nameXStart→dayXEnd (all cols) | Multi-column contamination |
| Positional `List<(XStart,XEnd)>` for days | Off-by-one; use `Dictionary<int,...>` |
| `--halves` composite merge | Zero effect — WRONG-COL isn't pixel-budget |
| `llm` preprocessing | Noise — 0.2 pts vs none |
| 5+2 votes (more row runs) | Locks in errors at temp=0; −8 pts |
| ISO date keys in JSON | Confuses model vs day-name keys; −11 pts |
| Numbered-column prompt | Destabilizes model; −3.4 pts |
| Suspect-blank re-query | Can't distinguish correct blanks from missed |
| Dual-view cross-reference (6th vote) | Correlated views + blank injection; −13 |
| CSV output for row passes | Parse errors cascade; −39 shifts |
| Drift detector threshold=1 | Fires on normal schedules; −8 pts |
| Pass 2b (name column strip LLM crop) | LLM hallucinates variant names; zero net benefit even with edit-distance filter |
| Leading-RTO re-query (first 3 all RTO/PTO) | −5; fires on legitimate columns — pattern indistinguishable from contamination |
| 80% majority holiday/uniform-RTO check | Blanks Jenny+Kyleigh (correct RTO); OCR salvage recovers only 2/13 cells; net −1 |
| Levenshtein ≤ 2 phantom employee dedup | `Andee` and `Cyndee` are distance 2 (both end in `-dee`); dedup incorrectly drops `Cyndee` as a phantom of `Andee`. IM(3) phantom row is non-deterministic; "Cydee" row itself has wrong shifts when it appears alone. Net: −22 on other images, 0 on IM(3). |
| ocrTimeMap holiday-guard | IM(4) ocrTimeMap = 0 for all 7 columns — WinRT reads time cells as fragments; dead code |
| Retail hint in `OllamaCalendarService.ExtractColumnAsync` | Dead code — hybrid pipeline calls `HybridCalendarService.ExtractColumnFromImageAsync`, not this |

## Rejected Models — Do Not Retest

> Models confirmed below baseline. Full details in `experiment-log.md`. Do not re-run these.

| Model | Best score | Why rejected |
|---|---|---|
| gemma3 (any size) | — | Year hallucination — architectural |
| accounts/fireworks/models/glm-5 | 117/434 (27.0%) | Text-only — no vision; 0 employees per image; do not retest |
| accounts/fireworks/models/qwen3p6-plus | 117/434 (27.0%) | Text-only — no vision; 0 employees per image; do not retest |
| accounts/fireworks/models/qwen3-vl-30b-a3b-thinking | 212/434 (48.8%) | Vision works but thinking chain burns tokens; only 2/7 day strips extracted; 4× slower than qwen2.5vl:7b |
| llava:13b, minicpm-v, granite3.2-vision:2b | ~0% | Wrong dates or all-x output |
| qwen2.5vl:32b (CPU offload) | −23 shifts | 75 min runtime, accuracy worse than 7b |
| llama3.2-vision:11b (ensemble) | — | Fills blanks with "x" not times; corrupts ensemble |
| Fireworks qwen3-vl-30b-a3b-instruct | 329/434 (75.8%) | General MoE arch, not a visual table reader; row-disambiguation failures; rate limits on serverless |
| Kimi K2.5 (`kimi-k2p5`) | — | Reasoning chain leaks into names JSON array; all downstream passes corrupted |
| qwen2p5-vl-72b via Fireworks serverless | — | Returns empty responses on serverless; on-demand only at $10/hr |

## Quick Reference Commands

```powershell
# GLM-OCR (new best: 92.6%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --glm-ocr --test --model glm-ocr 2>&1 | Tee-Object glm-ocr-benchmark.txt

# GLM-OCR score only
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --glm-ocr --test --model glm-ocr 2>&1 | Select-String "Overall|IM \("

# Hybrid pipeline (qwen2.5vl:7b, 90.8%)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs" --test --model qwen2.5vl:7b 2>&1 | Tee-Object hybrid-run-output.txt

# Single image test (GLM-OCR, faster iteration)
dotnet run --project CalendarParse.Cli --no-build -- "CalendarParse\calander-parse-test-imgs\IM (2).jpg" --glm-ocr --test
```

## Research Pipeline

Ideas not yet tried. Add new entries here; move to experiment-log.md once tested.

| Idea | Hypothesis |
|------|------------|
| **qwen2.5-vl fine-tune (chrisalehman/ai-document-extraction)** | Fine-tuned on W2 forms for table→JSON — closer to CalendarParse use case than base model. |
| **Image preprocessing (sharpen + contrast)** | Image quality may be the main bottleneck for time-misread errors. Risk: may affect red-ink X-mark color signal. |
| **rolm-ocr for Athena row detection** | OCR-dedicated model; outputs markdown table. **Serverless not supported on Fireworks — on-demand deployment only (dedicated GPU required, same blocker as qwen2p5-vl-72b).** Confirmed blocked as of 2026-04-06. |
