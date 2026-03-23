namespace CalendarParse.Models
{
    public class ShiftEntry
    {
        /// <summary>ISO-8601 date string, e.g. "2026-02-03"</summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>Raw shift text read from the cell, e.g. "8am-4pm" or "OFF"</summary>
        public string Shift { get; set; } = string.Empty;
    }
}
