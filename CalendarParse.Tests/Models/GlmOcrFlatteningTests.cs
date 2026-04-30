using CalendarParse.Models;

namespace CalendarParse.Tests.Models;

public class GlmOcrFlatteningTests
{
    [Fact]
    public void FlattenToShiftData_NullReceiver_ReturnsEmptyList()
    {
        CalendarData? data = null;
        Assert.Empty(data.FlattenToShiftData());
    }

    [Fact]
    public void FlattenToShiftData_EmptyEmployees_ReturnsEmptyList()
    {
        var data = new CalendarData { Employees = [] };
        Assert.Empty(data.FlattenToShiftData());
    }

    [Fact]
    public void FlattenToShiftData_EmployeeWithNoShifts_ReturnsEmptyList()
    {
        var data = new CalendarData
        {
            Employees = [new EmployeeSchedule { Name = "Alice", Shifts = [] }]
        };
        Assert.Empty(data.FlattenToShiftData());
    }

    [Fact]
    public void FlattenToShiftData_SingleEmployeeSingleShift_ReturnsOneRow()
    {
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule
                {
                    Name   = "Alice",
                    Shifts = [new ShiftEntry { Date = "2025-11-03", Shift = "9am-5pm" }],
                }
            ]
        };

        var result = data.FlattenToShiftData();

        var row = Assert.Single(result);
        Assert.Equal("Alice",      row.Employee);
        Assert.Equal("2025-11-03", row.Date);
        Assert.Equal("9am-5pm",    row.TimeRange);
    }

    [Fact]
    public void FlattenToShiftData_SingleEmployee_MultipleShifts_CorrectCount()
    {
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule
                {
                    Name = "Alice",
                    Shifts =
                    [
                        new ShiftEntry { Date = "2025-11-03", Shift = "9am-5pm"  },
                        new ShiftEntry { Date = "2025-11-04", Shift = "10am-6pm" },
                        new ShiftEntry { Date = "2025-11-05", Shift = "OFF"      },
                    ]
                }
            ]
        };

        Assert.Equal(3, data.FlattenToShiftData().Count);
    }

    [Fact]
    public void FlattenToShiftData_MultipleEmployees_CorrectTotalCount()
    {
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule
                {
                    Name   = "Alice",
                    Shifts = [new ShiftEntry { Date = "2025-11-03", Shift = "9am-5pm" }, new ShiftEntry { Date = "2025-11-04", Shift = "OFF" }],
                },
                new EmployeeSchedule
                {
                    Name   = "Bob",
                    Shifts = [new ShiftEntry { Date = "2025-11-03", Shift = "1pm-9pm" }],
                },
            ]
        };

        Assert.Equal(3, data.FlattenToShiftData().Count);
    }

    [Fact]
    public void FlattenToShiftData_EmployeeNamePropagatedToEachRow()
    {
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule
                {
                    Name   = "Charlie",
                    Shifts =
                    [
                        new ShiftEntry { Date = "2025-11-03", Shift = "9am-5pm" },
                        new ShiftEntry { Date = "2025-11-04", Shift = "9am-5pm" },
                    ],
                }
            ]
        };

        Assert.All(data.FlattenToShiftData(), row => Assert.Equal("Charlie", row.Employee));
    }

    [Fact]
    public void FlattenToShiftData_ShiftEntryShiftField_MapsToTimeRange()
    {
        // Verifies ShiftEntry.Shift (NOT a property called TimeRange) → ShiftData.TimeRange
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule
                {
                    Name   = "Alice",
                    Shifts = [new ShiftEntry { Date = "2025-11-03", Shift = "2pm-10pm" }],
                }
            ]
        };

        Assert.Equal("2pm-10pm", data.FlattenToShiftData()[0].TimeRange);
    }

    [Fact]
    public void FlattenToShiftData_MixedEmptyAndNonEmptyEmployees_OnlyNonEmptyContributed()
    {
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule { Name = "Empty1", Shifts = [] },
                new EmployeeSchedule
                {
                    Name   = "Alice",
                    Shifts = [new ShiftEntry { Date = "2025-11-03", Shift = "9am-5pm" }],
                },
                new EmployeeSchedule { Name = "Empty2", Shifts = [] },
            ]
        };

        var result = data.FlattenToShiftData();
        var row = Assert.Single(result);
        Assert.Equal("Alice", row.Employee);
    }

    [Fact]
    public void FlattenToShiftData_EstimatedBounds_IsNullForAllRows()
    {
        // GLM-OCR does not populate bounding boxes; extension never sets EstimatedBounds
        var data = new CalendarData
        {
            Employees =
            [
                new EmployeeSchedule
                {
                    Name   = "Alice",
                    Shifts = [new ShiftEntry { Date = "2025-11-03", Shift = "9am-5pm" }],
                }
            ]
        };

        Assert.All(data.FlattenToShiftData(), row => Assert.Null(row.EstimatedBounds));
    }
}
