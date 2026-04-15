# CalendarParse — Deferred Work

Items deferred from the mobile app v1 plan. All require no breaking changes to ship.

---

## ✅ zoom-on-time-step — DONE

**What:** When position opt-in is active, the zoom slider is now shown on the **pending
time step** (before time is confirmed), not just on the position step. This lets the user
zoom in to read the schedule image while deciding whether the parsed time is correct.

**Fix:**
- `ConfirmationPage.xaml.cs` (`OnBubbleTapped`): In the `_positionOptIn` branch, removed
  the unconditional `ZoomControlsPanel.IsVisible = false`. In the `else` arm (i.e. bubble
  is not in `PositionState.Editing`), explicitly show `ZoomControlsPanel`, `ZoomSliderWrapper`,
  and `ZoomSlider` before calling `UpdateOverlayEditVisualState()`.
- `OverlayVisibilityLogic.cs` (`ShouldDrawCanvasBorder`): Changed the final `return` from
  `isSelected && state.TimeState != TimeState.Confirmed` to `return false`, so the static
  gold canvas border is suppressed on the time step when position opt-in is active (the
  zoom slider UI is the active-bubble indicator instead).
- `OverlayVisibilityTests.cs`: Renamed
  `ShouldDrawCanvasBorder_ReturnsTrue_ForSelectedBubble_WhenOptIn_DuringTimeStep` →
  `ShouldDrawCanvasBorder_ReturnsFalse_ForSelectedBubble_WhenOptIn_DuringTimeStep` and
  flipped its assertion from `Assert.True` to `Assert.False`.

---

## ✅ remove-emojis — DONE

**What:** All emoji characters (`📍`, `✏️`, `👍`, `◀`, `▶`, `⚠`) removed from button
text, label strings, and code-behind string literals across the MAUI app.

**Fix:**
- `ConfirmationPage.xaml.cs`: Stripped emoji prefixes from `ThumbsDownBtn.Text` and
  `ThumbsUpBtn.Text` assignments — e.g. `"📍 Edit Position"` → `"Edit Position"`.
- `ConfirmationPage.xaml`: Replaced `Text="◀"` / `Text="▶"` on nav buttons with
  `mi:MauiIcon.Icon="{mi:Material ChevronLeft}"` / `ChevronRight` attached properties
  (file already had `xmlns:mi`).
- `HistoryPage.xaml`: Added `xmlns:mi` namespace declaration; replaced
  `<Label Text="⚠" TextColor="OrangeRed">` with
  `<mi:MauiIcon Icon="{mi:Material Warning}" IconColor="OrangeRed" IconSize="20">`.
- `ScheduleSummaryPage.xaml.cs`: Removed `⚠ ` prefix from the conflict string literal.

---

## ✅ fix-processing-dialog — DONE

**What:** Removed the blocking `DisplayAlertAsync` popup in `MainPage.SubmitPhotoAsync`
that forced users to tap OK before any network work started.

**Fix:**
- `MainPage.xaml.cs` (`SubmitPhotoAsync`): Deleted the `await DisplayAlertAsync(…)` call
  (and its accompanying comment) that preceded the UI state changes.
- `MainPage.xaml.cs` (`SubmitPhotoAsync`): Changed the success status label text from
  `"Submitted!"` to `"Submitted! Processing takes ~2 min."` so the timing expectation is
  communicated inline via `StatusLabel` without a modal popup.

---

## ✅ rework-bubble-detail-panel — DONE

**What:** Cleaned up the `BubbleDetailPanel` card: removed the redundant employee-name
label and combined the date and time into a single row.

**Fix:**
- `ConfirmationPage.xaml`: Removed `<Label x:Name="BubbleEmployeeLabel" …>`. Replaced
  the separate `BubbleDateLabel` and `BubbleTimeLabel` stacked labels with a two-column
  `<Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">` so date (bold, left) and time
  (right) sit on the same line. Added missing `xmlns:mi` namespace declaration.
- `ConfirmationPage.xaml.cs`: Removed all four `BubbleEmployeeLabel` references —
  `IsVisible = true` in `SelectBubble`, `Text = bubble.Shift.Employee` in `SelectBubble`,
  `IsVisible = false` in `EnterPositionEditMode`, and `IsVisible = true` in
  `ExitPositionEditMode`.

---

## ✅ submit-icon-right — DONE

**What:** Added a Material Send icon to the `ProcessScheduleBtn` button using MauiIcons.

**Fix:**
- `CalendarParse.csproj`: Added `<PackageReference Include="AathifMahir.Maui.MauiIcons.Material" Version="6.0.0" />`.
- `MauiProgram.cs`: Added `using MauiIcons.Material;` and chained `.UseMaterialMauiIcons()`
  into the builder fluent call so MauiIcons font assets are registered at startup.
- `ConfirmationPage.xaml`: Added `mi:MauiIcon.Value="{mi:Material Icon=Send}"` to
  `ProcessScheduleBtn`. (`mi:MauiIcon.ContentSide` is not a property exposed by
  MauiIcons.Core v6; icon position is controlled by the font glyph rendering pipeline.)

---

## 1. mDNS / QR-code server discovery

**What:** Eliminate manual IP:PORT entry in Settings. Phone discovers the PC server
automatically over LAN via mDNS, or by scanning a QR code the server prints on startup.

**How:** Implement a second `IServerDiscovery` (e.g. `MdnsDiscovery`) and register it
alongside `ManualIpDiscovery`. The server adds a Bonjour/Avahi advertisement; the app
resolves `_calendarparse._tcp.local.` or falls back to manual.

**Effort:** ~1 day (human) / ~15 min (CC)

---

## 2. Cloud API migration

**What:** Move `CalendarParse.Api` from a Windows PC to a cloud host so users don't need
a PC running at home.

**How:** No client changes needed — swap base URL in `IServerDiscovery`. The server needs
to work without WinRT (replace `WindowsWinRtOcrService` with a cross-platform OCR lib, or
run on a Windows VM). `CalendarParse.Api.csproj` TFM changes from `net10.0-windows10.0.19041.0`
to `net10.0`.

**Effort:** ~2–3 days (human) / depends on OCR lib choice

---

## 3. User Auth System and profile support

**What:** Support multiple employee profiles per server, so coworkers can share a server and view each other's schedules.
**How:**  Add a `Profile` entity to
`ScheduleHistoryDb` (Id, Name, ServerKey). Settings page becomes profile switcher.
Requires auth to prevent one user from seeing another's history.
 
**Blocker:** Auth / user identity system doesn't exist yet.

---

## 4. API shared secret rotation

**What:** Re-key the `X-CalendarParse-Key` without restarting the server.

**How:** Add `POST /rotate-key` endpoint (protected by the current key). Server generates
a new key, writes it to `appsettings.json`, and returns it. App updates `ServerKey` in
`AppPreferences` and saves.

**Effort:** ~2 hours (human) / ~10 min (CC)

---

## 5. Deeper NotificationListenerService image extraction

**What:** Pull the schedule image bytes directly from the notification large icon or
notification extras — skipping the share-sheet step entirely.

**How:** In `AndroidNotificationMonitor.OnNotificationPosted`, check
`notification.Extras` for `android.picture` (a `Bitmap`). If the bitmap is large enough
(e.g. > 200×200px), encode it to JPEG and pass directly to `ConfirmationPage.StartWithImageAsync`.

**Caveat:** Most messaging apps don't put the full image in notification extras (they use
a thumbnail). This is best-effort — fall back to share sheet when the bitmap is absent or
too small.

**Effort:** ~4 hours (human) / ~15 min (CC)

---

## 6. Subscribe to another user's calendar

**What:** One employee subscribes to a coworker's calendar entries so they can see
each other's shifts (e.g. for shift-swapping).

**How:** Requires a cloud endpoint that stores confirmed shifts by user, plus auth so
only authorized users can read each other's data. Out of scope until auth exists.

**Blocker:** Auth + cloud API migration (items 2 & 3).
