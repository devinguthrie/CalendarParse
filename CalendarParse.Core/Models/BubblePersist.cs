namespace CalendarParse.Core.Models;

/// <summary>
/// Serialization DTO for one bubble's persisted state.
/// Written to ScheduleRun.ShiftsJson after each confirmation action
/// so the session can be resumed exactly where it was left off.
/// </summary>
public record BubblePersist(
    string Employee,
    string Date,
    string OriginalTimeRange,
    string DisplayTime,
    int    TimeState,       // 0=Pending 1=Editing 2=Confirmed
    int    PositionState,   // 0=Pending 1=Confirmed 2=Skipped 3=Editing
    int?   BoundsX,
    int?   BoundsY,
    int?   BoundsWidth,
    int?   BoundsHeight);
