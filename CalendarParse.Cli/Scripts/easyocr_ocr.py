"""
Persistent EasyOCR subprocess worker.

Protocol (all communication is line-based, UTF-8, LF-terminated):
  - Startup: prints "READY" once model is loaded and stdin polling begins.
  - Per request: read one line from stdin = absolute path to image file.
  - Response: one JSON line to stdout = list of OCR elements.
  - Shutdown: send "QUIT" on stdin (or close stdin) to exit cleanly.

JSON element schema:
  {"text": str, "confidence": float, "x": int, "y": int, "width": int, "height": int}

EasyOCR returns quad bounding boxes (4 corner points).  This script converts
each quad to an axis-aligned bounding box (min/max of corner coords).
"""

import sys
import json
import os


def main() -> None:
    import easyocr  # imported inside main so startup errors are catchable

    reader = easyocr.Reader(["en"], verbose=False)

    # Signal to the C# host that the model is loaded and we are ready.
    print("READY", flush=True)

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue
        if line.upper() == "QUIT":
            break

        try:
            results = reader.readtext(line, detail=1)
        except Exception as exc:  # noqa: BLE001
            # Return empty list on per-image errors so the pipeline can continue.
            sys.stderr.write(f"[easyocr_ocr] error on {line!r}: {exc}\n")
            sys.stderr.flush()
            print("[]", flush=True)
            continue

        output = []
        for (bbox, text, conf) in results:
            xs = [float(p[0]) for p in bbox]
            ys = [float(p[1]) for p in bbox]
            output.append(
                {
                    "text": text,
                    "confidence": float(conf),
                    "x": int(min(xs)),
                    "y": int(min(ys)),
                    "width": int(max(xs) - min(xs)),
                    "height": int(max(ys) - min(ys)),
                }
            )

        print(json.dumps(output), flush=True)


if __name__ == "__main__":
    main()
