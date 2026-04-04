using CalendarParse.Models;
using CalendarParse.Services;
using static CalendarParse.Services.EmployeeNameMatcher;

namespace CalendarParse.Tests;

public class EmployeeNameMatcherTests
{
    private static List<ShiftData> Shifts(params string[] names) =>
        names.Select(n => new ShiftData { Employee = n, Date = "2026-01-06", TimeRange = "9:00-5:00" })
             .ToList();

    [Fact]
    public void ExactCaseInsensitiveMatch_AutoSelects()
    {
        var result = Match(Shifts("Alice", "Bob"), "alice");
        Assert.Equal(MatchKind.ExactMatch, result.Kind);
        Assert.All(result.Shifts, s => Assert.Equal("Alice", s.Employee));
    }

    [Fact]
    public void FuzzyMatch_SingleCandidate_AutoSelects()
    {
        // "Alic" is distance 1 from "Alice"
        var result = Match(Shifts("Alice", "Bob"), "Alic");
        Assert.Equal(MatchKind.FuzzyMatch, result.Kind);
        Assert.All(result.Shifts, s => Assert.Equal("Alice", s.Employee));
    }

    [Fact]
    public void FuzzyMatch_MultipleCandidates_ReturnsAmbiguous()
    {
        // "Ali" is distance 2 from "Alice" AND distance 2 from "Alix"
        var result = Match(Shifts("Alice", "Alix", "Bob"), "Ali");
        Assert.Equal(MatchKind.Ambiguous, result.Kind);
        Assert.Contains("Alice", result.Candidates);
        Assert.Contains("Alix",  result.Candidates);
    }

    [Fact]
    public void NoMatch_ReturnsAllShifts()
    {
        var result = Match(Shifts("Alice", "Bob"), "Zara");
        Assert.Equal(MatchKind.NoMatch, result.Kind);
        Assert.Equal(2, result.Shifts.Count);
    }

    [Fact]
    public void EmptyShiftList_ReturnsNoShifts()
    {
        var result = Match([], "Alice");
        Assert.Equal(MatchKind.NoShifts, result.Kind);
        Assert.Empty(result.Shifts);
    }

    [Fact]
    public void AccentNormalization_Jose_MatchesJose()
    {
        // José (with accent) should match Jose (without accent)
        var result = Match(Shifts("José"), "Jose");
        Assert.Equal(MatchKind.ExactMatch, result.Kind);
    }
}
