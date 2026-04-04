namespace CalendarParse.Models;

public enum TimeState     { Pending, Editing, Confirmed }
public enum PositionState
{
    Pending = 0,
    Confirmed = 1,
    Skipped = 2,
    Editing = 3,
}

/// <summary>
/// Pure state machine for one confirmation bubble.
/// No MAUI dependencies — fully testable from CalendarParse.Tests.
///
/// State diagram:
///
///   TIME PHASE
///   ──────────────────────────────────────────────────
///   Pending  → ConfirmTime  → Confirmed  (border green)
///   Pending  → EditTime     → Editing    (border red)
///   Editing  → SaveTime     → Confirmed
///   Editing  → DismissEdit  → Pending
///   Confirmed→ EditTime     → Editing
///
///   POSITION PHASE
///   ──────────────────────────────────────────────────
///   Pending  → BeginEditPosition → Editing
///   Editing  → ConfirmPosition   → Confirmed
///   Confirmed→ BeginEditPosition → Editing
///
///   FULLY_CONFIRMED = TimeState==Confirmed AND PositionState∈{Confirmed,Skipped}
/// </summary>
public class BubbleState
{
    public ShiftData    Shift         { get; init; } = null!;
    public TimeState    TimeState     { get; private set; } = TimeState.Pending;
    public PositionState PositionState { get; private set; } = PositionState.Skipped;

    /// <summary>Corrected time value — may be edited by the user.</summary>
    public string       DisplayTime   { get; private set; } = string.Empty;

    public BubbleState(ShiftData shift, PositionState initialPositionState = PositionState.Skipped)
    {
        Shift         = shift;
        DisplayTime   = shift.TimeRange;
        PositionState = initialPositionState;
    }

    public bool IsFullyConfirmed =>
        TimeState == TimeState.Confirmed
        && (PositionState is PositionState.Confirmed or PositionState.Skipped);

    // ── Time transitions ──────────────────────────────────────────────────────

    /// <summary>Thumbs up: accept the displayed time.</summary>
    public void ConfirmTime()
    {
        if (TimeState is TimeState.Pending or TimeState.Editing)
            TimeState = TimeState.Confirmed;
    }

    /// <summary>Thumbs down / edit icon: open the text editor.</summary>
    public void EditTime()
    {
        if (TimeState is TimeState.Pending or TimeState.Confirmed)
            TimeState = TimeState.Editing;
    }

    /// <summary>Save button: accept the edited text and confirm.</summary>
    public void SaveTime(string editedText)
    {
        if (TimeState != TimeState.Editing) return;
        DisplayTime = editedText.Trim();
        TimeState   = TimeState.Confirmed;
    }

    /// <summary>Dismiss button: cancel editing, return to Pending.</summary>
    public void DismissEdit()
    {
        if (TimeState == TimeState.Editing)
            TimeState = TimeState.Pending;
    }

    // ── Position transitions ──────────────────────────────────────────────────

    /// <summary>Enter interactive position edit mode.</summary>
    public void BeginEditPosition()
    {
        if (PositionState is PositionState.Pending or PositionState.Confirmed)
            PositionState = PositionState.Editing;
    }

    /// <summary>User dragged the bubble to the correct position and tapped thumbs up.</summary>
    public void ConfirmPosition()
    {
        if (PositionState is PositionState.Pending or PositionState.Editing)
            PositionState = PositionState.Confirmed;
    }

    /// <summary>Exit position edit mode without confirming.</summary>
    public void CancelEditPosition()
    {
        if (PositionState == PositionState.Editing)
            PositionState = PositionState.Pending;
    }

    /// <summary>Sets position state directly — used when applying the opt-in preference.</summary>
    public void SetPositionState(PositionState state) => PositionState = state;
}
