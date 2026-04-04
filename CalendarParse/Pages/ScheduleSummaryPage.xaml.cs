using CalendarParse.Data;
using CalendarParse.Models;
using System.Text;

namespace CalendarParse.Pages;

public partial class ScheduleSummaryPage : ContentPage
{
    private readonly ScheduleHistoryDb _db;
    private readonly List<ShiftData>   _shifts;
    private readonly int               _runId;   // -1 when called from a legacy path

    public ScheduleSummaryPage(ScheduleHistoryDb db, List<ShiftData> shifts, int runId = -1)
    {
        InitializeComponent();
        _db     = db;
        _shifts = shifts;
        _runId  = runId;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await BuildSummaryAsync();
    }

    private async Task BuildSummaryAsync()
    {
        // ── Total hours ───────────────────────────────────────────────────────
        var totalMinutes = CalculateTotalMinutes(_shifts);
        var hours        = totalMinutes / 60.0;
        TotalHoursLabel.Text  = $"{hours:F1} hrs this week";
        ShiftCountLabel.Text  = $"{_shifts.Count} shift{(_shifts.Count == 1 ? "" : "s")}";

        // ── Week-over-week diff ───────────────────────────────────────────────
        var weekStart = GetMondayOfCurrentWeek();
        var prior     = await GetPriorWeekMinutesAsync(weekStart);
        WeekDiffLabel.Text = prior.HasValue
            ? FormatDiff(totalMinutes, prior.Value)
            : "— (no prior week data)";

        // ── Persist this run ──────────────────────────────────────────────────
        // When _runId >= 0, ConfirmationPage already created and completed the run.
        // Only create a new run here for legacy callers that don't pass a runId.
        if (_runId < 0)
        {
            _db.ScheduleRuns.Add(new ScheduleRun
            {
                TotalMinutes = totalMinutes,
                WeekStart    = weekStart.ToString("yyyy-MM-dd"),
                ProcessedAt  = DateTime.UtcNow,
                IsComplete   = true,
                TotalCount   = _shifts.Count,
                ConfirmedCount = _shifts.Count,
            });
            await _db.SaveChangesWithRetryAsync();
        }

        // ── Shift list ────────────────────────────────────────────────────────
        var rows = _shifts
            .OrderBy(s => s.Date)
            .Select(s => $"{FormatDate(s.Date)}   {s.TimeRange}")
            .ToList();
        ShiftsList.ItemsSource = rows;

        // ── Conflict detection (calendar read — best-effort) ──────────────────
        await CheckConflictsAsync();
    }

    // ── Calendar integration ──────────────────────────────────────────────────

    private async void OnAddToCalendarClicked(object? sender, EventArgs e)
    {
        AddToCalendarBtn.IsEnabled = false;
        try
        {
#if ANDROID
            await AddShiftsToAndroidCalendarAsync();
#elif IOS
            await AddShiftsToIosCalendarAsync();
#else
            await DisplayAlertAsync("Not Supported",
                "Calendar integration is not available on this platform.", "OK");
#endif
        }
        finally
        {
            AddToCalendarBtn.IsEnabled = true;
        }
    }

#if ANDROID
    private async Task AddShiftsToAndroidCalendarAsync()
    {
        // Calendar permission check
        var status = await Permissions.CheckStatusAsync<Permissions.CalendarWrite>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.CalendarWrite>();

        if (status != PermissionStatus.Granted)
        {
            await DisplayAlertAsync("Permission Required",
                "Calendar write access is needed to add shifts.", "OK");
            return;
        }

        var added = 0;
        foreach (var shift in _shifts)
        {
            if (!TryParseShiftTimes(shift, out var start, out var end)) continue;
            var eventsUri = global::Android.Net.Uri.Parse("content://com.android.calendar/events");
            if (eventsUri is null) continue;

            var values = new global::Android.Content.ContentValues();
            values.Put("calendar_id",    1); // default calendar
            values.Put("title",          $"Work: {shift.TimeRange}");
            values.Put("dtstart",        ToUnixMs(start));
            values.Put("dtend",          ToUnixMs(end));
            values.Put("eventTimezone",  global::Java.Util.TimeZone.Default?.ID ?? "UTC");

            try
            {
                var uri = global::Android.App.Application.Context.ContentResolver?.Insert(
                    eventsUri,
                    values);
                if (uri is not null) added++;
            }
            catch { /* skip individual failures */ }
        }

        await DisplayAlertAsync("Done", $"{added} shift{(added == 1 ? "" : "s")} added to calendar.", "OK");
    }

    private static long ToUnixMs(DateTime dt)
        => (long)(dt.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
#endif

#if IOS
    private async Task AddShiftsToIosCalendarAsync()
    {
        var store = new EventKit.EKEventStore();
        var granted = OperatingSystem.IsIOSVersionAtLeast(17)
            ? (await store.RequestWriteOnlyAccessToEventsAsync()).Item1
            : (await store.RequestAccessAsync(EventKit.EKEntityType.Event)).Item1;
        if (!granted)
        {
            await DisplayAlertAsync("Permission Required",
                "Calendar access is needed to add shifts.", "OK");
            return;
        }

        var added = 0;
        foreach (var shift in _shifts)
        {
            if (!TryParseShiftTimes(shift, out var start, out var end)) continue;
            var ev    = EventKit.EKEvent.FromStore(store);
            ev.Title  = $"Work: {shift.TimeRange}";
            ev.StartDate = (Foundation.NSDate)start;
            ev.EndDate   = (Foundation.NSDate)end;
            ev.Calendar  = store.DefaultCalendarForNewEvents;
            if (store.SaveEvent(ev, EventKit.EKSpan.ThisEvent, out _))
                added++;
        }

        await DisplayAlertAsync("Done", $"{added} shift{(added == 1 ? "" : "s")} added to calendar.", "OK");
    }
#endif

    // ── .ics export ───────────────────────────────────────────────────────────

    private async void OnShareIcsClicked(object? sender, EventArgs e)
    {
        var ics = BuildIcs();
        var path = Path.Combine(FileSystem.CacheDirectory, "schedule.ics");
        await File.WriteAllTextAsync(path, ics);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "My Work Schedule",
            File  = new ShareFile(path, "text/calendar"),
        });
    }

    private string BuildIcs()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//CalendarParse//EN");

        foreach (var shift in _shifts)
        {
            if (!TryParseShiftTimes(shift, out var start, out var end)) continue;
            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine($"DTSTART:{start:yyyyMMddTHHmmssZ}");
            sb.AppendLine($"DTEND:{end:yyyyMMddTHHmmssZ}");
            sb.AppendLine($"SUMMARY:Work: {shift.TimeRange}");
            sb.AppendLine($"UID:{Guid.NewGuid():N}@calendarparse");
            sb.AppendLine("END:VEVENT");
        }

        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    // ── Quick-copy ────────────────────────────────────────────────────────────

    private async void OnCopyShiftClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string text })
        {
            await Clipboard.Default.SetTextAsync(text);
            // Brief visual feedback would go here (toast/snackbar — platform-specific)
        }
    }

    // ── Conflict detection ────────────────────────────────────────────────────

    private async Task CheckConflictsAsync()
    {
        // Conflict detection requires calendar read — best-effort, never blocking
        try
        {
#if ANDROID
            await CheckAndroidConflictsAsync();
#endif
        }
        catch
        {
            // Silently skip — calendar read is not critical
        }
    }

#if ANDROID
    private async Task CheckAndroidConflictsAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.CalendarRead>();
        if (status != PermissionStatus.Granted) return; // don't request — non-blocking

        var conflicts = new List<string>();
        foreach (var shift in _shifts)
        {
            if (!TryParseShiftTimes(shift, out var start, out var end)) continue;

            var startMs = ToUnixMs(start);
            var endMs   = ToUnixMs(end);

            // Query calendar for overlapping events
            var uri = global::Android.Net.Uri.Parse(
                $"content://com.android.calendar/instances/when/{startMs}/{endMs}");
            if (uri is null) continue;
            var cursor = global::Android.App.Application.Context.ContentResolver?.Query(
                uri, ["title", "begin", "end"], null, null, null);

            if (cursor is null) continue;
            while (cursor.MoveToNext())
            {
                var title = cursor.GetString(0) ?? string.Empty;
                if (!title.StartsWith("Work:", StringComparison.OrdinalIgnoreCase))
                    conflicts.Add($"⚠ {FormatDate(shift.Date)}: overlaps '{title}'");
            }
            cursor.Close();
        }

        if (conflicts.Count > 0)
        {
            ConflictsList.ItemsSource = conflicts;
            ConflictsPanel.IsVisible  = true;
        }
    }
#endif

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CalculateTotalMinutes(List<ShiftData> shifts)
    {
        var total = 0;
        foreach (var s in shifts)
        {
            if (TryParseShiftTimes(s, out var start, out var end))
                total += (int)(end - start).TotalMinutes;
        }
        return total;
    }

    private static bool TryParseShiftTimes(ShiftData shift, out DateTime start, out DateTime end)
    {
        start = end = default;
        if (!DateTime.TryParse(shift.Date, out var date)) return false;

        var parts = shift.TimeRange.Split('-');
        if (parts.Length != 2) return false;
        if (!TimeSpan.TryParse(parts[0].Trim(), out var startTime)) return false;
        if (!TimeSpan.TryParse(parts[1].Trim(), out var endTime))   return false;

        start = date.Add(startTime);
        end   = date.Add(endTime);
        if (end < start) end = end.AddDays(1); // overnight shift
        return true;
    }

    private static DateTime GetMondayOfCurrentWeek()
    {
        var today = DateTime.Today;
        var diff  = (int)today.DayOfWeek - (int)DayOfWeek.Monday;
        if (diff < 0) diff += 7;
        return today.AddDays(-diff);
    }

    private async Task<int?> GetPriorWeekMinutesAsync(DateTime currentMonday)
    {
        var priorMonday = currentMonday.AddDays(-7).ToString("yyyy-MM-dd");
        var run = await _db.ScheduleRuns
            .Where(r => r.WeekStart == priorMonday)
            .OrderByDescending(r => r.ProcessedAt)
            .FirstOrDefaultAsync();
        return run?.TotalMinutes;
    }

    private static string FormatDiff(int currentMins, int priorMins)
    {
        var diff = currentMins - priorMins;
        var sign = diff >= 0 ? "+" : "";
        return $"{sign}{diff / 60.0:F1} hrs vs last Mon–Sun";
    }

    private static string FormatDate(string iso)
    {
        return DateTime.TryParse(iso, out var dt)
            ? dt.ToString("ddd MMM d")
            : iso;
    }
}

// EF Core extension helpers for this file
file static class EfQ
{
    internal static Task<T?> FirstOrDefaultAsync<T>(
        this IQueryable<T> q, CancellationToken ct = default)
        => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(q, ct);

    internal static IQueryable<T> Where<T>(
        this Microsoft.EntityFrameworkCore.DbSet<T> s,
        System.Linq.Expressions.Expression<Func<T, bool>> pred) where T : class
        => System.Linq.Queryable.Where(s, pred);

    internal static IOrderedQueryable<T> OrderByDescending<T, TKey>(
        this IQueryable<T> s,
        System.Linq.Expressions.Expression<Func<T, TKey>> key)
        => System.Linq.Queryable.OrderByDescending(s, key);
}
