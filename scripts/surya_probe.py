"""
Probe script: run Surya TableRecPredictor on IM(3) and print a summary
of detected rows, cols, and cells to validate cell detection quality.
Usage: python scripts/surya_probe.py "CalendarParse/calander-parse-test-imgs/IM (3).jpg"
"""
import sys
import json
from PIL import Image
from surya.table_rec import TableRecPredictor

def bbox_from_polygon(polygon):
    """Convert [[x,y],...] polygon to [x1,y1,x2,y2] bounding box."""
    xs = [p[0] for p in polygon]
    ys = [p[1] for p in polygon]
    return [min(xs), min(ys), max(xs), max(ys)]

def main():
    img_path = sys.argv[1] if len(sys.argv) > 1 else "CalendarParse/calander-parse-test-imgs/IM (3).jpg"
    print(f"Loading image: {img_path}", flush=True)
    img = Image.open(img_path)
    print(f"Image size: {img.width}x{img.height}", flush=True)

    print("Loading TableRecPredictor (CPU)...", flush=True)
    predictor = TableRecPredictor(device="cpu")

    print("Running table recognition...", flush=True)
    results = predictor([img])
    result = results[0]

    print(f"\n=== TABLE STRUCTURE ===")
    print(f"Rows detected: {len(result.rows)}")
    print(f"Cols detected: {len(result.cols)}")
    print(f"Cells detected: {len(result.cells)}")

    # Print row bboxes
    print(f"\n--- Rows ---")
    for row in sorted(result.rows, key=lambda r: r.row_id):
        bbox = bbox_from_polygon(row.polygon)
        print(f"  row_id={row.row_id} is_header={row.is_header} bbox=[{bbox[0]:.0f},{bbox[1]:.0f},{bbox[2]:.0f},{bbox[3]:.0f}] h={bbox[3]-bbox[1]:.0f}")

    # Print col bboxes
    print(f"\n--- Cols ---")
    for col in sorted(result.cols, key=lambda c: c.col_id):
        bbox = bbox_from_polygon(col.polygon)
        print(f"  col_id={col.col_id} is_header={col.is_header} bbox=[{bbox[0]:.0f},{bbox[1]:.0f},{bbox[2]:.0f},{bbox[3]:.0f}] w={bbox[2]-bbox[0]:.0f}")

    # Print a grid of cell assignments
    print(f"\n--- Cell grid (row_id × col_id) ---")
    by_rc = {}
    for cell in result.cells:
        cid = cell.col_id if cell.col_id is not None else cell.within_row_id
        by_rc[(cell.row_id, cid)] = cell

    max_row = max((k[0] for k in by_rc), default=0)
    max_col = max((k[1] for k in by_rc), default=0)
    for r in range(max_row + 1):
        row_cells = []
        for c in range(max_col + 1):
            cell = by_rc.get((r, c))
            if cell:
                bbox = bbox_from_polygon(cell.polygon)
                row_cells.append(f"[{bbox[0]:.0f},{bbox[1]:.0f},{bbox[2]:.0f},{bbox[3]:.0f}]")
            else:
                row_cells.append("[--missing--]")
        print(f"  row {r:2d}: {' '.join(row_cells)}")

    # Save full JSON for further analysis
    out = {
        "image_size": [img.width, img.height],
        "rows": len(result.rows),
        "cols": len(result.cols),
        "cells": [
            {
                "row_id": c.row_id,
                "col_id": c.col_id if c.col_id is not None else c.within_row_id,
                "is_header": c.is_header,
                "bbox": bbox_from_polygon(c.polygon),
            }
            for c in result.cells
        ]
    }
    out_path = img_path.replace(".jpg", ".surya_cells.json").replace(".png", ".surya_cells.json")
    with open(out_path, "w") as f:
        json.dump(out, f, indent=2)
    print(f"\nSaved cell JSON: {out_path}")

if __name__ == "__main__":
    main()
