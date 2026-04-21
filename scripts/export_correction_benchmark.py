"""
Export API confirmed corrections into benchmark-style files.

Reads CalendarParse.Api jobs.db (ConfirmedCorrections table) and writes:
  - <name>.jpg          (copied source image when available)
  - <name>.answer.json  (truth data in benchmark format)

Usage:
  python scripts/export_correction_benchmark.py \
    --db "%LOCALAPPDATA%/CalendarParse/jobs.db" \
    --out "CalendarParse/calander-parse-test-imgs/live-benchmark"
"""

import argparse
import datetime as dt
import json
import os
import shutil
import sqlite3
from pathlib import Path


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--db", required=True, help="Path to jobs.db")
    p.add_argument("--out", required=True, help="Output folder")
    return p.parse_args()


def to_answer_json(shifts):
    by_employee = {}
    dates = []
    for s in shifts:
        emp = (s.get("Employee") or "").strip()
        d = (s.get("Date") or "").strip()
        t = (s.get("TimeRange") or "").strip()
        if not emp or not d:
            continue
        by_employee.setdefault(emp, []).append({"Date": d, "Shift": t})
        dates.append(d)

    for emp in by_employee:
        by_employee[emp].sort(key=lambda x: x["Date"])

    employees = [
        {"Name": emp, "Shifts": shifts}
        for emp, shifts in sorted(by_employee.items(), key=lambda kv: kv[0].lower())
    ]

    month = ""
    year = 0
    if dates:
        first = dt.date.fromisoformat(sorted(dates)[0])
        month = first.strftime("%B")
        year = first.year

    return {
        "Month": month,
        "Year": year,
        "Employees": employees,
    }


def main():
    args = parse_args()
    db_path = Path(os.path.expandvars(args.db)).expanduser().resolve()
    out_dir = Path(args.out).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)

    conn = sqlite3.connect(str(db_path))
    cur = conn.cursor()

    cur.execute(
        """
        SELECT Id, JobId, ImagePath, EmployeeName, ShiftsJson, ConfirmedAtUtc
        FROM ConfirmedCorrections
        ORDER BY Id
        """
    )
    rows = cur.fetchall()

    exported = 0
    for rid, job_id, image_path, employee_name, shifts_json, confirmed_at in rows:
        try:
            shifts = json.loads(shifts_json or "[]")
            answer = to_answer_json(shifts)

            stem = f"CORR ({rid})"
            answer_path = out_dir / f"{stem}.answer.json"
            answer_path.write_text(json.dumps(answer, indent=2), encoding="utf-8")

            if image_path and Path(image_path).exists():
                suffix = Path(image_path).suffix.lower() or ".jpg"
                image_out = out_dir / f"{stem}{suffix}"
                shutil.copy2(image_path, image_out)

            exported += 1
        except Exception as ex:
            print(f"WARN: skipping correction {rid}: {ex}")

    print(f"Exported {exported} correction truth rows to: {out_dir}")


if __name__ == "__main__":
    main()
