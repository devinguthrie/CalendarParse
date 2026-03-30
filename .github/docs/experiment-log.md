# CalendarParse CLI — Experiment Log

_Last updated: 2026-03-29 (session 15). Full verbose history in experiment-log.old.md._

Test images: IM(1) = 11 employees, 77 shifts, Oct 26–Nov 1 2025. IM(2) = 13 employees, 91 shifts, Nov 23–29 2025. **IM(3) = 12 employees, 84 shifts, Sep 21–27 2025.** Combined = **252 shifts**.

---

## Summary Table

| Phase | Model | Architecture | Best | Avg | Notes |
|-------|-------|-------------|------|-----|-------|
| 1 | — | CSV data audit | — | — | Ground truth corrected |
| 2 | llama3.2-vision:11b | Single-pass full JSON | 6.5% | ~6% | Blank cells — model won't read inside cells |
| 3 | llama3.2-vision:11b | 3-pass per-row JSON | 18.2% | ~15% | 26 phantom employees |
| 4 | llama3.2-vision:11b | CSV bulk extraction | 14.3% | ~12% | Truncation + misalignment |
| 5 | llama3.2-vision:11b | Per-employee 7-shift CSV | 23.4% | ~17% | Example copying; llama ceiling |
| 5a | llama3.2-vision:11b | Prompt variants (date labels, no example, 2 examples, single-cell) | 11.7% | ~10% | All worse than Phase 5 |
| 6 | llava:13b | Per-employee CSV | 0% | 0% | Not viable |
| 7 | qwen2.5vl:7b | Per-employee CSV | 33.8% | ~30% | Row cross-contamination |
| 8 | qwen2.5vl:7b | Single-shot positional arrays | 50.6% | ~44% | Off-by-one column alignment |
| 9 | qwen2.5vl:7b | Single-shot named day keys | 54.5% | ~45% | Named keys fix column alignment |
| 9a | qwen2.5vl:7b | Batched + temp=0 | 28.6% | ~28% | Second batch all blank |
| 9b | qwen2.5vl:7b | 2-pass merge | 53.2% | ~46% | Bad runs not filtered |
| 9c | qwen2.5vl:7b | 3-pass majority vote | 53.2% | ~48% | Superseded |
| 9d | qwen2.5vl:7b | + smart re-query | ~45% | ~43% | Answer.json corrected (Seena, Kyleigh, Ciara) |
| 9e | qwen2.5vl:7b | + x-marks pass + red ink hint | 66.2% | ~65.3% | +22 pts; X-SWAP bulk fix |
| 9f | qwen2.5vl:7b | + scorer blank≡x equivalence | 71.4% | ~71.4% | +5 pts scorer fix |
| 9g | qwen2.5vl:7b | + answer.json Victor Tue/Thu fix | 76.6% | ~76.6% | +2 pts ground truth fix |
| 10 | qwen2.5vl:7b | Vertical column slices | 41.6% | 41.6% | REVERTED — totals column confusion |
| 11 | minicpm-v | Phase 9g pipeline | 0% | 0% | Wrong dates + all x |
| 12 | granite3.2-vision:2b | Phase 9g pipeline | 0% | 0% | Header fails + all x |
| 13 | gemma3:4b | Phase 9g pipeline | 0% | 0% | Wrong year + phantoms + all x |
| 14a | qwen2.5vl:7b | + fuzzy name matching (scorer) | 76.6% | ~75.7% | Neutral — protective |
| 14b | qwen2.5vl:7b | + suspect-blank re-query | 74.0% | 74.0% | REVERTED — can't distinguish correct/missed blanks |
| 15a | qwen2.5vl:7b | + all-7-cols prompt emphasis | — | 74.0% | REVERTED — confused other employees |
| 15b | qwen2.5vl:7b | + anchor-guided re-extraction | — | 40.3% | REVERTED — catastrophic; overwrites anchors |
| 15c | qwen2.5vl:7b | Pipe-delimited format | — | 10.4% | REVERTED — example copying |
| 15d | qwen2.5vl:7b | Column extraction re-test | — | 33.8% | REVERTED — same as Phase 10 |
| 16 | 3 models × 5 preprocess modes | Preprocessing A/B (168 shifts) | 68.5% | — | `none`/`llm` best; grayscale harmful; `current`=0% |
| 17 | qwen2.5vl:7b | `none` vs `llm` 3-run | — | 67.5% / 67.3% | `llm` advantage was noise |
| 18 | qwen2.5vl:7b | `--halves` image split | — | 67.5% | Zero effect |
| 19 | qwen2.5vl:7b | `--resize 1120` | — | 54.4% | HARMFUL −13 pts |
| 20 | qwen2.5vl:7b | Error analysis run | — | 67.9% | NAME-SPLIT discovered (Athena) |
| 21 | qwen2.5vl:7b | `--known-names` + name normalisation | — | 71.4%* | +3.5 pts; NAME-SPLIT fixed (*single run outlier) |
| 22 | qwen + llama ensemble | Blank-fill ensemble | — | 71.4% | Neutral — llama fills "x" not times |
| 23 | gemma3:27b | Full pipeline | — | 17.3% | Wrong year; not viable |
| 24 | qwen2.5vl:7b | 5+2 votes + ISO date keys | — | 56.0% | REGRESSED −11.5 pts (both changes harmful) |
| 25 | qwen2.5vl:7b | 5+2 votes only (P12 isolated) | — | 57.7% | REGRESSED — extra votes lock in errors |
| 26 | qwen2.5vl:7b | True baseline (3+2, day-names, known-names) | — | 66.1% | Phase 21's 71.4% was outlier |
| 27 | qwen2.5vl:7b | Numbered-column prompt + drift detect | 68.5% | 62.7% | REGRESSED — high variance |
| 28 | qwen2.5vl:7b | Drift detector only, temp=0.1 | 72.6% | 66.5% | High variance; peak but unstable |
| **29** | **qwen2.5vl:7b** | **temp 0.1→0.0** | — | **69.0%** | **+3 pts; perfectly deterministic** |
| **30** | **qwen2.5vl:7b** | **Blank fallback improvements** | — | **69.6%** | **+0.6 pts; conditional re-query guard** |
| 31 | qwen2.5vl:7b | Drift threshold=1 + ExtractRowAsync | — | 61.3% | REVERTED — fires on normal schedules |
| **32** | **qwen2.5vl:7b** | **Quality guards (CountValidShifts)** | — | **69.6%** | **Defensive; prevents future regressions** |
| 33 | qwen2.5vl:7b | OCR garbage sanitize (no --known-names mistake) | — | 67.3% | Apparent −2.3; real cause: missing flag |
| **34** | **qwen2.5vl:7b** | **P19 confirmed with --known-names** | — | **70.2%** | **+0.6 pts; OCR garbage fix works** |
| 35 | qwen2.5vl:7b | Two-shot self-anchoring Q1→Q2 | — | 67.9% | REVERTED — biases away from cells |
| **36** | **qwen2.5vl:7b** | **Anti-WRONG-COL warning (end of rules)** | — | **78.0%** | **COMMITTED — +13 shifts; all-time vision best** |
| 37 | qwen2.5vl:7b | Ordinal hint + anti-shift in ExtractColumnAsync | — | 73.2% | REVERTED — harmful in single-col |
| 37b | qwen2.5vl:7b | Anti-shift in ExtractColumnAsync only | — | 73.2% | REVERTED — confirmed: warning harmful in single-col |
| 38 | qwen2.5vl:7b | 1 row + 4 col vote reweight | — | 40.5% | CATASTROPHIC — temp=0 = 4 identical wrong copies |
| 39 | qwen2.5vl:7b | CRITICAL warning at TOP of rules | — | 61.9% | REVERTED — −27 shifts |
| 40 | qwen2.5vl:7b | Warning in ExtractRowAsync | — | 78.0% | NEUTRAL — fallback path not exercised |
| 41 | qwen2.5vl:7b | CSV output for row passes | — | 54.8% | REVERTED — parse cascade |
| 42 | qwen2.5vl:7b | Dual-view cross-reference (6th vote) | — | 70.2% | REVERTED — correlated views inject blanks |
| 43 | qwen2.5vl:7b | qwen2.5vl:32b, CPU offload | — | 64.3% | 75 min; bigger is WORSE with offload |
| 44 | qwen2.5vl:7b | Hybrid v1: OCR positional list | — | 50.6% | Positional misalignment |
| 45 | qwen2.5vl:7b | Hybrid v2: day-keyed dictionary | — | 58.9% | +14: correct day slot mapping |
| 46 | qwen2.5vl:7b | Hybrid v3: CropAndStitch | — | 66.7% | +13: no multi-column contamination |
| 47 | qwen2.5vl:7b | Hybrid v4: DayPrefixes matching | — | 70.2% | +6: all 7 cols detected both images |
| **48** | **qwen2.5vl:7b** | **Hybrid v5: ISO date cascade fix** | — | **82.1%** | **+20: largest single gain** |
| **49** | **qwen2.5vl:7b** | **Hybrid v6: RTO/PTO holiday heuristic** | — | **89.9% (151/168)** | **2-image all-time best with --known-names** |
| **50** | **qwen2.5vl:7b** | **3-image test set (IM(3) added, 252 shifts)** | — | **75.4% (190/252)** | **Baseline on expanded set; no known-names** |
| **51** | **qwen2.5vl:7b** | **Session name pool + late OCR name supplement** | — | **83.3% (210/252)** | **+20 pts; Andee/Brittney/Cyndee/Sarah/Franny recovered in IM(3)** |
| **52** | **qwen2.5vl:7b** | **OCR fragments before pass 2 + non-orthogonal prompt** | — | **90.1% (227/252)** | **ALL-TIME BEST — no --known-names required** |

---

## Key Architectural Lessons

### What works
- **Named day keys** in JSON (Sun/Mon/…) — anchors column identity (Phase 9)
- **3 row + 2 col majority vote** at temp=0 — complementary error correction (Phase 9c→36)
- **X-marks binary pass** + red ink hint — resolves X-SWAP bulk errors (Phase 9e)
- **`--known-names` injection** — eliminates NAME-SPLIT phantoms (Phase 21) — now superseded by dynamic discovery
- **Anti-WRONG-COL warning at END of rules** in multi-col extraction — +13 shifts (Phase 36)
- **temperature=0.0** — perfectly deterministic; any score change is real signal (Phase 29)
- **Hybrid OCR+LLM**: WinRT OCR positions → column boundaries → per-day strip → single-col LLM (Phases 44–49)
- **Holiday heuristic**: ≥80% uniform RTO/PTO column → blank all (Phase 49)
- **Session name pool**: names extracted from earlier images in the same run accumulate and are used as spelling hints for later images (Phase 51)
- **Late OCR name supplement**: after pass 4, OCR name-column tokens fuzzy-matched to find employees the LLM missed; added with empty shifts (Phase 51)
- **OCR fragments before pass 2**: run WinRT OCR first, collect name-column partial reads, pass to LLM as grounding anchors — helps reconstruct fragmented names (e.g. "M"+"an" → "Megan") (Phase 52)
- **Non-orthogonal coordinate hint**: telling the LLM the image is a photo of a grid (lines may not be straight) improves row-boundary accuracy (Phase 52)

### What never works
- Non-JSON output formats (CSV, pipe-delimited) — model copies examples
- Any model <6B params — cannot read dense tables
- CPU-offloaded models — accuracy AND speed degrade
- Grayscale/binary preprocessing — destroys color signal
- Adding anti-shift warning to single-column context
- More copies of same extraction method at temp=0 — no diversity, amplifies errors
- Anchor-guided re-extraction — corrupts anchor employees
- Image downscaling — destroys fine digit legibility
- Two-shot self-anchoring — biases away from cell reading
- Pass 2b (name column strip as separate LLM crop) — LLM hallucinates variant names (Andrea/Cyndi) even with edit-distance filter; zero net benefit

### Critical context-sensitive rules
- Anti-WRONG-COL warning: ✅ multi-column (`ExtractAllShiftsAsync`), ❌ single-column (`ExtractColumnAsync`)
- Warning placement: ✅ end of rules, ❌ top of rules (−27 shifts)
- Majority vote ratio: 3 row + 2 col is optimal at temp=0; do not reweight

---

## Phase Details — Milestones Only

_Reverted/failed phases are in the summary table above. Only committed milestones detailed here._

### Phase 9e — X-Marks Pass + Red Ink Hint (65.3% avg)
Added Pass 4 `ExtractXMarksAsync`: binary "which cells have X?" query. Applied as overlay: blank→x only. Red ink hint in prompts. +22 pts over Phase 9d.

### Phase 9f — Scorer blank≡x (71.4% avg)
`ShiftsMatch()` treats `""`≡`"x"` as equivalent "not working". +5 pts.

### Phase 9g — Answer.json Victor Fix (76.6%)
Victor Tue/Thu corrected from 10:00-6:00 → 10:00-6:30 (model was correct). +2 pts.

### Phase 21 — Known-Names + Name Normalisation (71.4% single run)
`--known-names` prevents NAME-SPLIT (Athena→Clara+Athena(train)). +5 shifts on IM(2). Fuzzy merge post-process. True 3-run mean later established at 66.1% (Phase 26).

### Phase 29 — Temperature 0.0 (69.0% deterministic)
Eliminated all run-to-run variance. Every run identical. +3 pts vs temp=0.1 baseline.

### Phase 36 — Anti-WRONG-COL Warning (78.0% — vision best)
Single CRITICAL sentence at end of `ExtractAllShiftsAsync` rules:
> "Do NOT shift values one column to the right. Each cell value belongs ONLY in the column whose header is directly above that cell."

+13 shifts. IM(2) +11, IM(1) +2.

### Phases 44–49 — Hybrid Pipeline Evolution (50.6% → 89.9%)
| Phase | Fix | Gain |
|-------|-----|------|
| 44 | OCR positional list → strip LLM | Baseline 50.6% |
| 45 | `Dictionary<int,...>` keyed by day index | +14 |
| 46 | CropAndStitch (name+day col only) + header-skip prompt | +13 |
| 47 | DayPrefixes 30+ variants, 3-char prefix matching | +6 |
| 48 | ISO date cascade fix (strip date-as-first-element) | +20 |
| 49 | RTO/PTO uniform-column holiday heuristic (≥80% → blank) | +13 |

### Phase 50 — IM(3) Added to Test Set (252 shifts total)
Third test image: Sep 21–27 2025, 12 employees, 84 shifts. Includes Megan and Raul (unique to IM(3)) and Andee, Brittney, Cyndee, Sarah, Franny, Jenny, Victor, Halle, Kyleigh, Seena (shared with IM(1)). Baseline with --known-names and no session features: 190/252 (75.4%). Root cause: LLM pass 2 stochastically returns only 7–10 of 12 employees for IM(3).

### Phase 51 — Session Name Pool + Late OCR Name Supplement (210/252 = 83.3%)
Two complementary features:
1. **Session name pool**: `HybridCalendarService` accumulates employee names across images in the same run (`_sessionNames`). Names from IM(1) and IM(2) are passed as `additionalHints` to pass 2 of IM(3), prompting the LLM to use those spellings and look for those employees. Soft constraint: "spelling reference — also list any other names clearly visible."
2. **Late OCR name supplement**: After pass 4, OCR tokens from the name column are fuzzy-matched against the session pool. Names matched (Tori, Raul via prefix/contains/startswith) are added with empty shift records. Empty shifts correctly match `"x"` days via the `"" ≡ x` scorer rule.
3. **OCR supplement fallback fix**: When the session pool exists but a token doesn't match any pool name, fall through to heuristics instead of skipping — catches employees not in any prior image.

### Phase 52 — OCR Fragments Before Pass 2 + Non-Orthogonal Hint (227/252 = 90.1%)
Two changes:
1. **OCR moved before pass 2**: WinRT OCR now runs before the LLM name extraction call. Name-column tokens are grouped into per-row text fragments (14px Y-band) and passed as `ocrNameFragments` to `ExtractNamesAsync`. Prompt says: "OCR detected these partial text fragments from the name column of this image (they may be truncated or split): [fragments]. Use these as anchors — every fragment likely corresponds to an employee row. Identify the full name for each fragment." This lets the LLM reconstruct "M"+"an" → "Megan" and other fragmented names.
2. **Non-orthogonal coordinate hint**: All LLM prompts (pass 2, pass 3 strip, pass 4 x-marks) now include: "Note: this is a photograph of a printed grid — the grid lines may not be perfectly straight or orthogonal due to camera angle and perspective distortion. Read each row by visual context, not pixel-perfect alignment." This improved cross-row boundary accuracy.

Net result: Megan (IM(3)) 0/7 → 7/7, Raul (IM(3)) 4/7 → 7/7, Seena (IM(3)) 4/7 → 7/7 (was getting Megan's data), Sarah/Franny IM(1) each +1. No `--known-names` flag required.

### Answer.json Corrections
- Seena Sun: 10:00-3:00 → 10:30-3:00
- Kyleigh Tue: 9:00-1:00 → 9:30-2:00
- Ciara Tue: "" → x
- Victor Tue+Thu: 10:00-6:00 → 10:00-6:30
