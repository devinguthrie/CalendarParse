namespace CalendarParse.Models
{
    public class EmployeeSchedule
    {
        public string Name { get; set; } = string.Empty;
        public List<ShiftEntry> Shifts { get; set; } = new();
    }
}
