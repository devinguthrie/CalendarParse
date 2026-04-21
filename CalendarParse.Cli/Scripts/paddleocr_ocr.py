"""
Persistent PaddleOCR subprocess worker.

Protocol (all communication is line-based, UTF-8, LF-terminated):
  - Startup: prints "READY" once model is loaded and stdin polling begins.
  - Per request: read one line from stdin = absolute path to image file.
  - Response: one JSON line to stdout = list of OCR elements.
  - Shutdown: send "QUIT" on stdin (or close stdin) to exit cleanly.

JSON element schema:
  {"text": str, "confidence": float, "x": int, "y": int, "width": int, "height": int}

PaddleOCR returns quad bounding boxes (4 corner points).  This script converts
each quad to an axis-aligned bounding box (min/max of corner coords).

Supports PaddleOCR 2.x (paddleocr>=2.0,<3.0).
"""

import sys
import json


def main() -> None:
    from paddleocr import PaddleOCR  # imported inside main so startup errors are catchable

    ocr = PaddleOCR(
        use_angle_cls=True,
        lang="en",
        use_gpu=False,
        show_log=False,
    )

    # Signal to the C# host that the model is loaded and we are ready.
    print("READY", flush=True)

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue
        if line.upper() == "QUIT":
            break

        try:
            # result is a list of pages; result[0] is a list of [bbox, (text, conf)]
            result = ocr.ocr(line, cls=True)
            lines = result[0] if result else []
        except Exception as exc:  # noqa: BLE001
            # Return empty list on per-image errors so the pipeline can continue.
            sys.stderr.write(f"[paddleocr_ocr] error on {line!r}: {exc}\n")
            sys.stderr.flush()
            print("[]", flush=True)
            continue

        output = []
        if lines:
            for entry in lines:
                # entry: [bbox, (text, confidence)]
                bbox, (text, conf) = entry
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
