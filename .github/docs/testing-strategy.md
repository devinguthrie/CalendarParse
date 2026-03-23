# CalendarParse CLI — Testing Strategy

_Last updated: 2026-03-13 (session 8)_

---

## Goal

Maximize the percentage of correctly extracted shift values against the ground-truth answer JSON, measured as **correct cells / 168 total cells** (IM(1): 77 shifts + IM(2): 91 shifts).

Current best combined: **69.6% (117/168)** — `qwen2.5vl:7b` + `--known-names`, Phase 30 (P16+P18 guards), deterministic × 3  
Current best single-image: **76.6% (59/77)** on IM(1) only — `qwen2.5vl:7b`, Phase 9g (historical single-run peak)

> **Answer.json corrections to date**: Seena Sun, Kyleigh Tue, Ciara Tue (session 2); Victor Tue + Thu (session 2, later).  
> **Scorer note**: `""` (blank) and `"x"` are treated as equivalent — both mean "not working".  
> **Fuzzy name matching**: Levenshtein ≤ 2 in scorer lookup — added Phase 14a.  
> **Known-names**: always pass `--known-names Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori` — prevents NAME-SPLIT errors (Phase 21, +5 shifts).

---

## Test Execution

### Standard test run

```powershell
dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori"
```

Output includes:
- Per-image success/failure
- Per-shift mismatches (employee, date, got, expected)
- Overall: `N/77 shifts matched (X%)`

### Variance testing (multiple runs)

Due to model non-determinism, run 3–5 times and record the range:

```powershell
# 3 quick runs, print only the score line
1..3 | ForEach-Object {
    dotnet run --project CalendarParse.Cli -- "CalendarParse\calander-parse-test-imgs" --vision --test --model qwen2.5vl:7b --known-names "Andee,Brittney,Cyndee,Sarah,Franny,Jenny,Victor,Halle,Kyleigh,Seena,Ciara,Athena,Tori" 2>&1 | Select-String "Overall"
}
```

Accept a change as an improvement only if the **average** improves, not just the peak.

---

## Error Categories

When analyzing failures, classify each wrong cell into one of these buckets:

| Category | Description | Example |
|----------|-------------|---------|
| **BLANK** | Model returned `""` for a non-blank cell | got `""` expected `10:00-6:30` |
| **SPURIOUS** | Model returned a value for a blank cell | got `9:30-6:00` expected `""` |
| **X-SWAP** | Model confused `x` (day off) with `""` (blank) in either direction | got `""` expected `x` |
| **TIME-MISREAD** | Correct structure but wrong digits | got `10:30-3:00` expected `10:00-3:00` |
| **WRONG-ROW** | Value from a different employee's row | got `10:00-6:30` (Cyndee's value) expected Franny's value |
| **WRONG-COL** | Correct row but shifted column | value appears in adjacent day |
| **NAME-MISS** | Employee's name extracted incorrectly, no shifts attributed | "Siana" instead of "Seena" |
| **LABEL-WRONG** | Wrong label type | got `RTO` expected `PTO` or vice versa |

---

## Current Known Error Breakdown (Phase 30/P16+P18, 69.6% combined — deterministic × 3)

_51 errors total (168 − 117). Combined across IM(1) + IM(2) with `--known-names`, temperature=0.0._

| Employee | Image | Errors | Dominant error category |
|----------|-------|--------|--------------------------|
| Andee | IM(1) | 3 | PTO/RTO returned instead of time ranges — row misidentification |
| Kyleigh | IM(1) | 4 | Consistent +1 right-shift; x/RTO/blank confusion |
| Seena | IM(1) | 4 | +1 right-shift on 10/27–28 + x/RTO swap |
| Victor | IM(1) | 2 | 30-min end time misread (10:00-6:30 vs 10:00-6:00) |
| Franny | IM(2) | 4 | Perfect +1 right-shift on all 5 extraction runs — unfixable by re-query |
| Tori | IM(2) | 4 | Right-shift pattern (similar to Franny) |
| Brittney, Cyndee, Sarah, Jenny, Victor, Ciara, Athena, Seena | IM(2) | 1–2 each | Thanksgiving column drift — blank col 11/27 offsets adjacent columns |

**Total accounted above**: ~30 named errors + ~21 Thanksgiving drift errors = 51 total.

**Dominant remaining error**: WRONG-COL (~50% of failures). Two sub-patterns:
1. **Employee-specific structural drift** — Franny, Kyleigh, Seena, Tori have a persistent +1 right-shift across all extraction runs; re-querying returns the same drifted result.
2. **Thanksgiving blank-column cascade** — The 11/27 universally blank column destabilizes column-counting for 8 employees in IM(2), producing 1–2 adjacent-column errors each.

---

## Hypotheses Ranked by Potential Impact

### Highest priority — column drift fixes

1. **Two-shot self-anchoring**: Split extraction into Q1 "What are the exact date labels printed at the top of each of the 7 columns?" then Q2 "Using ONLY the labels from Q1, read shift cells below each label." Forces explicit visual anchoring before value extraction — targets the root cause of WRONG-COL. Low cost (prompt change only; can replace one of the 3 row runs).

2. **CSV output format**: Ask the model to return a CSV using image-visible date headers (e.g. `10/26,10/27,...`) instead of JSON with day-name keys. Column counts are programmatically verifiable — a row with the wrong column count is immediately detectable. Tabular format mirrors image structure. Risk: trailing empty cells silently dropped; test carefully. Note: distinct from the "Pipe/CSV" anti-pattern (which used fixed template examples).

3. **Dual output cross-reference**: In a single call, request both a per-employee row view AND a per-day column view as two JSON objects. Cross-reference: cells that agree across both views have high confidence; disagreements flag drift candidates for re-query. No extra API calls. Risk: consistent model drift may confirm wrong answers in both views.

### High priority

4. ~~**Kyleigh/Seena/Ciara WRONG-COL**~~ — TRIED Phase 15b (anchor-guided re-extraction), REVERTED. Anti-pattern confirmed.

5. ~~**Andee Thu/Fri BLANK**~~ — TRIED Phase 15a, REVERTED. Confirmed hard OCR limit.

6. **`qwen2.5vl:72b`** — 32 GB VRAM available. Q3_K_M quantization (~30 GB) should fit. Same family as current best model; ~10× more parameters expected to significantly improve WRONG-COL and TIME-MISREAD accuracy.

### Medium priority

7. **`internvl3:38b` / `internvl2.5:38b`** — Document/table OCR specialist; Q4 ~24 GB. Check with `ollama search internvl`.

8. **Hybrid WinRT OCR pipeline** — Use `WindowsWinRtOcrService.cs` bounding-box output + `WindowsTableDetector.cs` grid detection to deterministically map cells to grid positions. Bypasses model column-counting entirely. Highest ceiling; most implementation work. See `next-session-plan.md`.

### Confirmed dead ends

- gemma3 (any size) — year hallucination on IM(1)
- llava:13b, minicpm-v, granite3.2-vision:2b — wrong dates / all-x output
- Anchor-guided re-extraction — anti-pattern (overwrites correct values)
- Pipe/CSV output format — model copies examples
- Downscaling images (`--resize`) — destroys fine digit legibility
- Ensemble with llama3.2-vision:11b — fills blanks with "x" not time ranges
- CLAHE / grayscale / `current` preprocessing — destroys color signal or inverts image

---

## What "Good" Looks Like

_Thresholds apply to the combined 168-shift score (IM(1) + IM(2)) with `--known-names`._

| Threshold | What it means |
|-----------|---------------|
| > 69.6% | Beating current best (117/168, Phase 30/P16+P18, deterministic) |
| > 75% avg | Clear improvement — on par with old IM(1)-only peak on the harder combined set |
| > 80% avg | Near-production quality |
| > 90% avg | Excellent — only rare genuine OCR ambiguities remain |
| 100% | Theoretical maximum — unlikely with OCR noise |

---

## Invariants — Do Not Change These

- The `NormalizeDate()` call in both `OllamaCalendarService` and `Program.cs` — removes a class of false negatives
- The `RepairTruncatedJson()` fallback — prevents total parse failures on truncated responses
- The `EnsureModelLoadedAsync()` warmup — prevents the first call from eating ~30s in model load time
- The `keep_alive = -1` Ollama option — prevents model unload between the 3 extraction calls
- The `--known-names` flag on all test runs — prevents NAME-SPLIT errors; always pass the full 13-name list (Phase 21)
