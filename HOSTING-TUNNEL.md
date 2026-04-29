# CalendarParse — Path 1: Cloudflare Tunnel

## Goal
Expose the existing local API publicly via Cloudflare Tunnel using the existing
`devinguthrie.com` domain (registered at Network Solutions).
No cloud server. One minor code change (Auth0 audience). No new domain purchase needed.

---

## Architecture

```
Mobile app (Android)
      │
      ▼ HTTPS
Cloudflare Edge (global CDN/proxy)
      │
      ▼ Outbound tunnel (cloudflared on your PC)
CalendarParse.Api (port 5150, Windows PC)
      │
      ▼ localhost:11434
Ollama + glm-ocr (same PC)
```

The tunnel works by `cloudflared` on your PC making an **outbound** connection to Cloudflare.
No port forwarding, no firewall changes, no static IP needed.

---

## Prerequisites

- Cloudflare account (free) — cloudflare.com
- `devinguthrie.com` registered at Network Solutions (already owned)
- Ollama running locally with glm-ocr pulled
- API built and published (see DEPLOY.md)

---

## Steps

### Step 1 — Add devinguthrie.com to Cloudflare and update nameservers

You do **not** need to transfer the domain away from Network Solutions. You only need
Cloudflare to manage DNS for it. The registration stays at Network Solutions.

**1a — Add domain to Cloudflare:**
1. Go to [dash.cloudflare.com](https://dash.cloudflare.com) → **Add a Site**
2. Enter `devinguthrie.com` → select the **Free plan**
3. Cloudflare scans your existing DNS records (review and confirm they look right)
4. Cloudflare assigns two nameservers — copy them, e.g.:
   ```
   abby.ns.cloudflare.com
   bob.ns.cloudflare.com
   ```

**1b — Update nameservers at Network Solutions:**
1. Log in to [networksolutions.com](https://www.networksolutions.com)
2. Go to **My Domain Names** → select `devinguthrie.com`
3. Click **Edit DNS** → **Manage Name Servers** → **Change Where Domain Points**
4. Replace the existing nameservers with the two Cloudflare nameservers from step 1a
5. Save

Propagation takes minutes to 48 hours. Cloudflare emails you when it's active.
Check status at [dnschecker.org](https://dnschecker.org).

> **Subdomain**: Using `api.devinguthrie.com` keeps the main domain pointing wherever
> it does today (your existing website, etc.) — only the `api` subdomain routes to
> the tunnel. No disruption to anything else on `devinguthrie.com`.

---

### Step 2 — Install cloudflared
```powershell
winget install Cloudflare.cloudflared
```
Or download the installer from:
https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/

---

### Step 3 — Authenticate to Cloudflare
```powershell
cloudflared tunnel login
```
Opens a browser → authorize with your Cloudflare account.
Saves cert to: `C:\Users\<you>\.cloudflared\cert.pem`

---

### Step 4 — Create the tunnel
```powershell
cloudflared tunnel create calendarparse
```
Output: tunnel ID (a UUID) and credentials saved to:
`C:\Users\<you>\.cloudflared\<tunnel-id>.json`

Note the tunnel ID — you need it in the config file.

---

### Step 5 — Create config file
Create: `C:\Users\<you>\.cloudflared\config.yml`

```yaml
tunnel: <your-tunnel-id>
credentials-file: C:\Users\<you>\.cloudflared\<your-tunnel-id>.json

ingress:
  - hostname: api.devinguthrie.com
    service: http://localhost:5150
  - service: http_status:404
```

Replace `<your-tunnel-id>` with the UUID from Step 4.

---

### Step 6 — Create DNS record
```powershell
cloudflared tunnel route dns calendarparse api.devinguthrie.com
```
This adds a CNAME in Cloudflare DNS:
`api.devinguthrie.com → <tunnel-id>.cfargotunnel.com`

---

### Step 7 — Test the tunnel (before installing service)
Terminal 1 — start the API:
```powershell
.\CalendarParse.Api.exe
# or: dotnet run --project CalendarParse.Api
```

Terminal 2 — run the tunnel:
```powershell
cloudflared tunnel run calendarparse
```

From your phone (or any external network), test:
```
GET https://api.devinguthrie.com/health
```
Expected: `{ "status": "ok", "ollamaAvailable": true, ... }`

---

### Step 8 — Install cloudflared as Windows service (auto-start on boot)
Run **as Administrator**:
```powershell
cloudflared service install
```
The tunnel now starts at Windows boot without you doing anything.

To verify it's running as a service:
```powershell
Get-Service cloudflared
```

---

### Step 9 — Auto-start the API on PC boot

**Option A — Task Scheduler (simplest)**:
1. Win+R → `taskschd.msc`
2. Create Basic Task → Trigger: "When I log on"
3. Action: Start program → path to `CalendarParse.Api.exe` (from publish folder)
4. Start in: the publish folder path

**Option B — NSSM (Windows service, runs even if not logged in)**:
```powershell
# Download NSSM from nssm.cc then:
nssm install CalendarParseApi "C:\path\to\CalendarParse.Api.exe"
nssm start CalendarParseApi
```
Run as LOCAL SYSTEM so it starts before login.

**Ollama**: Ollama for Windows adds itself to startup apps automatically.
Verify it's enabled: `Task Manager → Startup apps → Ollama`
If missing, add it manually or run `ollama serve` in the Task Scheduler task.

---

### Step 10 — Update appsettings.json and mobile app

**One-line code change** — update `Auth0:Audience` in `CalendarParse.Api/appsettings.json`:
```json
"Auth0": {
  "Domain": "dev-mvllt4ls0rxvyru2.us.auth0.com",
  "Audience": "https://api.devinguthrie.com"
}
```
Then re-publish the API exe (see DEPLOY.md).

> Note: Auth0 is only used for JWT Bearer auth (future use). The mobile app currently
> uses API key auth so this change has no immediate functional impact — but it's good
> hygiene to keep it accurate.

**Update mobile app** — in the app's **Settings tab**:
- **Server URL**: `https://api.devinguthrie.com` (was `http://192.168.x.x:5150`)
- **API key**: unchanged — same key from `appsettings.json`

Test the full flow: Import photo → parse → confirm.

---

## What changes in the code

| Item | Change needed? |
|---|---|
| API binding (`0.0.0.0:5150`) | ✅ Already correct |
| CORS | ✅ No CORS config — no change |
| API key persistence | ✅ Fine — `appsettings.json` persists on PC |
| Auth0 audience | ⚠️ Update to `https://api.devinguthrie.com` in `appsettings.json` |
| Service registration (HybridCalendarService) | ✅ Fine — WinRT works on your PC |
| Mobile app server URL | ⚠️ Update in app Settings |

**Net: one-line change** to `appsettings.json` + re-publish the exe.

---

## Ongoing tradeoffs

| Situation | Impact |
|---|---|
| PC is off / rebooting | API unreachable until PC is back up |
| Home internet goes down | API unreachable |
| Ollama crashes | Jobs fail; `ollamaAvailable: false` on `/health` |
| Power outage | All services down until power restored + auto-start |

This is fine for personal/occasional use. For always-on reliability, see `HOSTING-CLOUD.md`.

---

## Rollback

To stop public exposure: run `cloudflared service stop` or uninstall with
`cloudflared service uninstall`. The API continues running locally for LAN use.
