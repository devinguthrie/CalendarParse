# CalendarParse — Build & Deploy

## Architecture recap

| Component | Runs on | Port |
|-----------|---------|------|
| `CalendarParse.Api` | Windows PC (same machine as Ollama) | 5150 |
| `CalendarParse` MAUI app | Android phone | — |

The phone connects to the PC over LAN. Ollama must be running on the PC.

---

## PC Server (`CalendarParse.Api`)

### Dev / quick start
```powershell
dotnet run --project CalendarParse.Api
```

**First run** prints the generated API key — copy it, you'll need it in the app's Settings:
```
CalendarParse API key: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
Listening on http://0.0.0.0:5150
```
The key is saved to `CalendarParse.Api/appsettings.json` and reused on subsequent starts.

**Verify from the phone** (same WiFi): `http://<pc-ip>:5150/health`

### Production (self-contained exe)
```powershell
dotnet publish CalendarParse.Api -c Release -r win-x64 --self-contained
# Output: CalendarParse.Api/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/
```

Run `CalendarParse.Api.exe` from that folder. Keep Ollama running alongside it.

---

## Android App (`CalendarParse`)

Requires the [.NET MAUI workload](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation).

### Deploy directly to a USB-connected device
Enable USB debugging on the phone first, then:
```powershell
.\run-android.ps1
```
The script builds the APK, installs it via `adb`, and launches the app. `adb` must be on PATH
or at `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe` (the default Android Studio location).

> Note: `-t:Run` is unreliable when `MainActivity` has both `MainLauncher=true` and an
> `[IntentFilter]`. The script uses `adb install` + `adb shell am start` instead.

### Build a signed `.apk` for sideloading
```powershell
dotnet publish CalendarParse -f net10.0-android -c Release `
  /p:AndroidSigningKeyStore=your.keystore `
  /p:AndroidSigningKeyAlias=key0 `
  /p:AndroidSigningKeyPass=<pass> `
  /p:AndroidSigningStorePass=<pass>
# Output: CalendarParse/bin/Release/net10.0-android/
```

If you don't have a keystore yet:
```powershell
keytool -genkey -v -keystore calendarparse.keystore -alias key0 `
  -keyalg RSA -keysize 2048 -validity 10000
```

---

## Run the tests
```powershell
dotnet test CalendarParse.Tests
```
36 pass, 1 skipped (live Ollama integration test — run manually with a real image).

---

## First-run checklist (phone)

1. **Settings tab** — enter:
   - Your name *exactly* as it appears on the schedule
   - Server URL: `http://<pc-ip>:5150`
   - API key (from the server's first-run output)
2. **Monitor tab** — enter:
   - Messaging app package (e.g. `com.google.android.apps.messaging` for Google Messages)
   - Your boss's display name as it appears in notifications
3. Go to Android **Settings → Notification Access** → grant CalendarParse access
4. Test the full flow: **Import** button → pick a schedule photo → watch the spinner → confirm overlay bubbles → Add to Calendar

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Health check fails from phone | Check PC firewall — allow inbound TCP on port 5150 |
| "Invalid server key" toast | Re-copy the key from `appsettings.json` on the PC |
| Overlay bubbles in wrong place | EXIF rotation issue — re-share the image via the share sheet (not import) |
| No notification detected | Confirm Notification Access permission is granted; check package name and sender name match exactly |
| `ollamaAvailable: false` on health | Ollama isn't running — start it: `ollama serve` |
