using CalendarParse.Models;

namespace CalendarParse.Services;

/// <summary>
/// Maps <see cref="CalendarData"/> + <see cref="CellPositionsSnapshot"/> to a flat list
/// of <see cref="ShiftData"/> with populated <see cref="BoundingBox"/> values.
///
/// Extracted from HybridCalendarService so it can be unit-tested without WinRT / Ollama.
/// </summary>
public static class ShiftBoundsMapper
{
    /// <summary>
    /// Converts <paramref name="data"/> and <paramref name="positions"/> into
    /// a flat list of <see cref="ShiftData"/>.
    ///
    /// For each shift, the date string is parsed to a <see cref="DayOfWeek"/> (Sunday=0…Saturday=6),
    /// which keys into <paramref name="positions"/>.<see cref="CellPositionsSnapshot.Columns"/>
    /// to get X bounds, and the employee name keys into
    /// <paramref name="positions"/>.<see cref="CellPositionsSnapshot.Rows"/> to get the Y center.
    /// </summary>
    public static List<ShiftData> Map(CalendarData data, CellPositionsSnapshot? positions)
    {
        var result = new List<ShiftData>();

        foreach (var employee in data.Employees)
        {
            RowSnapshot? row = null;
            if (positions?.Rows is not null)
            {
                positions.Rows.TryGetValue(employee.Name, out row);
                if (row is null)
                {
                    var key = positions.Rows.Keys.FirstOrDefault(k =>
                        k.Equals(employee.Name, StringComparison.OrdinalIgnoreCase));
                    if (key is not null) positions.Rows.TryGetValue(key, out row);
                }
            }

            foreach (var entry in employee.Shifts)
            {
                BoundingBox? bounds = null;

                if (positions is not null
                    && row is not null
                    && DateTime.TryParse(entry.Date, out var dt))
                {
                    var dayIndex = (int)dt.DayOfWeek; // 0=Sun…6=Sat
                    if (positions.Columns.TryGetValue(dayIndex, out var col))
                    {
                        var halfRow = Math.Max(positions.EstimatedRowHeightPx / 2, 10);
                        bounds = new BoundingBox
                        {
                            X      = col.XStart,
                            Y      = Math.Max(0, row.CellCenterY - halfRow),
                            Width  = Math.Max(1, col.XEnd - col.XStart),
                            Height = Math.Max(1, positions.EstimatedRowHeightPx),
                        };
                    }
                }

                result.Add(new ShiftData
                {
                    Employee        = employee.Name,
                    Date            = entry.Date,
                    TimeRange       = entry.Shift,
                    EstimatedBounds = bounds,
                });
            }
        }

        return result;
    }
}

// Lightweight snapshots used by ShiftBoundsMapper — decoupled from CellPositions
// so Core has no dependency on the CLI namespace.

public class CellPositionsSnapshot
{
    public int EstimatedRowHeightPx { get; set; }
    public Dictionary<int, ColSnapshot>  Columns { get; set; } = new();
    public Dictionary<string, RowSnapshot> Rows  { get; set; } = new();
}

public class ColSnapshot
{
    public int XStart { get; set; }
    public int XEnd   { get; set; }
}

public class RowSnapshot
{
    public int CellCenterY { get; set; }
}
