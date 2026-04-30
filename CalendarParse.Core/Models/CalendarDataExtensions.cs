namespace CalendarParse.Models
{
    public static class CalendarDataExtensions
    {
        /// <summary>
        /// Flattens a <see cref="CalendarData"/> into a list of <see cref="ShiftData"/> rows,
        /// one per employee/date pair. Safe to call on a null receiver (returns empty list).
        /// </summary>
        public static List<ShiftData> FlattenToShiftData(this CalendarData? data)
        {
            if (data?.Employees is null)
                return [];

            return data.Employees
                .SelectMany(e => (e.Shifts ?? []).Select(s => new ShiftData
                {
                    Employee  = e.Name,
                    Date      = s.Date,
                    TimeRange = s.Shift,
                }))
                .ToList();
        }
    }
}
