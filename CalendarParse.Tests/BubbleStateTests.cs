using CalendarParse.Models;

namespace CalendarParse.Tests;

public class BubbleStateTests
{
    private static ShiftData Shift(string time = "9:00-5:00") => new()
    {
        Employee  = "Alice",
        Date      = "2026-01-06",
        TimeRange = time,
    };

    // ── TIME transitions ──────────────────────────────────────────────────────

    [Fact]
    public void Pending_ConfirmTime_BecomesConfirmed()
    {
        var b = new BubbleState(Shift());
        b.ConfirmTime();
        Assert.Equal(TimeState.Confirmed, b.TimeState);
    }

    [Fact]
    public void Pending_EditTime_BecomesEditing()
    {
        var b = new BubbleState(Shift());
        b.EditTime();
        Assert.Equal(TimeState.Editing, b.TimeState);
    }

    [Fact]
    public void Editing_SaveTime_BecomesConfirmedWithNewText()
    {
        var b = new BubbleState(Shift("9:00-5:00"));
        b.EditTime();
        b.SaveTime("10:00-6:00");
        Assert.Equal(TimeState.Confirmed, b.TimeState);
        Assert.Equal("10:00-6:00", b.DisplayTime);
    }

    [Fact]
    public void Editing_DismissEdit_ReturnsToPending()
    {
        var b = new BubbleState(Shift());
        b.EditTime();
        b.DismissEdit();
        Assert.Equal(TimeState.Pending, b.TimeState);
    }

    [Fact]
    public void Confirmed_EditTime_BecomesEditing()
    {
        var b = new BubbleState(Shift());
        b.ConfirmTime();
        b.EditTime();
        Assert.Equal(TimeState.Editing, b.TimeState);
    }

    [Fact]
    public void SaveTime_TrimsWhitespace()
    {
        var b = new BubbleState(Shift());
        b.EditTime();
        b.SaveTime("  9:00-5:00  ");
        Assert.Equal("9:00-5:00", b.DisplayTime);
    }

    [Fact]
    public void DismissEdit_WhenNotEditing_IsNoOp()
    {
        var b = new BubbleState(Shift());
        b.DismissEdit(); // already Pending — should stay Pending
        Assert.Equal(TimeState.Pending, b.TimeState);
    }

    // ── POSITION opt-in transitions ───────────────────────────────────────────

    [Fact]
    public void PositionOptIn_Yes_SetsPositionPending()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        Assert.Equal(PositionState.Pending, b.PositionState);
    }

    [Fact]
    public void PositionOptIn_Never_SetsPositionSkipped()
    {
        var b = new BubbleState(Shift(), PositionState.Skipped);
        Assert.Equal(PositionState.Skipped, b.PositionState);
    }

    [Fact]
    public void ConfirmPosition_WhenPending_BecomesConfirmed()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.ConfirmPosition();
        Assert.Equal(PositionState.Confirmed, b.PositionState);
    }

    [Fact]
    public void BeginEditPosition_FromPending_BecomesEditing()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.BeginEditPosition();
        Assert.Equal(PositionState.Editing, b.PositionState);
    }

    [Fact]
    public void BeginEditPosition_FromConfirmed_BecomesEditing()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.ConfirmPosition();
        b.BeginEditPosition();
        Assert.Equal(PositionState.Editing, b.PositionState);
    }

    [Fact]
    public void ConfirmPosition_WhenEditing_BecomesConfirmed()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.BeginEditPosition();
        b.ConfirmPosition();
        Assert.Equal(PositionState.Confirmed, b.PositionState);
    }

    [Fact]
    public void ConfirmPosition_WhenSkipped_IsNoOp()
    {
        var b = new BubbleState(Shift(), PositionState.Skipped);
        b.ConfirmPosition(); // should have no effect
        Assert.Equal(PositionState.Skipped, b.PositionState);
    }

    [Fact]
    public void CancelEditPosition_WhenEditing_BecomesPending()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.BeginEditPosition();
        b.CancelEditPosition();
        Assert.Equal(PositionState.Pending, b.PositionState);
    }

    // ── FULLY_CONFIRMED ───────────────────────────────────────────────────────

    [Fact]
    public void IsFullyConfirmed_True_WhenTimeConfirmedAndPositionSkipped()
    {
        var b = new BubbleState(Shift(), PositionState.Skipped);
        b.ConfirmTime();
        Assert.True(b.IsFullyConfirmed);
    }

    [Fact]
    public void IsFullyConfirmed_True_WhenTimeConfirmedAndPositionConfirmed()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.ConfirmPosition();
        Assert.True(b.IsFullyConfirmed);
    }

    [Fact]
    public void IsFullyConfirmed_False_WhenTimePending()
    {
        var b = new BubbleState(Shift(), PositionState.Skipped);
        Assert.False(b.IsFullyConfirmed);
    }

    [Fact]
    public void IsFullyConfirmed_False_WhenTimeConfirmedButPositionPending()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        Assert.False(b.IsFullyConfirmed); // position still needs confirming
    }
}
