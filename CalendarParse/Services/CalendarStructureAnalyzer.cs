using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CalendarParse.Models;
using Rect = CalendarParse.Models.Rect;

#if ANDROID
using Android.Util;
#endif

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
        private static readonly Regex DayNameRegex = new(
            @"^(sun|mon|tue|wed|thu|fri|sat)(day)?\.?$",
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

        // ── Ordered day-name → DayOfWeek mapping ─────────────────────────────────
        private static readonly Dictionary<string, DayOfWeek> DayNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sun"] = DayOfWeek.Sunday, ["sunday"]    = DayOfWeek.Sunday,
            ["mon"] = DayOfWeek.Monday, ["monday"]    = DayOfWeek.Monday,
            ["tue"] = DayOfWeek.Tuesday, ["tuesday"]  = DayOfWeek.Tuesday,
            ["wed"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
            ["thu"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
            ["fri"] = DayOfWeek.Friday, ["friday"]    = DayOfWeek.Friday,
            ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        };

        public CalendarData Analyze(List<TableCell> cells, List<OcrElement> ocrElements, StringBuilder? debug = null)
        {
            void D(string msg)
            {
                debug?.AppendLine(msg);
#if ANDROID
                Log.Debug("CalParse.Analyzer", msg);
#endif
            }

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

            // Log all cells that have text
            var textCells = cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
            D($"Cells with text after OCR mapping: {textCells.Count}");
            foreach (var tc in textCells)
                D($"  [{tc.Row},{tc.Col}] \"{tc.Text}\" conf={tc.Confidence:F2}");

            // ── Step 2: Find month/year from any cell ─────────────────────────────
            ExtractMonthYear(cells, out string monthName, out int year);
            D($"Month/Year extracted: \"{monthName}\" / {year}");

            // ── Step 3: Find header row (contains date-like values) ───────────────
            int headerRow = FindHeaderRow(cells, maxRow, debug);
            D($"Header row: {headerRow}");

            // ── Step 4: Find employee column (leftmost non-date text column) ───────
            int employeeCol = FindEmployeeColumn(cells, headerRow, maxRow);
            D($"Employee column: {employeeCol}");

            // ── Step 5: Build date map: col → DateOnly ────────────────────────────
            var dateMap = BuildDateMap(cells, headerRow, maxRow, maxCol, monthName, year, employeeCol, debug);
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

            // Collect employee rows (all non-header rows, non-empty employee column)
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

                // Collect shift cells in this employee's row
                var shiftCells = cells
                    .Where(c => c.Row == empCell.Row && c.Col != employeeCol)
                    .OrderBy(c => c.Col);

                foreach (var shiftCell in shiftCells)
                {
                    if (!dateMap.TryGetValue(shiftCell.Col, out var date)) continue;
                    schedule.Shifts.Add(new ShiftEntry
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        Shift = shiftCell.Text.Trim()
                    });
                }

                if (schedule.Shifts.Count > 0)
                    calendarData.Employees.Add(schedule);
            }

            D($"Employees with shifts: {calendarData.Employees.Count}");
            return calendarData;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// For each OCR element, find the cell with the greatest overlap and assign its text.
        /// </summary>
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

        private static void ExtractMonthYear(List<TableCell> cells, out string monthName, out int year)
        {
            monthName = string.Empty;
            year = DateTime.UtcNow.Year;

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
                if (y.Success)
                    year = int.Parse(y.Value);

                if (!string.IsNullOrEmpty(monthName) && year != DateTime.UtcNow.Year) break;
            }

            if (string.IsNullOrEmpty(monthName))
                monthName = DateTime.UtcNow.ToString("MMMM");
        }

        /// <summary>
        /// Returns the row index whose cells most closely match date/day-name patterns.
        /// Falls back to row 0 if nothing is found.
        /// </summary>
        private static int FindHeaderRow(List<TableCell> cells, int maxRow, StringBuilder? debug = null)
        {
            int bestRow = 0;
            int bestScore = 0;

            for (int r = 0; r <= Math.Min(maxRow, 3); r++)
            {
                var rowCells = cells.Where(c => c.Row == r).ToList();
                int score = rowCells.Count(c =>
                    DayNameRegex.IsMatch(c.Text) ||
                    DayNumberRegex.IsMatch(c.Text) ||
                    SlashDateRegex.IsMatch(c.Text));

                debug?.AppendLine($"  FindHeaderRow row={r}: {rowCells.Count} cells, {score} date-like");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRow = r;
                }
            }
            return bestRow;
        }

        /// <summary>
        /// Finds the leftmost column in the data rows that consistently contains
        /// non-date, non-empty text (employee names).
        /// </summary>
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

                // If at least half the cells look like names, this is the employee column
                if (nonDateCount >= colCells.Count / 2.0)
                    return col;
            }
            return 0;
        }

        /// <summary>
        /// Builds a mapping from column index → <see cref="DateOnly"/> using the header row.
        /// Uses the anchor-date strategy: finds the highest-confidence date cell, determines
        /// its weekday position, and extrapolates from there.
        /// </summary>
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

            // Try to parse a known month/year for anchor resolution
            int monthNumber = ParseMonthNumber(monthName);
            if (monthNumber < 1) monthNumber = DateTime.UtcNow.Month;

            // Look for an anchor: a cell with a parseable full or slash date (highest confidence)
            TableCell? anchor = headerCells
                .Where(c => SlashDateRegex.IsMatch(c.Text) || IsNumericDate(c.Text, monthNumber, year))
                .OrderByDescending(c => c.Confidence)
                .FirstOrDefault();

            if (anchor is not null)
            {
                DateOnly anchorDate = ParseAnchorDate(anchor.Text, monthNumber, year);
                debug?.AppendLine($"  Anchor cell found: col={anchor.Col} \"{anchor.Text}\" → {anchorDate:yyyy-MM-dd}");

                // Extrapolate: col offset from anchor
                foreach (var cell in headerCells)
                {
                    int delta = cell.Col - anchor.Col;
                    map[cell.Col] = anchorDate.AddDays(delta);
                }
                return map;
            }

            debug?.AppendLine("  No numeric/slash anchor found — trying day-name fallback.");

            // Fallback: use day names to determine weekday offsets, combine with month/year
            // Look for a day-name header row and build from the 1st of the month
            TableCell? firstDayCell = headerCells.FirstOrDefault(c => DayNameRegex.IsMatch(c.Text));
            if (firstDayCell is not null)
            {
                var dayOfWeek = DayNameMap.GetValueOrDefault(
                    firstDayCell.Text.Trim().ToLower(), DayOfWeek.Sunday);

                // Find the first occurrence of that weekday in the month
                var firstOfMonth = new DateOnly(year, monthNumber, 1);
                int daysUntil = ((int)dayOfWeek - (int)firstOfMonth.DayOfWeek + 7) % 7;
                DateOnly anchorDate = firstOfMonth.AddDays(daysUntil);

                debug?.AppendLine($"  Day-name anchor: col={firstDayCell.Col} \"{firstDayCell.Text}\" ({dayOfWeek}) → {anchorDate:yyyy-MM-dd}");

                foreach (var cell in headerCells)
                {
                    int delta = cell.Col - firstDayCell.Col;
                    map[cell.Col] = anchorDate.AddDays(delta);
                }
                return map;
            }

            debug?.AppendLine("  No day-name anchor found — falling back to raw day-number parsing.");

            // Last resort: treat header numbers as day-of-month
            foreach (var cell in headerCells)
            {
                if (int.TryParse(cell.Text.Trim(), out int day) && day >= 1 && day <= 31)
                {
                    try
                    {
                        map[cell.Col] = new DateOnly(year, monthNumber, day);
                        debug?.AppendLine($"  Raw day: col={cell.Col} \"{cell.Text}\" → {map[cell.Col]:yyyy-MM-dd}");
                    }
                    catch { /* skip invalid dates */ }
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

        private static DateOnly ParseAnchorDate(string text, int month, int year)
        {
            // M/D or MM/DD
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
            // Plain day number
            if (int.TryParse(text.Trim(), out int day))
            {
                try { return new DateOnly(year, month, day); } catch { }
            }
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }

        private static int ParseMonthNumber(string monthName)
        {
            if (DateTime.TryParseExact(monthName, new[] { "MMMM", "MMM" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.Month;
            return -1;
        }
    }
}
