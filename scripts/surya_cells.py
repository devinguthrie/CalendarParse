"""
surya_cells.py — Surya-based calendar schedule extractor.

Uses Surya TableRecPredictor to detect cell bounding boxes in a portrait
calendar photo, then RecognitionPredictor to read text from each cell.
This avoids GLM-OCR's M-RoPE tiling issues on portrait images.

Usage:
    python scripts/surya_cells.py <image_path> [name_filter]

Outputs JSON to stdout in the same format as GlmOcrCalendarService:
    {"Month": "September", "Year": 2025, "Employees": [...]}

Exit codes:
    0 = success (JSON on stdout)
    1 = error (message on stderr, "ERROR: ..." on stdout)
"""
import sys
import re
import json
import math
from datetime import date
from PIL import Image, ImageOps
from surya.table_rec import TableRecPredictor
from surya.recognition import RecognitionPredictor
from surya.foundation import FoundationPredictor


# ── Constants ─────────────────────────────────────────────────────────────────

DATE_RE      = re.compile(r'^\d{1,2}/\d{1,2}/\d{4}$')
TIME_RE      = re.compile(r'^\d{1,2}:\d{2}\s*[-\u2013]\s*\d{1,2}:\d{2}$')
SHIFT_TOKENS = {"x", "xx", "pto", "rto"}
NON_EMPLOYEE = {
    "bank", "volume", "hours", "splh", "upt", "adt", "olls/vin", "oil/vin",
    "negative inv check", "sun", "mon", "tue", "tues", "wed", "thurs", "thu",
    "fri", "sat",
}
DAY_OFFSETS = {
    "sun": 0, "mon": 1, "tue": 2, "tues": 2,
    "wed": 3, "thurs": 4, "thu": 4, "fri": 5, "sat": 6,
}


# ── Geometry helpers ──────────────────────────────────────────────────────────

def poly_to_bbox(polygon):
    """[[x,y],...] → [x1, y1, x2, y2]"""
    xs = [p[0] for p in polygon]
    ys = [p[1] for p in polygon]
    return [min(xs), min(ys), max(xs), max(ys)]

def bbox_to_int(bbox):
    return [int(bbox[0]), int(bbox[1]), int(bbox[2]), int(bbox[3])]


# ── Date helpers ──────────────────────────────────────────────────────────────

def parse_mdy(s):
    """Parse M/D/YYYY → date, or None."""
    parts = s.strip().split('/')
    if len(parts) != 3:
        return None
    try:
        return date(int(parts[2]), int(parts[0]), int(parts[1]))
    except (ValueError, IndexError):
        return None


def sunday_of(d):
    """Return the Sunday of the week containing d."""
    return d - __import__('datetime').timedelta(days=d.weekday() + 1) if d.weekday() != 6 else d


# ── Shift normalization ───────────────────────────────────────────────────────

def normalize_shift(s):
    """Normalize raw cell text to a canonical shift value."""
    s = s.strip()
    # Collapse Unicode en-dash to hyphen
    s = s.replace('\u2013', '-')
    # Normalize time ranges: remove spaces around hyphen
    if TIME_RE.match(s.replace(' ', '')):
        s = re.sub(r'\s*-\s*', '-', s)
    # Uppercase special tokens
    if s.lower() in SHIFT_TOKENS:
        s = s.upper().replace("XX", "xx")
    return s


def is_shift_like(s):
    """Returns True if text looks like a shift value (not a hours number)."""
    s = s.strip()
    if not s:
        return False
    if TIME_RE.match(s.replace(' ', '')):
        return True
    return s.lower() in SHIFT_TOKENS


# ── Main pipeline ─────────────────────────────────────────────────────────────

def extract_schedule(image_path, name_filter=""):
    # Apply EXIF orientation so crop coords match Surya's internal coordinate space
    img = ImageOps.exif_transpose(Image.open(image_path)).convert("RGB")

    # ── Step 1: Detect table cells ──────────────────────────────────────────
    print("[1/3] Detecting table structure...", file=sys.stderr, flush=True)
    table_predictor = TableRecPredictor(device="cpu")
    table_results   = table_predictor([img])
    table_result    = table_results[0]

    cells = table_result.cells
    if not cells:
        return "ERROR: Surya found no cells in image"

    # Build a grid: (row_id, col_id) → bbox
    grid = {}
    for cell in cells:
        cid = cell.col_id if cell.col_id is not None else cell.within_row_id
        bbox = poly_to_bbox(cell.polygon)
        grid[(cell.row_id, cid)] = bbox

    max_row = max(r for r, _ in grid)
    max_col = max(c for _, c in grid)
    print(f"    Grid: {max_row+1} rows × {max_col+1} cols, {len(cells)} cells",
          file=sys.stderr, flush=True)

    # ── Step 2: Recognise text in all cells ─────────────────────────────────
    print("[2/3] Recognising cell text...", file=sys.stderr, flush=True)
    recognition_predictor = RecognitionPredictor(FoundationPredictor(device="cpu"))

    # Crop each cell individually and pass as a batch.
    # Using per-crop images avoids the coordinate-space mismatch that occurs
    # when passing polygons= (returned tl.polygon is in local crop coords, not
    # image coords, breaking any center-proximity matching).
    sorted_keys = sorted(grid.keys())
    bboxes_int  = [bbox_to_int(grid[k]) for k in sorted_keys]
    crops       = [img.crop((b[0], b[1], b[2], b[3])) for b in bboxes_int]

    # Returns one OCRResult per crop image.
    # Pass a full-image polygon for each crop so RecognitionPredictor doesn't
    # need a separate detection predictor.
    crop_polys = []
    for crop in crops:
        w, h = crop.size
        crop_polys.append([[[0, 0], [w, 0], [w, h], [0, h]]])

    rec_results = recognition_predictor(images=crops, polygons=crop_polys)

    # Build cell_text map — join multiple lines within a cell with space
    cell_text = {}
    for key, result in zip(sorted_keys, rec_results):
        text = " ".join(tl.text for tl in result.text_lines if tl.text).strip()
        cell_text[key] = text

    # Build text grid for easy access
    def get(r, c, default=""):
        return cell_text.get((r, c), default).strip()

    # ── Step 3: Parse schedule ───────────────────────────────────────────────
    print("[3/3] Parsing schedule...", file=sys.stderr, flush=True)

    # Find the date row: first row with ≥2 cells matching M/D/YYYY
    date_row_idx = -1
    date_col_ids = []
    date_values  = []

    for r in range(max_row + 1):
        hits = []
        for c in range(max_col + 1):
            text = get(r, c)
            if DATE_RE.match(text):
                hits.append((c, text))
        if len(hits) >= 2:
            date_row_idx = r
            date_col_ids = [h[0] for h in hits]
            date_values  = [h[1] for h in hits]
            break

    if date_row_idx < 0:
        # Debug: dump all header row cells
        row0 = [get(0, c) for c in range(max_col+1)]
        print(f"    DEBUG row 0: {row0}", file=sys.stderr)
        return "ERROR: Surya could not find a date row (need ≥2 M/D/YYYY cells)"

    print(f"    Date row: row_id={date_row_idx}, dates at cols {date_col_ids}",
          file=sys.stderr, flush=True)

    # Find day-names row (row just before date row with ≥4 day names)
    day_offsets_by_col = {}  # col_id → 0-based day offset
    for r in range(max(0, date_row_idx - 3), date_row_idx):
        hits = 0
        for c in range(max_col + 1):
            if get(r, c).lower() in DAY_OFFSETS:
                hits += 1
        if hits >= 4:
            for cid in date_col_ids:
                text = get(r, cid).lower()
                if text in DAY_OFFSETS:
                    day_offsets_by_col[cid] = DAY_OFFSETS[text]
            break

    # Majority-vote anchor sunday from dates + day offsets
    sunday_counts = {}
    for k, (cid, dv) in enumerate(zip(date_col_ids, date_values)):
        d = parse_mdy(dv)
        if d is None:
            continue
        off = day_offsets_by_col.get(cid, k)  # fallback to position
        sun = d - __import__('datetime').timedelta(days=off)
        sunday_counts[sun] = sunday_counts.get(sun, 0) + 1

    if not sunday_counts:
        return "ERROR: Could not parse any dates from date row"

    anchor_sunday = max(sunday_counts, key=sunday_counts.get)

    # Map date_col_ids → ISO date strings
    col_to_date = {}
    for k, cid in enumerate(date_col_ids):
        off = day_offsets_by_col.get(cid, k)
        iso = (anchor_sunday + __import__('datetime').timedelta(days=off)).isoformat()
        col_to_date[cid] = iso

    iso_dates = [col_to_date[cid] for cid in date_col_ids]
    first_date_obj = __import__('datetime').date.fromisoformat(iso_dates[0])
    month_name = first_date_obj.strftime("%B")
    year       = first_date_obj.year

    print(f"    Week: {anchor_sunday} → {iso_dates}", file=sys.stderr, flush=True)

    # Extract employee rows (rows after date row)
    employees = []
    for r in range(date_row_idx + 1, max_row + 1):
        raw_name = get(r, 0)
        if not raw_name:
            continue
        # Skip numeric rows (sales targets, totals)
        try:
            float(raw_name.replace(',', ''))
            continue
        except ValueError:
            pass
        # Skip known header/footer keywords
        if raw_name.lower().strip() in NON_EMPLOYEE:
            continue
        # Strip parenthetical annotations
        name = re.sub(r'\s*\(.*?\)', '', raw_name).strip()
        if not name:
            continue

        # Extract shifts for each date column
        shifts = []
        for cid, iso in col_to_date.items():
            raw_shift = get(r, cid)
            shift_val = normalize_shift(raw_shift) if raw_shift else ""
            shifts.append({"Date": iso, "Shift": shift_val})

        # Sort shifts by date
        shifts.sort(key=lambda s: s["Date"])

        employees.append({"Name": name, "Shifts": shifts})

    # Apply name filter
    if name_filter:
        employees = [e for e in employees
                     if name_filter.lower() in e["Name"].lower()]

    result = {
        "Month":     month_name,
        "Year":      year,
        "Employees": employees,
    }
    return json.dumps(result, ensure_ascii=False)


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: surya_cells.py <image_path> [name_filter]", file=sys.stderr)
        sys.exit(1)

    img_path    = sys.argv[1]
    name_filter = sys.argv[2] if len(sys.argv) > 2 else ""

    result = extract_schedule(img_path, name_filter)
    # Write UTF-8 to avoid Windows charmap encoding errors
    sys.stdout.buffer.write((result + '\n').encode('utf-8'))
    sys.stdout.buffer.flush()
    if result.startswith("ERROR:"):
        sys.exit(1)
