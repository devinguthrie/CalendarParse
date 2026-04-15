using CalendarParse.Models;

namespace CalendarParse.Services;

/// <summary>
/// Pure, MAUI-free predicates that decide when the position-editing overlay
/// controls should be shown inside ConfirmationPage.
///
/// Extracted from the page code-behind so the rules can be unit-tested
/// without instantiating a MAUI page.
/// </summary>
public static class OverlayVisibilityLogic
{
    /// <summary>
    /// Returns true when the drag-overlay panel (the container that hosts the
    /// drag rect, corner handles, lock-pan button, etc.) should be visible.
    ///
    /// Rule: the overlay exists to let the user confirm/edit a position.
    /// That step only starts after the user has confirmed the time for this
    /// bubble — therefore the panel must remain hidden while TimeState is
    /// Pending or Editing, even when position opt-in is active.
    /// </summary>
    public static bool ShouldShowDragOverlay(bool positionOptIn, BubbleState state)
        => positionOptIn && state.TimeState == TimeState.Confirmed;

    /// <summary>
    /// Returns true when the position-step review overlay border (the SelectedRectBorder
    /// XAML element, coloured gold/blue/green) should be visible.
    ///
    /// Rule: hide the border while the user is actively dragging the position rect
    /// (PositionState.Editing) — only the grey drag rect should be visible then.
    /// The border is shown again once the user confirms the position or when both
    /// locks are engaged (editable = true, i.e. actively resizing).
    /// </summary>
    public static bool ShouldShowSelectionBorder(
        bool editable, bool posConfirmed, bool posReviewStep, bool posEditing)
        => editable
        || posConfirmed
        || (posReviewStep && !posEditing);

    /// <summary>
    /// Returns true when the canvas should draw a border rectangle around a bubble.
    ///
    /// With position opt-in active:
    ///   - PositionState.Confirmed (any bubble)         → green border (done signal)
    ///   - Selected bubble still on time-confirm step  → gold/red border (shows active day)
    ///   - Anything else (selected time-confirmed, non-selected non-confirmed) → NO border
    ///     The fixed PositionTargetRect is the active-day indicator during the position step.
    ///
    /// Without position opt-in (time-confirm-only flow):
    ///   - Selected bubble → border (shows which day is active)
    ///   - Any TimeState.Confirmed bubble → green border
    ///   - Non-selected, non-confirmed → NO border (server bounds don’t trigger visible borders)
    /// </summary>
    public static bool ShouldDrawCanvasBorder(bool positionOptIn, BubbleState state, bool isSelected)
    {
        if (!positionOptIn)
            return isSelected || state.TimeState == TimeState.Confirmed;

        // Position flow: confirmed position always shows (green confirmed marker).
        if (state.PositionState == PositionState.Confirmed) return true;

        // PositionTargetRect is the visual indicator across ALL steps of the opt-in flow
        // (both time-confirm and location-review). Never draw a canvas border here —
        // doing so would produce two simultaneous overlays on the same bubble.
        return false;
    }

    /// <summary>
    /// Returns true when the bubble is at the interactive position review
    /// step — i.e. time is confirmed and the position still needs the user's
    /// attention (Pending = not yet placed; Editing = currently being dragged).
    /// </summary>
    public static bool IsPositionReviewOrEditStep(bool positionOptIn, BubbleState state)
        => positionOptIn
        && state.TimeState == TimeState.Confirmed
        && state.PositionState is PositionState.Pending or PositionState.Editing;
}
