using CalendarParse.Models;

namespace CalendarParse.Tests;

/// <summary>
/// Unit tests for ScheduleRun model and ScheduleRunSummary computed properties.
/// Pure model tests — no EF, no MAUI required.
/// DB CRUD methods (CreateRunAsync, UpdateRunProgressAsync, etc.) are EF boilerplate
/// tested by integration tests with the live app.
/// </summary>
public class ScheduleRunTests
{
    // ── ScheduleRun defaults ──────────────────────────────────────────────────

    [Fact]
    public void ScheduleRun_IsComplete_DefaultsFalse()
    {
        var run = new ScheduleRun();
        Assert.False(run.IsComplete);
    }

    [Fact]
    public void ScheduleRun_ShiftsJson_DefaultsToEmptyArray()
    {
        var run = new ScheduleRun();
        Assert.Equal("[]", run.ShiftsJson);
    }

    [Fact]
    public void ScheduleRun_ConfirmedCount_DefaultsToZero()
    {
        var run = new ScheduleRun();
        Assert.Equal(0, run.ConfirmedCount);
        Assert.Equal(0, run.TotalCount);
    }

    [Fact]
    public void ScheduleRun_DefaultStatus_IsCorrectionInProgress()
    {
        // Backward compat: existing rows without a Status column default to CorrectionInProgress
        var run = new ScheduleRun();
        Assert.Equal(RunStatus.CorrectionInProgress, run.Status);
    }

    [Fact]
    public void ScheduleRun_RemoteJobId_DefaultsNull()
    {
        var run = new ScheduleRun();
        Assert.Null(run.RemoteJobId);
    }

    // ── RunStatus enum ────────────────────────────────────────────────────────

    [Fact]
    public void RunStatus_HasAllExpectedValues()
    {
        var values = Enum.GetValues<RunStatus>();
        Assert.Contains(RunStatus.Processing,           values);
        Assert.Contains(RunStatus.CorrectionInProgress, values);
        Assert.Contains(RunStatus.Completed,            values);
        Assert.Contains(RunStatus.Error,                values);
    }

    [Fact]
    public void RunStatus_ProcessingIsZero()
    {
        // DB default for new rows with no column value must be 0=Processing or 1=CorrectionInProgress.
        // We chose CorrectionInProgress=1 as default for legacy rows; verify the int values.
        Assert.Equal(0, (int)RunStatus.Processing);
        Assert.Equal(1, (int)RunStatus.CorrectionInProgress);
        Assert.Equal(2, (int)RunStatus.Completed);
        Assert.Equal(3, (int)RunStatus.Error);
    }

    // ── ScheduleRunSummary computed properties ────────────────────────────────

    [Fact]
    public void ScheduleRunSummary_ProgressText_FormatsCorrectly()
    {
        var s = new ScheduleRunSummary
        {
            Status = RunStatus.CorrectionInProgress,
            ConfirmedCount = 3, TotalCount = 7,
        };
        Assert.Equal("3/7 confirmed", s.ProgressText);
    }

    [Fact]
    public void ScheduleRunSummary_ProgressText_ZeroShifts()
    {
        var s = new ScheduleRunSummary { Status = RunStatus.CorrectionInProgress, TotalCount = 0 };
        Assert.Equal("No shifts", s.ProgressText);
    }

    [Fact]
    public void ScheduleRunSummary_ProgressText_Processing()
    {
        var s = new ScheduleRunSummary { Status = RunStatus.Processing };
        Assert.Equal("Processing…", s.ProgressText);
    }

    [Fact]
    public void ScheduleRunSummary_ProgressText_Error()
    {
        var s = new ScheduleRunSummary { Status = RunStatus.Error };
        Assert.Equal("Error — tap to retry", s.ProgressText);
    }

    [Fact]
    public void ScheduleRunSummary_IsProcessing_TrueWhenProcessing()
    {
        var s = new ScheduleRunSummary { Status = RunStatus.Processing };
        Assert.True(s.IsProcessing);
        Assert.False(s.IsError);
    }

    [Fact]
    public void ScheduleRunSummary_IsError_TrueWhenError()
    {
        var s = new ScheduleRunSummary { Status = RunStatus.Error };
        Assert.True(s.IsError);
        Assert.False(s.IsProcessing);
    }

    [Fact]
    public void ScheduleRunSummary_HasWeekLabel_FalseWhenEmpty()
    {
        var s = new ScheduleRunSummary { WeekStart = string.Empty };
        Assert.False(s.HasWeekLabel);
        Assert.Equal(string.Empty, s.WeekLabel);
    }

    [Fact]
    public void ScheduleRunSummary_HasWeekLabel_TrueWhenSet()
    {
        var s = new ScheduleRunSummary { WeekStart = "2026-03-30" };
        Assert.True(s.HasWeekLabel);
        Assert.Equal("Week of 2026-03-30", s.WeekLabel);
    }

    [Fact]
    public void ScheduleRunSummary_DateText_DefaultWhenUnset()
    {
        var s = new ScheduleRunSummary(); // ProcessedAt == default
        Assert.Equal("—", s.DateText);
    }
}
