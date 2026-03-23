namespace CalendarParse.Models
{
    public class CalendarData
    {
        public string Month { get; set; } = string.Empty;
        public int Year { get; set; }
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
        public List<EmployeeSchedule> Employees { get; set; } = new();
    }
}
