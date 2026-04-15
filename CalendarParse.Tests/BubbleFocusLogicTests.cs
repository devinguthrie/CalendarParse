using CalendarParse.Core.Services;
using CalendarParse.Models;
using Xunit;

namespace CalendarParse.Tests;

/// <summary>
/// Tests for BubbleFocusLogic.FindNextUnconfirmedIndex — the logic that decides
/// which bubble to activate after the user confirms or edits a day.
///
/// Bug areas:
///   • After confirming the last unconfirmed bubble the focus should not jump
///     to an already-confirmed bubble.
///   • Going back and editing a prior confirmed bubble must not skip unconfirmed
///     bubbles that come after it in the list.
///   • Wrapping must return to the very first unconfirmed bubble, not loop
///     indefinitely on the current one.
/// </summary>
public class BubbleFocusLogicTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static BubbleState Pending()
        => new(new ShiftData { Employee = "A", Date = "Mon", TimeRange = "9-5" },
               PositionState.Skipped);

    private static BubbleState Confirmed()
    {
        var s = Pending();
        s.ConfirmTime();
        return s;
    }

    // ── Edge cases: empty / single ────────────────────────────────────────────

    [Fact]
    public void EmptyList_ReturnsMinusOne()
        => Assert.Equal(-1, BubbleFocusLogic.FindNextUnconfirmedIndex([], null));

    [Fact]
    public void SinglePending_WithNullCurrent_ReturnsSelf()
    {
        var s = Pending();
        // No current → scan from start → first unconfirmed is index 0
        Assert.Equal(0, BubbleFocusLogic.FindNextUnconfirmedIndex([s], null));
    }

    [Fact]
    public void SinglePending_WhenCurrentIsSelf_ReturnsMinusOne()
    {
        // After confirming the only bubble, there is nothing else to focus.
        var s = Pending();
        Assert.Equal(-1, BubbleFocusLogic.FindNextUnconfirmedIndex([s], s));
    }

    [Fact]
    public void SingleConfirmed_ReturnsMinusOne()
    {
        var s = Confirmed();
        Assert.Equal(-1, BubbleFocusLogic.FindNextUnconfirmedIndex([s], null));
    }

    // ── Sequential advancement ────────────────────────────────────────────────

    [Fact]
    public void TwoPending_AdvancesForwardFromCurrent()
    {
        var s0 = Pending();
        var s1 = Pending();
        var list = new[] { s0, s1 };

        // Current is index 0 → next unconfirmed is index 1
        Assert.Equal(1, BubbleFocusLogic.FindNextUnconfirmedIndex(list, s0));
    }

    [Fact]
    public void ThreePending_CurrentAtMiddle_AdvancesToNext()
    {
        var s0 = Pending();
        var s1 = Pending();
        var s2 = Pending();
        Assert.Equal(2, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2], s1));
    }

    [Fact]
    public void LastIsPending_SkipAlreadyConfirmedAhead()
    {
        var s0 = Confirmed();
        var s1 = Confirmed();
        var s2 = Pending();  // ← last in list, not yet confirmed
        // Current is s1; next forward is s2
        Assert.Equal(2, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2], s1));
    }

    // ── Wrapping behaviour ────────────────────────────────────────────────────

    [Fact]
    public void WrapAround_WhenNothingAheadIsUnconfirmed()
    {
        var s0 = Pending();   // ← only unconfirmed, before current
        var s1 = Confirmed();
        var s2 = Confirmed();
        // current = s2: nothing unconfirmed ahead → wrap → s0 at index 0
        Assert.Equal(0, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2], s2));
    }

    [Fact]
    public void WrapAround_SkipsConfirmedBubblesAtStart()
    {
        var s0 = Confirmed();
        var s1 = Pending();   // ← only unconfirmed
        var s2 = Confirmed();
        var s3 = Confirmed();
        // current = s3: wrap → index 1
        Assert.Equal(1, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2, s3], s3));
    }

    [Fact]
    public void AllConfirmed_ReturnsMinusOne()
    {
        var states = new[] { Confirmed(), Confirmed(), Confirmed() };
        Assert.Equal(-1, BubbleFocusLogic.FindNextUnconfirmedIndex(states, states[1]));
    }

    // ── Back-navigation: editing a prior confirmed bubble ─────────────────────

    [Fact]
    public void BackToFirstBubble_NextIsSecondUnconfirmed()
    {
        // Scenario: user goes back to bubble 0 after bubbles 1 and 2 are still pending.
        var s0 = Confirmed();
        var s1 = Pending();
        var s2 = Pending();
        // current = s0 → next unconfirmed forward = s1 at index 1
        Assert.Equal(1, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2], s0));
    }

    [Fact]
    public void BackToMidBubble_NextIsNextUnconfirmedAhead()
    {
        // Scenario: s0 confirmed, s1 re-opened for editing, s2 still pending
        var s0 = Confirmed();
        var s1 = Pending();   // re-opened
        var s2 = Pending();
        // current = s1 → next forward = s2 at index 2
        Assert.Equal(2, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2], s1));
    }

    [Fact]
    public void BackToMidBubble_WhenNothingAheadIsUnconfirmed_WrapsToEarlierUnconfirmed()
    {
        // Scenario: s1 was editing when reselected; only s0 is unconfirmed (earlier in list)
        var s0 = Pending();   // earlier, unconfirmed
        var s1 = Pending();   // current (being edited / re-confirmed)
        var s2 = Confirmed();
        var s3 = Confirmed();
        // current = s1; nothing unconfirmed after index 1 → wrap → index 0 (s0)
        Assert.Equal(0, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1, s2, s3], s1));
    }

    // ── Unknown current (not in list) ─────────────────────────────────────────

    [Fact]
    public void CurrentNotInList_TreatsAsNullAndStartsFromBeginning()
    {
        var s0 = Pending();
        var s1 = Pending();
        var outsider = Pending(); // not in the list

        // Should fall back to scanning from index 0 (currentIdx == -1)
        Assert.Equal(0, BubbleFocusLogic.FindNextUnconfirmedIndex([s0, s1], outsider));
    }

    // ── Large list performance smoke-test ─────────────────────────────────────

    [Fact]
    public void LargeList_AllPending_AdvancesForwardLinear()
    {
        var states = Enumerable.Range(0, 100).Select(_ => Pending()).ToList();
        for (int i = 0; i < 99; i++)
            Assert.Equal(i + 1, BubbleFocusLogic.FindNextUnconfirmedIndex(states, states[i]));
    }
}
