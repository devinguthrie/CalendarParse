# CalendarParse — Path 2: Cloud API + RunPod GPU

## Goal
Always-on public hosting with no PC dependency.
GLM-OCR pipeline only (98.8% accuracy). WinRT not available on cloud — this is fine.
Cost: ~$5–20/mo depending on sub-option.

---

## Architecture

```
Mobile app (Android)
      │
      ▼ HTTPS
Cloudflare DNS → Cloud API host
                      │
                      ▼ POST /api/generate (Ollama format)
               RunPod Serverless Worker
               (Python handler → Ollama → glm-ocr model)
```

---

## Sub-options

### 2A — Linux container (Fly.io or Railway)
- **Cost**: ~$0–5/mo (Fly.io free tier or Railway $5/mo) + RunPod GPU + domain
- **Risk**: Emgu.CV native library packaging on Linux (decode/resize/encode — basic ops, should work)
- **Requires**: Dockerfile

### 2B — Azure App Service Windows (~$15–20/mo)
- **Cost**: B1 Windows plan ~$13/mo + RunPod GPU + domain
- **Risk**: None for Emgu.CV (already works on Windows). No Dockerfile needed.
- **WinRT note**: Still NOT available on Windows Server (App Service). This is not a loss.

---

## Required Code Changes

### 1. Fix API key persistence 🚨
`EnsureApiKey()` writes key to `appsettings.json`. On any cloud redeploy this file is
wiped → new key generated → mobile app breaks.

**Fix in `CalendarParse.Api/Program.cs`** (~line 422):
```csharp
// Before generating a new key, check env var override:
var envKey = builder.Configuration["CalendarParse:ApiKey"];
if (!string.IsNullOrWhiteSpace(envKey))
    return envKey;   // use injected key, skip file write
// ... existing auto-generate logic below
```

Set env var on the host:
```
CALENDARPARSE__CalendarParse__ApiKey=<your-stable-key>
```

### 2. Swap service registration in Program.cs
Currently `HybridCalendarService` is registered. Change to `GlmOcrCalendarService`.

In `Program.cs` (~line 39-40):
```csharp
// BEFORE:
builder.Services.AddScoped<ICalendarParseService, HybridCalendarService>();

// AFTER:
builder.Services.AddScoped<ICalendarParseService, GlmOcrCalendarService>();
```

Also update default model in `appsettings.json`:
```json
"OllamaModel": "glm-ocr"
```

And set `OllamaBaseUrl` to point at the RunPod worker URL via env var.

### 3. Linux only (2A): Add Emgu.CV Linux runtime package
In `CalendarParse.Parsing/CalendarParse.Parsing.csproj`:
```xml
<PackageReference Include="Emgu.CV.runtime.linux-x64" Version="4.12.0.5764"
  Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
```

### 4. Linux only (2A): Dockerfile
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5150

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish CalendarParse.Api -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CalendarParse.Api.dll"]
```

### 5. Persistent storage
SQLite + uploaded images at `%LOCALAPPDATA%/CalendarParse/` — ephemeral in cloud.
- **Fly.io**: 1GB persistent volume (free) → mount at `/root/.local/share/CalendarParse`
- **Azure**: Azure Files share mount (~$0.06/GB/mo) or accept data loss on redeploy
- **Or**: Migrate to Azure Blob for images + keep SQLite but accept jobs lost on redeploy

---

## RunPod Serverless Worker

### How it works
`GlmOcrCalendarService` calls `POST {ollamaBaseUrl}/api/generate` (standard Ollama API).
RunPod Serverless requires a Python handler. Build a proxy:

```python
# handler.py
import runpod, requests, subprocess, time

def start_ollama():
    subprocess.Popen(["ollama", "serve"])
    time.sleep(3)  # wait for startup

def handler(job):
    payload = job["input"]
    resp = requests.post("http://localhost:11434/api/generate", json=payload, stream=False)
    return resp.json()

start_ollama()
runpod.serverless.start({"handler": handler})
```

### Dockerfile for RunPod worker
```dockerfile
FROM ollama/ollama:latest

# Install Python + RunPod SDK
RUN apt-get update && apt-get install -y python3 python3-pip
RUN pip3 install runpod requests

# Bake in glm-ocr model weights (~4-5GB) so cold start doesn't need to download
RUN ollama serve & sleep 5 && ollama pull glm-ocr && pkill ollama

COPY handler.py .
CMD ["python3", "handler.py"]
```

Docker image: ~8–12GB. Cold start (cached on RunPod): 30–90s.

### Cost
- RTX 3090: ~$0.000126/sec
- 60s inference = **~$0.008/job**
- 200 jobs/month = **~$1.60 GPU cost**

### API URL format
RunPod Serverless exposes jobs via:
```
POST https://api.runpod.ai/v2/{endpoint-id}/runsync
```
This is NOT the Ollama format — need an adapter in the C# service or a RunPod worker
that re-wraps the request and exposes a compatible endpoint.

**Recommended**: Configure the RunPod worker to accept the Ollama JSON body directly
(i.e., `handler` receives `{"model":..., "prompt":..., "images":[...]}` and forwards
it unchanged to local Ollama). Then update `GlmOcrCalendarService` to call the RunPod
sync endpoint instead of Ollama directly — or run a lightweight proxy that maps
`POST /api/generate` → RunPod sync.

---

## Optional: Fireworks AI Fallback

`FireworksCalendarService` already exists. Add as fallback when RunPod is unavailable:
1. Set `FIREWORKS_API_KEY` env var
2. In job processor: if RunPod request times out (>120s), retry with Fireworks
3. Cost: ~$0.002/fallback job

---

## Deployment Steps (2B — Azure App Service)

1. Make code changes above (API key fix, service swap)
2. `az login`
3. Create resource group + App Service plan (Windows, B1):
   ```bash
   az group create --name calendarparse-rg --location eastus
   az appservice plan create --name calendarparse-plan --resource-group calendarparse-rg \
     --sku B1 --is-windows
   az webapp create --name calendarparse-api --resource-group calendarparse-rg \
     --plan calendarparse-plan --runtime "DOTNET|10.0"
   ```
4. Set env vars:
   ```bash
   az webapp config appsettings set --name calendarparse-api \
     --resource-group calendarparse-rg \
     --settings CalendarParse__ApiKey=<stable-key> \
               CalendarParse__OllamaBaseUrl=<runpod-worker-url> \
               CalendarParse__OllamaModel=glm-ocr
   ```
5. Publish:
   ```powershell
   dotnet publish CalendarParse.Api -c Release
   az webapp deploy --name calendarparse-api --resource-group calendarparse-rg \
     --src-path CalendarParse.Api/bin/Release/net10.0-windows.../publish.zip
   ```
6. Set up Cloudflare DNS → Azure App Service custom domain
7. Build + deploy RunPod worker
8. Update mobile app server URL

---

## Cost Comparison

| Item | 2A (Fly.io + RunPod) | 2B (Azure + RunPod) |
|---|---|---|
| API host | $0–5/mo | ~$13/mo |
| GPU (200 jobs/mo) | ~$2/mo | ~$2/mo |
| Domain | ~$1/mo | ~$1/mo |
| Storage | Free (1GB vol) | $0–1/mo |
| **Total** | **~$3–8/mo** | **~$16–17/mo** |

---

## What this path requires vs Path 1

| | Path 1 (Cloudflare Tunnel) | Path 2 |
|---|---|---|
| Code changes | None | Medium (3–5 files) |
| PC must be on | Yes | No |
| Always-on | No | Yes |
| WinRT | Available (not needed) | Not available |
| Engineering effort | ~2 hrs | ~1–2 days |

**Verdict**: Do Path 1 first. Switch to Path 2 if you need always-on reliability
or start getting real users.
