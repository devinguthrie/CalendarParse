using CalendarParse.Models;
using CalendarParse.Services;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for OverlayVisibilityLogic — the predicates that control whether
/// the drag-overlay panel is shown in ConfirmationPage.
///
/// Bug 1 (regression guard): DragOverlay must NOT be visible for time-pending
/// bubbles even when position opt-in is active. The container's grey background
/// showed in the top-right corner whenever the harness opened because the page
/// was setting IsVisible=true unconditionally for all positionOptIn bubbles.
///
/// Bug 2 is a MAUI-layer scroll issue (read stale _lastScrollX=0 instead of
/// ImageScrollView.ScrollX). That fix lives in ConfirmationPage; this file
/// documents the expected contract in a comment rather than a unit test since
/// it requires a live ScrollView.
/// </summary>
public class OverlayVisibilityTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static BubbleState PendingTimeState()
        => new(new ShiftData { Employee = "Alice", Date = "Mon", TimeRange = "9-5" },
               PositionState.Pending);

    private static BubbleState EditingTimeState()
    {
        var s = new BubbleState(new ShiftData { Employee = "Bob", Date = "Tue", TimeRange = "9-5" },
                                PositionState.Pending);
        s.EditTime();  // Pending → Editing
        return s;
    }

    private static BubbleState ConfirmedTimeState(PositionState pos = PositionState.Pending)
    {
        var s = new BubbleState(new ShiftData { Employee = "Carol", Date = "Wed", TimeRange = "9-5" }, pos);
        s.ConfirmTime();
        return s;
    }

    // ── ShouldShowDragOverlay ─────────────────────────────────────────────────

    // BUG 1 regression: overlay must stay hidden before time is confirmed.

    [Fact]
    public void ShouldShowDragOverlay_ReturnsFalse_WhenTimeStatePending()
    {
        var state = PendingTimeState();
        Assert.False(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: true, state));
    }

    [Fact]
    public void ShouldShowDragOverlay_ReturnsFalse_WhenTimeStateEditing()
    {
        var state = EditingTimeState();
        Assert.False(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: true, state));
    }

    [Fact]
    public void ShouldShowDragOverlay_ReturnsTrue_WhenTimeConfirmed_PositionPending()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        Assert.True(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: true, state));
    }

    [Fact]
    public void ShouldShowDragOverlay_ReturnsTrue_WhenTimeConfirmed_PositionEditing()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        state.BeginEditPosition();  // Pending → Editing
        Assert.True(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: true, state));
    }

    [Fact]
    public void ShouldShowDragOverlay_ReturnsTrue_WhenFullyConfirmed()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        state.ConfirmPosition();  // time + position confirmed
        Assert.True(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: true, state));
    }

    [Fact]
    public void ShouldShowDragOverlay_ReturnsFalse_WhenNotPositionOptIn_EvenIfTimeConfirmed()
    {
        var state = ConfirmedTimeState();
        Assert.False(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: false, state));
    }

    [Fact]
    public void ShouldShowDragOverlay_ReturnsFalse_WhenPositionSkipped_AndTimeConfirmed_AndOptInFalse()
    {
        var state = ConfirmedTimeState(PositionState.Skipped);
        Assert.False(OverlayVisibilityLogic.ShouldShowDragOverlay(positionOptIn: false, state));
    }

    // ── IsPositionReviewOrEditStep ────────────────────────────────────────────

    [Fact]
    public void IsPositionReviewOrEditStep_ReturnsFalse_WhenTimeNotConfirmed()
    {
        var state = PendingTimeState();
        Assert.False(OverlayVisibilityLogic.IsPositionReviewOrEditStep(positionOptIn: true, state));
    }

    [Fact]
    public void IsPositionReviewOrEditStep_ReturnsTrue_WhenTimeConfirmed_PositionPending()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        Assert.True(OverlayVisibilityLogic.IsPositionReviewOrEditStep(positionOptIn: true, state));
    }

    [Fact]
    public void IsPositionReviewOrEditStep_ReturnsTrue_WhenTimeConfirmed_PositionEditing()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        state.BeginEditPosition();
        Assert.True(OverlayVisibilityLogic.IsPositionReviewOrEditStep(positionOptIn: true, state));
    }

    [Fact]
    public void IsPositionReviewOrEditStep_ReturnsFalse_WhenPositionAlreadyConfirmed()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        state.ConfirmPosition();
        Assert.False(OverlayVisibilityLogic.IsPositionReviewOrEditStep(positionOptIn: true, state));
    }

    [Fact]
    public void IsPositionReviewOrEditStep_ReturnsFalse_WhenPositionSkipped()
    {
        var state = ConfirmedTimeState(PositionState.Skipped);
        Assert.False(OverlayVisibilityLogic.IsPositionReviewOrEditStep(positionOptIn: true, state));
    }

    [Fact]
    public void IsPositionReviewOrEditStep_ReturnsFalse_WhenNotPositionOptIn()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        Assert.False(OverlayVisibilityLogic.IsPositionReviewOrEditStep(positionOptIn: false, state));
    }

    // ── ShouldShowSelectionBorder (Bug 4 regression) ─────────────────────────
    //
    // When the user taps "Edit Position", PositionState enters Editing.
    // At that point both posReviewStep and posEditing are true.
    // The confirmation border (SelectedRectBorder) must be hidden so only
    // the grey drag rect is visible.

    [Fact]
    public void ShouldShowSelectionBorder_HidesBorder_WhenPositionEditing()
    {
        // posEditing=true, posReviewStep=true (Editing is included in review step)
        Assert.False(OverlayVisibilityLogic.ShouldShowSelectionBorder(
            editable: false, posConfirmed: false, posReviewStep: true, posEditing: true));
    }

    [Fact]
    public void ShouldShowSelectionBorder_ShowsBorder_WhenReviewStep_NotEditing()
    {
        // posReviewStep via Pending (not yet dragging)
        Assert.True(OverlayVisibilityLogic.ShouldShowSelectionBorder(
            editable: false, posConfirmed: false, posReviewStep: true, posEditing: false));
    }

    [Fact]
    public void ShouldShowSelectionBorder_ShowsBorder_WhenEditable()
    {
        // Both locks engaged — actively resizing the drag rect
        Assert.True(OverlayVisibilityLogic.ShouldShowSelectionBorder(
            editable: true, posConfirmed: false, posReviewStep: true, posEditing: true));
    }

    [Fact]
    public void ShouldShowSelectionBorder_ShowsBorder_WhenPositionConfirmed()
    {
        Assert.True(OverlayVisibilityLogic.ShouldShowSelectionBorder(
            editable: false, posConfirmed: true, posReviewStep: false, posEditing: false));
    }

    [Fact]
    public void ShouldShowSelectionBorder_HidesBorder_WhenNothingActive()
    {
        Assert.False(OverlayVisibilityLogic.ShouldShowSelectionBorder(
            editable: false, posConfirmed: false, posReviewStep: false, posEditing: false));
    }

    // ── ShouldDrawCanvasBorder ────────────────────────────────────────────────
    //
    // With positionOptIn = true:
    //   Confirmed-position bubbles always show (green done marker).
    //   Selected bubble on the time-confirm step shows (gold/red, indicates active day).
    //   Selected bubble that is time-confirmed but position-pending/editing → NO border
    //     (PositionTargetRect is the active-day indicator during the position step).
    //
    // With positionOptIn = false (time-only flow):
    //   Selected or TimeConfirmed → border; others → no border.

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsFalse_ForNonSelectedPendingBubble_WhenOptIn()
    {
        var state = PendingTimeState();   // TimeState.Pending, PositionState.Pending
        Assert.False(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: true, state, isSelected: false));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsTrue_ForSelectedBubble_WhenOptIn_DuringTimeStep()
    {
        // Selected + time-pending = still on the time-confirm step → canvas border shown.
        var state = PendingTimeState();  // PositionState.Pending, TimeState.Pending
        Assert.True(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: true, state, isSelected: true));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsFalse_ForSelectedBubble_WhenOptIn_TimeConfirmedPositionPending()
    {
        // Time done, position pending: PositionTargetRect is the active-day visual → no canvas border.
        var state = ConfirmedTimeState(PositionState.Pending);
        Assert.False(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: true, state, isSelected: true));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsFalse_ForSelectedBubble_WhenOptIn_AndPositionEditing()
    {
        // Bug 4 regression guard: selected bubble in position-edit mode must NOT show canvas border.
        var state = ConfirmedTimeState(PositionState.Pending);
        state.BeginEditPosition(); // PositionState → Editing
        Assert.False(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: true, state, isSelected: true));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsTrue_ForConfirmedPositionBubble_WhenOptIn()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        state.ConfirmPosition(); // PositionState → Confirmed
        Assert.True(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: true, state, isSelected: false));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsTrue_WhenOptInFalse_Selected()
    {
        // Without position opt-in, the selected bubble always shows a border.
        var state = PendingTimeState();
        Assert.True(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: false, state, isSelected: true));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsTrue_WhenOptInFalse_TimeConfirmedNonSelected()
    {
        // Non-selected but time-confirmed → shows green border.
        var state = PendingTimeState();
        state.ConfirmTime();
        Assert.True(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: false, state, isSelected: false));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsFalse_WhenOptInFalse_NonSelectedPending()
    {
        // Server-provided bounds must not produce a canvas border for unselected unconfirmed bubbles.
        var state = PendingTimeState();
        Assert.False(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: false, state, isSelected: false));
    }

    [Fact]
    public void ShouldDrawCanvasBorder_ReturnsFalse_ForEditingPositionBubble_WhenOptIn_NotSelected()
    {
        var state = ConfirmedTimeState(PositionState.Pending);
        state.BeginEditPosition(); // PositionState → Editing
        Assert.False(OverlayVisibilityLogic.ShouldDrawCanvasBorder(
            positionOptIn: true, state, isSelected: false));
    }
}
