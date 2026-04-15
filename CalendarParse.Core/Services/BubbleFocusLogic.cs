using CalendarParse.Models;

namespace CalendarParse.Core.Services;

/// <summary>
/// Pure logic for finding the next bubble to focus after a confirmation action.
/// Extracted from ConfirmationPage so the advancement and back-navigation logic
/// can be unit-tested without a MAUI page.
/// </summary>
public static class BubbleFocusLogic
{
    /// <summary>
    /// Finds the next unconfirmed bubble after <paramref name="currentState"/> in
    /// <paramref name="states"/>.  Searches forward first; wraps to the beginning
    /// if no unconfirmed bubble is found past the current position.
    ///
    /// Returns <c>-1</c> when all bubbles are fully confirmed.
    /// Returns <c>-1</c> when the list is empty.
    /// </summary>
    public static int FindNextUnconfirmedIndex(
        IReadOnlyList<BubbleState> states,
        BubbleState?               currentState)
    {
        if (states.Count == 0) return -1;

        var currentIdx = currentState is null ? -1 : IndexOf(states, currentState);

        // Search forward from the bubble AFTER current.
        for (int i = currentIdx + 1; i < states.Count; i++)
            if (!states[i].IsFullyConfirmed) return i;

        // Wrap: search from the beginning up to (but not including) current.
        for (int i = 0; i < currentIdx; i++)
            if (!states[i].IsFullyConfirmed) return i;

        return -1; // all confirmed (or current itself is the only unconfirmed)
    }

    private static int IndexOf(IReadOnlyList<BubbleState> states, BubbleState target)
    {
        for (int i = 0; i < states.Count; i++)
            if (ReferenceEquals(states[i], target)) return i;
        return -1;
    }
}
