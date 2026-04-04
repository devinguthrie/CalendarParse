# CalendarParse — Deferred Work

Items deferred from the mobile app v1 plan. All require no breaking changes to ship.

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
