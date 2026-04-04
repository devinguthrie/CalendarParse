# CalendarParse

**Extract shift schedules from photos using AI, then add them to your calendar.**

Take a picture of a printed or digital schedule, CalendarParse runs it through a vision LLM (Ollama), extracts your shifts, and displays them in the app. From there, add them to your calendar with one tap.

---

## Quick Start

### Prerequisites
- **Windows PC** with [Ollama](https://ollama.ai) installed and running
- **Android phone** on the same WiFi network
- [.NET 10](https://dotnet.microsoft.com/download) and [MAUI workload](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation)
- USB debugging enabled on your phone (first-time Android deploy only)

### 1. Start the API server on your PC
```powershell
dotnet run --project CalendarParse.Api
```
On first run, copy the generated API key. Make sure Ollama is running in the background.

### 2. Build and deploy the app to your phone
```powershell
.\run-android.ps1
```

### 3. Configure the app
- **Settings tab**: Enter your name, server URL (`http://<pc-ip>:5150`), and API key
- **Monitor tab**: Enter your messaging app package and boss's name
- **Android Settings** → go to **Notification Access** → enable CalendarParse

### 4. Test it
1. Import → pick a schedule photo
2. Review the extracted shifts
3. Tap "Add to Calendar"

---

## How It Works

1. **Capture** — Take a photo of any schedule (paper, screen, image file)
2. **Extract** — Ollama runs a vision model to parse employee names, shift times, and dates
3. **Display** — Results show up in the app with name filtering
4. **Add** — One tap adds shifts to your phone's calendar

The phone communicates with a backend API (`CalendarParse.Api`) running on your PC. The API handles authentication, image processing, and integration with Ollama.

---

## Project Structure

```
CalendarParse/
├── CalendarParse/              # MAUI app (Android/iOS)
├── CalendarParse.Api/          # Backend API server
├── CalendarParse.Core/         # Shared business logic
├── CalendarParse.Cli/          # CLI benchmarking tool
├── CalendarParse.Tests/        # Unit tests
└── run-android.ps1            # Deploy script
```

---

## Development

### Run tests
```powershell
dotnet test
```
36 tests pass. One integration test (live Ollama) is skipped — run it manually with a real schedule photo.

### Benchmark the vision model
The CLI tool measures extraction accuracy against a test set:
```powershell
dotnet run --project CalendarParse.Cli -- "path/to/test/images" --test --model qwen2.5vl:7b
```

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Health check fails from phone | Check PC firewall — allow TCP 5150 inbound |
| "Invalid server key" toast | Re-copy the key from `CalendarParse.Api/appsettings.json` |
| Ollama reports unavailable | Make sure `ollama serve` is running on the PC |
| Shifts in wrong time zone | Check phone system clock and timezone settings |
| App crashes on Notification Access | Enable it in Android Settings → Apps → Notification Access |

---

## Documentation

- **[DEPLOY.md](DEPLOY.md)** — Build, publish, and first-run setup
- **[TODOS.md](TODOS.md)** — Open work and known issues
- **[.github/docs/](\.github\docs\)** — Experiment logs and benchmarking notes

---

## License

MIT
