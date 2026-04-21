# CalendarParse CLI — Experiment Log

_Last updated: 2026-04-21 (session 43). Full verbose history in experiment-log.old.md._

Test images: IM(1) = 11 employees, 77 shifts, Oct 26–Nov 1 2025. IM(2) = 13 employees, 91 shifts, Nov 23–29 2025. **IM(3) = 12 employees, 84 shifts, Sep 21–27 2025.** IM(4) = 13 employees, 91 shifts, Jul 27–Aug 2 2025. IM(5) = 13 employees, 91 shifts, Jul 20–26 2025. Combined = **434 shifts** (5 images).

---

## Experiment Records

> **All per-experiment records live in [`experiments.jsonl`](../../experiments.jsonl)** (repo root, append-only).
> Each line is a JSON object with fields: `id`, `change_type`, `description`, `score_before`, `score_after`, `total_shifts`, `outcome`, `notes`, `timestamp`.
> The benchmark-loop script appends every `committed` and `reverted` runtime entry automatically.

---

## Key Architectural Lessons

See **[`.github/docs/memory/key-lessons.md`](memory/key-lessons.md)** for the full lessons reference.

---

## Session Index

Each session's full narrative lives in `.github/docs/memory/sessions/`:

| Session | Score | Summary |
|---------|-------|---------|
| [Phase archive](memory/sessions/phase-archive.md) | — | Phase 9e through Phase 63 (frozen history) |
| [22](memory/sessions/session-22.md) | 394/434 | Dead code removal + prompt externalization |
| [23](memory/sessions/session-23.md) | REVERTED 250/434 | Fireworks qwen3-vl-30b (wrong model family) |
| [24](memory/sessions/session-24.md) | 394/434 | Research: Kimi K2.5 / GLM-OCR design |
| [25](memory/sessions/session-25.md) | REVERTED 329/434 | qwen3-vl-30b re-benchmark with P20 |
| [26](memory/sessions/session-26.md) | **402/434 ★** | GLM-OCR first benchmark — new best |
| [27](memory/sessions/session-27.md) | — | Fireworks text-only models rejected |
| [28](memory/sessions/session-28.md) | 402/434 | Portrait crash root cause + Emgu.CV resize fix |
| [29](memory/sessions/session-29.md) | 402/434 | Levenshtein dedup REVERTED; crash hardening COMMITTED |
| [30](memory/sessions/session-30.md) | 187/434 | Tesseract v5 as WinRT replacement (proof-of-concept) |
| [31](memory/sessions/session-31.md) | 144/434 | colsFromOcr flag split + WinRT conditional |
| [32](memory/sessions/session-32.md) | 263/434 | Tesseract: whitelist +100 shifts + header-strip LLM |
| [33](memory/sessions/session-33.md) | 266/434 | Tesseract: 2× upscale (+3) |
| [34](memory/sessions/session-34.md) | **408/434 ★** | GLM-OCR temperature=0 — new best (current) |
| [35](memory/sessions/session-35.md) | 266/434 | Two IM(5) Tesseract experiments REVERTED |
| [36](memory/sessions/session-36.md) | REVERTED 244/434 | n-2 padding before needsLastRequery |
| [37](memory/sessions/session-37.md) | REVERTED 244/434 | PSM Auto / 3× upscale / EasyOCR (three experiments) |
| [38](memory/sessions/session-38.md) | 408/434 | IM(3) portrait investigation + model probes (38-A to 38-F) |
| [39](memory/sessions/session-39.md) | 9/14 | Live correction benchmark verification |
| [40](memory/sessions/session-40.md) | 287/448 | Combined benchmark (base + corrections) |
| [41](memory/sessions/session-41.md) | 265/434 | Parsing extraction parity benchmark (Option B regression) |
| [42](memory/sessions/session-42.md) | 408/434 | GLM-OCR verification after parity alert |
| [43](memory/sessions/session-43.md) | 267/434 | Hybrid regression scoping |

---

_**New entries**: create a new `memory/sessions/session-N.md` file. Do NOT append to this file._
