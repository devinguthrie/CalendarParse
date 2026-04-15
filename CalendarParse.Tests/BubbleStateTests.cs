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

    // ── EDGE CASES & INVALID TRANSITIONS ─────────────────────────────────────

    [Fact]
    public void ConfirmTime_WhenAlreadyConfirmed_IsIdempotent()
    {
        var b = new BubbleState(Shift());
        b.ConfirmTime();
        b.ConfirmTime();
        Assert.Equal(TimeState.Confirmed, b.TimeState);
    }

    [Fact]
    public void EditTime_WhenEditing_DoesNotChangeState()
    {
        var b = new BubbleState(Shift());
        b.EditTime();
        b.EditTime();
        Assert.Equal(TimeState.Editing, b.TimeState);
    }

    [Fact]
    public void SaveTime_WhenNotEditing_DoesNothing()
    {
        var b = new BubbleState(Shift());
        b.SaveTime("should not save");
        Assert.Equal(TimeState.Pending, b.TimeState);
        Assert.Equal("9:00-5:00", b.DisplayTime);
    }

    [Fact]
    public void BeginEditPosition_WhenEditing_DoesNotChangeState()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.BeginEditPosition();
        b.BeginEditPosition();
        Assert.Equal(PositionState.Editing, b.PositionState);
    }

    [Fact]
    public void ConfirmPosition_WhenAlreadyConfirmed_IsIdempotent()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.ConfirmPosition();
        b.ConfirmPosition();
        Assert.Equal(PositionState.Confirmed, b.PositionState);
    }

    [Fact]
    public void CancelEditPosition_WhenNotEditing_DoesNothing()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.CancelEditPosition();
        Assert.Equal(PositionState.Pending, b.PositionState);
    }

    // ── CANCEL POSITION RETURNS TO PENDING TIME (Hint 3) ─────────────────────

    /// <summary>
    /// The primary Hint 3 scenario: ConfirmTime auto-advances the bubble into
    /// position editing.  Pressing Cancel must return the user all the way back
    /// to the time-pending step, not leave them stranded with
    /// TimeState=Confirmed + PositionState=Pending.
    /// </summary>
    [Fact]
    public void CancelEditPosition_AfterConfirmTime_ResetsTimeStateToPending()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();                               // → Time=Confirmed, Pos=Editing
        b.CancelEditPosition();                        // → Time=Pending,   Pos=Pending

        Assert.Equal(TimeState.Pending,     b.TimeState);
        Assert.Equal(PositionState.Pending, b.PositionState);
    }

    /// <summary>
    /// Same as above but the user explicitly called BeginEditPosition from a
    /// Pending position state (rather than relying on the ConfirmTime auto-advance).
    /// Cancel should still reset time to Pending.
    /// </summary>
    [Fact]
    public void CancelEditPosition_AfterBeginEditFromPending_ResetsTimeStateToPending()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();                               // auto-advance → Pos=Editing, _prev=Pending
        b.ConfirmPosition();                           // → Pos=Confirmed, _prev=null
        // Now reset artificially to Pending so BeginEditPosition picks it up.
        b.SetPositionState(PositionState.Pending);
        b.BeginEditPosition();                         // _prev=Pending → Pos=Editing
        b.CancelEditPosition();                        // should reset Time too

        Assert.Equal(TimeState.Pending,     b.TimeState);
        Assert.Equal(PositionState.Pending, b.PositionState);
    }

    /// <summary>
    /// Re-edit scenario: the position was already confirmed; the user goes back
    /// to tweak it.  Cancelling should restore to Confirmed (not reset time).
    /// </summary>
    [Fact]
    public void CancelEditPosition_AfterBeginEditFromConfirmed_PreservesTimeAndRestoresConfirmed()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();                               // Time=Confirmed, Pos=Editing
        b.ConfirmPosition();                           // Pos=Confirmed

        b.BeginEditPosition();                         // Pos=Editing, _prev=Confirmed
        b.CancelEditPosition();                        // should restore Pos=Confirmed, leave Time alone

        Assert.Equal(TimeState.Confirmed,      b.TimeState);
        Assert.Equal(PositionState.Confirmed,  b.PositionState);
    }

    /// <summary>
    /// After cancel from a first-time edit the bubble is not fully confirmed
    /// (both time and position are Pending).
    /// </summary>
    [Fact]
    public void CancelEditPosition_AfterConfirmTime_IsNotFullyConfirmed()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.CancelEditPosition();

        Assert.False(b.IsFullyConfirmed);
    }

    /// <summary>
    /// Full end-to-end: confirm time → cancel position → confirm time again →
    /// confirm position → IsFullyConfirmed.  The cancel didn't permanently
    /// break the state machine.
    /// </summary>
    [Fact]
    public void CancelEditPosition_ThenRecomplete_IsFullyConfirmed()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();           // Time=Confirmed, Pos=Editing
        b.CancelEditPosition();    // Time=Pending,   Pos=Pending (back to start)

        // User re-confirms everything from scratch.
        b.ConfirmTime();           // Time=Confirmed, Pos=Editing
        b.ConfirmPosition();       // Pos=Confirmed

        Assert.True(b.IsFullyConfirmed);
    }

    /// <summary>
    /// Confirm position, re-edit, cancel: must be back at Confirmed (not Pending time).
    /// Cancelling a RE-EDIT must never touch TimeState.
    /// </summary>
    [Fact]
    public void CancelReEdit_NeverTouchesTimeState()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();
        b.ConfirmPosition();                           // Pos=Confirmed, Time=Confirmed

        b.BeginEditPosition();                         // re-edit
        b.CancelEditPosition();                        // should restore Confirmed, not reset time

        Assert.Equal(TimeState.Confirmed,     b.TimeState);
        Assert.Equal(PositionState.Confirmed, b.PositionState);
        Assert.True(b.IsFullyConfirmed);
    }

    [Fact]
    public void SetPositionState_ArbitraryState_SetsCorrectly()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.SetPositionState(PositionState.Editing);
        Assert.Equal(PositionState.Editing, b.PositionState);
        b.SetPositionState(PositionState.Confirmed);
        Assert.Equal(PositionState.Confirmed, b.PositionState);
    }

    // ── SaveTime auto-transitions position (Bug 2 fix) ───────────────────────

    /// <summary>
    /// EditTime → SaveTime should behave the same as ConfirmTime when position is still
    /// Pending: the user is done with the time step so we jump straight to position edit.
    /// </summary>
    [Fact]
    public void SaveTime_PendingPosition_AutoTransitionsToEditing()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.EditTime();
        b.SaveTime("10:00-6:00");

        Assert.Equal(TimeState.Confirmed,    b.TimeState);
        Assert.Equal(PositionState.Editing,  b.PositionState);
        Assert.Equal("10:00-6:00",          b.DisplayTime);
    }

    /// <summary>
    /// SaveTime on a bubble whose position is already Confirmed must NOT reset it back to Editing.
    /// Re-editing the time label for a fully-confirmed bubble should keep the confirmed position.
    /// </summary>
    [Fact]
    public void SaveTime_ConfirmedPosition_LeavesPositionUnchanged()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.ConfirmTime();       // Pos=Editing
        b.ConfirmPosition();   // Pos=Confirmed, Time=Confirmed

        b.EditTime();
        b.SaveTime("8:00-4:00");

        Assert.Equal(TimeState.Confirmed,    b.TimeState);
        Assert.Equal(PositionState.Confirmed, b.PositionState);
    }

    /// <summary>
    /// CancelEditPosition after SaveTime-initiated Editing must reset Time back to Pending —
    /// same contract as after ConfirmTime-initiated Editing.
    /// </summary>
    [Fact]
    public void SaveTime_ThenCancelEditPosition_ResetsTimeToPending()
    {
        var b = new BubbleState(Shift(), PositionState.Pending);
        b.EditTime();
        b.SaveTime("mmm");            // Time=Confirmed, Pos=Editing
        b.CancelEditPosition();       // should reset both → Time=Pending, Pos=Pending

        Assert.Equal(TimeState.Pending,     b.TimeState);
        Assert.Equal(PositionState.Pending, b.PositionState);
    }
}
