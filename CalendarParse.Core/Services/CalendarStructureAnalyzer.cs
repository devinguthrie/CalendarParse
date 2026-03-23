using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CalendarParse.Models;
using Rect = CalendarParse.Models.Rect;

namespace CalendarParse.Services
{
    /// <summary>
    /// Analyzes a grid of OCR'd <see cref="TableCell"/> objects to identify:
    /// <list type="bullet">
    ///   <item>The date header row (row containing day names or sequential dates)</item>
    ///   <item>The employee name column (leftmost column with non-date strings)</item>
    ///   <item>The anchor date and its column position (used to extrapolate all dates)</item>
    /// </list>
    /// Then assembles a <see cref="CalendarData"/> object.
    /// </summary>
    public class CalendarStructureAnalyzer : ICalendarStructureAnalyzer
    {
        // ── Regex patterns ────────────────────────────────────────────────────────

        // Day-of-week abbreviations / full names (case-insensitive)
        // Handles: SUN/SUNDAY, MON/MONDAY, TUE/TUES/TUESDAY, WED/WEDNESDAY,
        //          THU/THUR/THURS/THURSDAY, FRI/FRIDAY, SAT/SATURDAY
        private static readonly Regex DayNameRegex = new(
            @"^(sun(?:day)?|mon(?:day)?|tue(?:s(?:day)?)?|wed(?:nesday)?|thu(?:rs?(?:day)?)?|fri(?:day)?|sat(?:urday)?)\.?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Standalone day numbers 1–31
        private static readonly Regex DayNumberRegex = new(
            @"^\d{1,2}$",
            RegexOptions.Compiled);

        // M/D or MM/DD (optionally /YYYY)
        private static readonly Regex SlashDateRegex = new(
            @"^\d{1,2}/\d{1,2}(/\d{2,4})?$",
            RegexOptions.Compiled);

        // Month name (full or abbreviated)
        private static readonly Regex MonthRegex = new(
            @"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|" +
            @"jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Four-digit year
        private static readonly Regex YearRegex = new(@"\b(20\d{2})\b", RegexOptions.Compiled);

        // Matches two adjacent times with no hyphen separator, e.g. "3:008:30" → "3:00-8:30"
        private static readonly Regex ConcatTimeRegex = new(@"(\d{1,2}:\d{2})(\d{1,2}:\d{2})", RegexOptions.Compiled);

        // Matches time + partial second time missing colon, e.g. "10:00630" → "10:00-6:30"
        private static readonly Regex PartialSecondTimeRegex = new(@"(\d{1,2}:\d{2})[-]?(\d{1})(\d{2})\b", RegexOptions.Compiled);

        // Matches period-as-colon garble, e.g. "9.30600" → "9:30-6:00"
        private static readonly Regex PeriodColonTimeRegex = new(@"\b(\d{1,2})[.,](\d{2})[-]?(\d{1})(\d{2})\b", RegexOptions.Compiled);

        // ── Ordered day-name → DayOfWeek mapping ─────────────────────────────────
        private static readonly Dictionary<string, DayOfWeek> DayNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sun"] = DayOfWeek.Sunday,    ["sunday"]    = DayOfWeek.Sunday,
            ["mon"] = DayOfWeek.Monday,    ["monday"]    = DayOfWeek.Monday,
            ["tue"] = DayOfWeek.Tuesday,   ["tues"]      = DayOfWeek.Tuesday,  ["tuesday"]   = DayOfWeek.Tuesday,
            ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
            ["thu"] = DayOfWeek.Thursday,  ["thur"]      = DayOfWeek.Thursday, ["thurs"]     = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
            ["fri"] = DayOfWeek.Friday,    ["friday"]    = DayOfWeek.Friday,
            ["sat"] = DayOfWeek.Saturday,  ["saturday"]  = DayOfWeek.Saturday,
        };

        public CalendarData Analyze(List<TableCell> cells, List<OcrElement> ocrElements, StringBuilder? debug = null)
        {
            void D(string msg) => debug?.AppendLine(msg);

            if (cells.Count == 0)
            {
                D("WARN: No cells detected — returning empty CalendarData.");
                return new CalendarData { Month = "Unknown", Year = DateTime.UtcNow.Year };
            }

            D($"=== CalendarStructureAnalyzer ===");
            D($"Total cells: {cells.Count}  |  OCR elements: {ocrElements.Count}");

            // ── Step 1: Map OCR elements into the cell grid ───────────────────────
            MapOcrToCells(cells, ocrElements);

            int maxRow = cells.Max(c => c.Row);
            int maxCol = cells.Max(c => c.Col);
            D($"Grid dimensions: {maxRow + 1} rows x {maxCol + 1} cols");

            var textCells = cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
            D($"Cells with text after OCR mapping: {textCells.Count}");
            foreach (var tc in textCells)
                D($"  [{tc.Row},{tc.Col}] \"{tc.Text}\" conf={tc.Confidence:F2}");

            // ── Step 2: Find month/year from cells + raw OCR elements ─────────────
            ExtractMonthYear(cells, ocrElements, out string monthName, out int year);
            D($"Month/Year extracted: \"{monthName}\" / {(year == 0 ? "?" : year.ToString())}");

            // ── Step 3: Find header row (contains date-like values) ───────────────
            int headerRow = FindHeaderRow(cells, maxRow, debug);
            D($"Header row: {headerRow}");

            // ── Step 4: Find employee column (leftmost non-date text column) ───────
            int employeeCol = FindEmployeeColumn(cells, headerRow, maxRow);
            D($"Employee column: {employeeCol}");

            // ── Step 5: Build date map: col → DateOnly ────────────────────────────
            var dateMap = BuildDateMap(cells, headerRow, maxRow, maxCol, monthName, year, employeeCol, debug);

            // ── Step 5b: Derive month/year from date map if not found in image ────
            if (dateMap.Count > 0)
            {
                var earliest = dateMap.Values.Min();
                if (string.IsNullOrEmpty(monthName))
                {
                    monthName = earliest.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture);
                    D($"  Derived monthName from date map: \"{monthName}\"");
                }
                if (year == 0)
                {
                    year = earliest.Year;
                    D($"  Derived year from date map: {year}");
                }
            }
            // Final fallback
            if (string.IsNullOrEmpty(monthName)) monthName = DateTime.UtcNow.ToString("MMMM");
            if (year == 0) year = DateTime.UtcNow.Year;
            D($"Date map ({dateMap.Count} entries):");
            foreach (var kv in dateMap.OrderBy(k => k.Key))
                D($"  col {kv.Key} → {kv.Value:yyyy-MM-dd}");

            // ── Step 6: Assemble CalendarData ─────────────────────────────────────
            var calendarData = new CalendarData
            {
                Month = monthName,
                Year = year,
                CapturedAt = DateTime.UtcNow
            };

            var employeeRows = cells
                .Where(c => c.Row > headerRow && c.Col == employeeCol && !string.IsNullOrWhiteSpace(c.Text))
                .OrderBy(c => c.Row)
                .ToList();

            D($"Employee rows found: {employeeRows.Count}");
            foreach (var er in employeeRows)
                D($"  row {er.Row}: \"{er.Text}\"");

            foreach (var empCell in employeeRows)
            {
                var schedule = new EmployeeSchedule { Name = empCell.Text.Trim() };

                var shiftCells = cells
                    .Where(c => c.Row == empCell.Row && c.Col != employeeCol)
                    .ToList();

                // Supplement with next anonymous sub-row if present.
                // Some schedules render RTO markers in a visual sub-row above the actual
                // shift times, so the named row ends up full of "RTO" while the real time
                // data lives in the row immediately below.
                int nextRow = empCell.Row + 1;
                if (nextRow <= maxRow && !employeeRows.Any(er => er.Row == nextRow))
                {
                    foreach (var subCell in cells.Where(c => c.Row == nextRow && c.Col != employeeCol))
                    {
                        // Only merge sub-row cells with meaningful content
                        var subNorm = NormalizeShiftText(subCell.Text);
                        bool subUseful = ContainsTimePattern(subCell.Text) || subNorm == "x" || subNorm == "";
                        if (!subUseful) continue;

                        var existing = shiftCells.FirstOrDefault(s => s.Col == subCell.Col);
                        if (existing is null)
                            shiftCells.Add(subCell);
                        else if (!ContainsTimePattern(existing.Text) && ContainsTimePattern(subCell.Text))
                            shiftCells[shiftCells.IndexOf(existing)] = subCell;
                    }
                }

                shiftCells = shiftCells.OrderBy(c => c.Col).ToList();

                var usedDates = new HashSet<DateOnly>();

                // Pass 1: exact column → date map matches
                foreach (var shiftCell in shiftCells)
                {
                    if (!dateMap.TryGetValue(shiftCell.Col, out var date)) continue;
                    if (!usedDates.Add(date)) continue;
                    schedule.Shifts.Add(new ShiftEntry
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        Shift = NormalizeShiftText(shiftCell.Text)
                    });
                }

                // Pass 2: ±1 column tolerance for cells with no exact date mapping
                // (handles slight OCR alignment offsets between header and body rows)
                foreach (var shiftCell in shiftCells)
                {
                    if (dateMap.ContainsKey(shiftCell.Col)) continue; // already tried in pass 1
                    DateOnly date;
                    if (!dateMap.TryGetValue(shiftCell.Col - 1, out date) &&
                        !dateMap.TryGetValue(shiftCell.Col + 1, out date))
                        continue;
                    if (!usedDates.Add(date)) continue; // skip if date claimed by exact match
                    schedule.Shifts.Add(new ShiftEntry
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        Shift = NormalizeShiftText(shiftCell.Text)
                    });
                }

                if (schedule.Shifts.Count > 0)
                    calendarData.Employees.Add(schedule);
            }

            D($"Employees with shifts: {calendarData.Employees.Count}");
            return calendarData;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static void MapOcrToCells(List<TableCell> cells, List<OcrElement> ocrElements)
        {
            foreach (var elem in ocrElements)
            {
                var bestCell = cells
                    .Select(c => (cell: c, overlap: ComputeOverlapArea(elem.Bounds, c.Bounds)))
                    .Where(x => x.overlap > 0)
                    .OrderByDescending(x => x.overlap)
                    .FirstOrDefault();

                if (bestCell.cell is not null)
                {
                    bestCell.cell.Text = string.IsNullOrWhiteSpace(bestCell.cell.Text)
                        ? elem.Text
                        : bestCell.cell.Text + " " + elem.Text;
                    if (bestCell.cell.Confidence < elem.Confidence)
                        bestCell.cell.Confidence = elem.Confidence;
                }
            }
        }

        private static int ComputeOverlapArea(Rect a, Rect b)
        {
            int ox = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
            int oy = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
            return ox * oy;
        }

        private static void ExtractMonthYear(List<TableCell> cells, List<OcrElement> ocrElements, out string monthName, out int year)
        {
            monthName = string.Empty;
            year = 0; // 0 = not yet found

            // Scan cells first
            foreach (var cell in cells)
            {
                if (string.IsNullOrWhiteSpace(cell.Text)) continue;
                if (string.IsNullOrEmpty(monthName))
                {
                    var m = MonthRegex.Match(cell.Text);
                    if (m.Success)
                        monthName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(m.Value.ToLower());
                }
                var y = YearRegex.Match(cell.Text);
                if (y.Success) year = int.Parse(y.Value);
                if (!string.IsNullOrEmpty(monthName) && year != 0) return;
            }

            // Also scan raw OCR elements (catches header text that may not map cleanly into cells)
            foreach (var elem in ocrElements)
            {
                if (string.IsNullOrWhiteSpace(elem.Text)) continue;
                if (string.IsNullOrEmpty(monthName))
                {
                    var m = MonthRegex.Match(elem.Text);
                    if (m.Success)
                        monthName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(m.Value.ToLower());
                }
                if (year == 0)
                {
                    var y = YearRegex.Match(elem.Text);
                    if (y.Success) year = int.Parse(y.Value);
                }
                if (!string.IsNullOrEmpty(monthName) && year != 0) return;
            }
        }

        private static int FindHeaderRow(List<TableCell> cells, int maxRow, StringBuilder? debug = null)
        {
            int bestRow = 0;
            int bestScore = 0;

            for (int r = 0; r <= Math.Min(maxRow, 6); r++)
            {
                var rowCells = cells.Where(c => c.Row == r).ToList();
                // Day names are the strongest signal (×10), slash dates strong (×4), bare numbers weak (×1)
                // Use ExtractDayName so noisy OCR tokens like "[sun" and "| MON" are credited.
                int score =
                    rowCells.Count(c => ExtractDayName(c.Text) != null) * 10
                  + rowCells.Count(c => SlashDateRegex.IsMatch(CleanText(c.Text))) * 4
                  + rowCells.Count(c => DayNumberRegex.IsMatch(c.Text));

                debug?.AppendLine($"  FindHeaderRow row={r}: {rowCells.Count} cells, score={score}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRow = r;
                }
            }
            return bestRow;
        }

        private static int FindEmployeeColumn(List<TableCell> cells, int headerRow, int maxRow)
        {
            int maxCol = cells.Max(c => c.Col);
            for (int col = 0; col <= maxCol; col++)
            {
                var colCells = cells.Where(c => c.Col == col && c.Row > headerRow).ToList();
                if (colCells.Count == 0) continue;

                int nonDateCount = colCells.Count(c =>
                    !string.IsNullOrWhiteSpace(c.Text) &&
                    !DayNameRegex.IsMatch(c.Text) &&
                    !DayNumberRegex.IsMatch(c.Text) &&
                    !SlashDateRegex.IsMatch(c.Text));

                if (nonDateCount >= colCells.Count / 2.0)
                    return col;
            }
            return 0;
        }

        private Dictionary<int, DateOnly> BuildDateMap(
            List<TableCell> cells, int headerRow, int maxRow, int maxCol,
            string monthName, int year, int employeeCol, StringBuilder? debug = null)
        {
            var headerCells = cells
                .Where(c => c.Row == headerRow && c.Col != employeeCol)
                .OrderBy(c => c.Col)
                .ToList();

            debug?.AppendLine($"BuildDateMap: {headerCells.Count} header cells (row {headerRow}, excl. empCol {employeeCol})");
            foreach (var hc in headerCells)
                debug?.AppendLine($"  hcell[{hc.Col}] \"{hc.Text}\"");

            var map = new Dictionary<int, DateOnly>();
            int monthNumber = ParseMonthNumber(monthName);
            if (monthNumber < 1) monthNumber = DateTime.UtcNow.Month;
            int safeYear = year > 0 ? year : DateTime.UtcNow.Year;

            // ── Try 1: Explicit slash dates in header row — map each directly ────
            var slashInHeader = headerCells
                .Select(c => (cell: c, cleaned: CleanText(c.Text)))
                .Where(x => SlashDateRegex.IsMatch(x.cleaned))
                .ToList();

            if (slashInHeader.Count > 0)
            {
                debug?.AppendLine($"  Direct slash-date mapping ({slashInHeader.Count} cells):");
                foreach (var (cell, cleaned) in slashInHeader)
                {
                    map[cell.Col] = ParseAnchorDate(cleaned, monthNumber, safeYear);
                    debug?.AppendLine($"    col={cell.Col} \"{cell.Text}\" → {map[cell.Col]:yyyy-MM-dd}");
                }
                // Fill remaining header cells via nearest dated column + single-day step
                var dated = slashInHeader.OrderBy(x => x.cell.Col).ToList();
                foreach (var cell in headerCells.Where(c => !map.ContainsKey(c.Col)))
                {
                    var nearest = dated.OrderBy(d => Math.Abs(d.cell.Col - cell.Col)).First();
                    map[cell.Col] = nearest.cell.Col < cell.Col
                        ? map[nearest.cell.Col].AddDays(cell.Col - nearest.cell.Col)
                        : map[nearest.cell.Col].AddDays(-(nearest.cell.Col - cell.Col));
                }
                return map;
            }

            // ── Try 1b: Single numeric anchor in header row ────────────────────
            var numericAnchor = headerCells
                .Where(c => IsNumericDate(c.Text, monthNumber, safeYear))
                .OrderByDescending(c => c.Confidence)
                .FirstOrDefault();

            if (numericAnchor is not null)
            {
                DateOnly anchorDate = ParseAnchorDate(CleanText(numericAnchor.Text), monthNumber, safeYear);
                debug?.AppendLine($"  Numeric anchor: col={numericAnchor.Col} \"{numericAnchor.Text}\" → {anchorDate:yyyy-MM-dd}");
                foreach (var cell in headerCells)
                {
                    int delta = cell.Col - numericAnchor.Col;
                    map[cell.Col] = anchorDate.AddDays(delta);
                }
                return map;
            }

            debug?.AppendLine("  No numeric/slash anchor in header row — searching adjacent rows and day names.");

            // Identify day-name cells in the header (handles noise like "rt FRI", "=] SAT")
            var dayNameCells = headerCells
                .Where(c => ExtractDayName(c.Text) != null)
                .OrderBy(c => c.Col)
                .ToList();

            // ── Try 2: Slash dates in adjacent rows paired with day-name header ──
            DateOnly adjAnchorDate = default;
            int adjAnchorIdx = -1;

            for (int adjOffset = 1; adjOffset <= 2 && adjAnchorIdx < 0; adjOffset++)
            {
                foreach (int dir in new[] { 1, -1 })
                {
                    int adjRow = headerRow + dir * adjOffset;
                    if (adjRow < 0 || adjRow > maxRow) continue;

                    var adjByCol = cells.Where(c => c.Row == adjRow).ToDictionary(c => c.Col);
                    debug?.AppendLine($"  Checking adjacent row {adjRow} for slash dates...");

                    for (int i = 0; i < dayNameCells.Count; i++)
                    {
                        var dayCell = dayNameCells[i];
                        if (!adjByCol.TryGetValue(dayCell.Col, out var adjCell)) continue;

                        string cleaned = CleanText(adjCell.Text);
                        if (!SlashDateRegex.IsMatch(cleaned)) continue;

                        // Extract year from slash date if available (overrides uncertain year)
                        DateOnly parsed = ParseAnchorDate(cleaned, monthNumber, safeYear);
                        adjAnchorDate = parsed;
                        adjAnchorIdx = i;
                        debug?.AppendLine($"  Adjacent row anchor: row={adjRow} col={dayCell.Col} \"{adjCell.Text}\" → {parsed:yyyy-MM-dd}");

                        // Directly map any slash-dated cells in this row
                        foreach (var (dc, di) in dayNameCells.Select((c, ii) => (c, ii)))
                        {
                            if (!adjByCol.TryGetValue(dc.Col, out var ac)) continue;
                            string c2 = CleanText(ac.Text);
                            if (SlashDateRegex.IsMatch(c2))
                            {
                                map[dc.Col] = ParseAnchorDate(c2, monthNumber, safeYear);
                                debug?.AppendLine($"  Direct map: col={dc.Col} \"{ac.Text}\" → {map[dc.Col]:yyyy-MM-dd}");
                            }
                        }
                        break;
                    }
                    if (adjAnchorIdx >= 0) break;
                }
            }

            if (adjAnchorIdx >= 0 && dayNameCells.Count > 0)
            {
                // Fill remaining day-name columns using index offset from anchor
                foreach (var (dayCell, idx) in dayNameCells.Select((c, i) => (c, i)))
                {
                    if (!map.ContainsKey(dayCell.Col))
                    {
                        var derived = adjAnchorDate.AddDays(idx - adjAnchorIdx);
                        map[dayCell.Col] = derived;
                        debug?.AppendLine($"  Index-derived: col={dayCell.Col} ({ExtractDayName(dayCell.Text)}) → {derived:yyyy-MM-dd}");
                    }
                }

                // When only one day-name was OCR'd, the index-derived fill covers just that column.
                // Extend to the full 7-day week using the standard 2-column-per-day spacing
                // observed in these schedules (odd cols = shift days, even cols = hours).
                if (dayNameCells.Count == 1)
                {
                    var anchorCol = dayNameCells[0].Col;
                    int anchorDow = (int)adjAnchorDate.DayOfWeek; // 0=Sun, 1=Mon, ..., 6=Sat
                    var sunday = adjAnchorDate.AddDays(-anchorDow);
                    int sunCol = anchorCol - anchorDow * 2;
                    debug?.AppendLine($"  Single-anchor week extension: SUN={sunday:yyyy-MM-dd} at col {sunCol} (spacing=2)");
                    for (int d = 0; d < 7; d++)
                    {
                        int col = sunCol + d * 2;
                        if (col >= 0 && !map.ContainsKey(col))
                        {
                            map[col] = sunday.AddDays(d);
                            debug?.AppendLine($"  Week-derived: col={col} → {map[col]:yyyy-MM-dd}");
                        }
                    }
                }
                return map;
            }

            // ── Try 3: Day-name fallback using index-based offset ──────────────
            if (dayNameCells.Count > 0)
            {
                debug?.AppendLine("  No adjacent slash dates — using day-name index fallback.");
                var firstDayCell = dayNameCells[0];
                string? firstName = ExtractDayName(firstDayCell.Text);
                if (firstName != null && DayNameMap.TryGetValue(firstName.ToLower(), out var dayOfWeek))
                {
                    var firstOfMonth = new DateOnly(safeYear, monthNumber, 1);
                    int daysUntil = ((int)dayOfWeek - (int)firstOfMonth.DayOfWeek + 7) % 7;
                    DateOnly anchorDate = firstOfMonth.AddDays(daysUntil);
                    debug?.AppendLine($"  Day-name anchor: col={firstDayCell.Col} \"{firstName}\" ({dayOfWeek}) → {anchorDate:yyyy-MM-dd}");

                    foreach (var (dayCell, idx) in dayNameCells.Select((c, i) => (c, i)))
                        map[dayCell.Col] = anchorDate.AddDays(idx);
                }
                return map;
            }

            debug?.AppendLine("  No day-name anchor found — falling back to raw day-number parsing.");

            // ── Try 4: Raw day-number parsing ──────────────────────────────────
            foreach (var cell in headerCells)
            {
                if (int.TryParse(cell.Text.Trim(), out int day) && day >= 1 && day <= 31)
                {
                    try
                    {
                        map[cell.Col] = new DateOnly(safeYear, monthNumber, day);
                        debug?.AppendLine($"  Raw day: col={cell.Col} \"{cell.Text}\" → {map[cell.Col]:yyyy-MM-dd}");
                    }
                    catch { }
                }
            }

            return map;
        }

        private static bool IsNumericDate(string text, int month, int year)
        {
            if (!int.TryParse(text.Trim(), out int day)) return false;
            if (day < 1 || day > 31) return false;
            try { _ = new DateOnly(year, month, day); return true; }
            catch { return false; }
        }

        /// <summary>Strips leading/trailing punctuation and brackets, leaving core text.</summary>
        private static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Trim();
            int start = 0, end = text.Length - 1;
            while (start <= end && !char.IsLetterOrDigit(text[start]) && text[start] != '/')
                start++;
            while (end >= start && !char.IsLetterOrDigit(text[end]) && text[end] != '/')
                end--;
            return start <= end ? text[start..(end + 1)] : string.Empty;
        }

        /// <summary>Extracts a day-name token from text that may contain noise (e.g. "rt FRI" → "FRI").</summary>
        private static string? ExtractDayName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var word in text.Split(new[] { ' ', '\t', '[', ']', '=', '|', '(', ')' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = word.Trim('.', ',', ';', '\'', '"');
                if (DayNameRegex.IsMatch(trimmed)) return trimmed;
            }
            return null;
        }

        private static DateOnly ParseAnchorDate(string text, int month, int year)
        {
            if (SlashDateRegex.IsMatch(text))
            {
                var parts = text.Split('/');
                if (int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int d))
                {
                    int y = parts.Length > 2 && int.TryParse(parts[2], out int yr) ? yr : year;
                    if (y < 100) y += 2000;
                    try { return new DateOnly(y, m, d); } catch { }
                }
            }
            if (int.TryParse(text.Trim(), out int day))
            {
                try { return new DateOnly(year, month, day); } catch { }
            }
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }

        private static string NormalizeShiftText(string text)
        {
            var t = text.Trim().Trim('[', ']').Trim();
            var upper = t.ToUpperInvariant();
            // Normalize common "day off" codes to canonical "x"
            if (upper == "RTO" || upper == "PTO" || upper == "OFF" ||
                upper == "X" || upper == "DAY OFF" || upper == "VAC" ||
                upper == "VACATION" || upper == "RIO")   // "RIO" = garbled "RTO"
                return "x";
            // "IT" is a recurring OCR artifact for visually blank cells
            if (upper == "IT")
                return "";
            // Fix period-as-colon + concat, e.g. "9.30600" → "9:30-6:00" (must run before ConcatTimeRegex)
            t = PeriodColonTimeRegex.Replace(t, "$1:$2-$3:$4");
            // Fix concatenated times missing a hyphen (e.g. "3:008:30" → "3:00-8:30")
            t = ConcatTimeRegex.Replace(t, "$1-$2");
            // Fix time + partial second time missing colon (e.g. "10:00630" → "10:00-6:30")
            t = PartialSecondTimeRegex.Replace(t, "$1-$2:$3");
            return t;
        }

        private static bool ContainsTimePattern(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            (System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{1,2}:\d{2}") ||
             System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{1,2}[.,]\d{2}"));

        private static int ParseMonthNumber(string monthName)
        {
            if (DateTime.TryParseExact(monthName, new[] { "MMMM", "MMM" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.Month;
            return -1;
        }
    }
}
