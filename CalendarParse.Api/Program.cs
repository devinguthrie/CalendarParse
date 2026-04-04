using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarParse.Api;
using CalendarParse.Api.Data;
using CalendarParse.Cli.Services;
using CalendarParse.Models;
using CalendarParse.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
var port          = builder.Configuration.GetValue<int>("CalendarParse:Port", 5150);
var ollamaBaseUrl = builder.Configuration["CalendarParse:OllamaBaseUrl"] ?? "http://localhost:11434";
var ollamaModel   = builder.Configuration["CalendarParse:OllamaModel"]   ?? "qwen2.5vl:7b";
var debugMode     = builder.Configuration.GetValue<bool>("CalendarParse:DebugMode", false);

// ── Shared secret ─────────────────────────────────────────────────────────────
var apiKey = EnsureApiKey(builder, port);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();

// Transient: each request gets a fresh HybridCalendarService instance.
builder.Services.AddTransient<HybridCalendarService>(_ =>
    new HybridCalendarService(baseUrl: ollamaBaseUrl, model: ollamaModel));

// Job database (SQLite, stored in %LOCALAPPDATA%/CalendarParse/)
var jobDbDir  = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CalendarParse");
Directory.CreateDirectory(jobDbDir);

var jobDbPath = Path.Combine(jobDbDir, "jobs.db");
builder.Services.AddDbContext<JobDbContext>(opts =>
    opts.UseSqlite($"Data Source={jobDbPath}"),
    ServiceLifetime.Singleton);

// Image storage directory
var imageDir = Path.Combine(jobDbDir, "images");
Directory.CreateDirectory(imageDir);

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

if (debugMode)
    Console.WriteLine("[DEBUG MODE] API will return mock responses after a 5-second delay.");

// ── Schema init ───────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Auth middleware (skip /health) ────────────────────────────────────────────
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/auth"))
    {
        await next(context);
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-CalendarParse-Key", out var incoming)
        || incoming.ToString() != apiKey)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key." });
        return;
    }

    await next(context);
});

// ── Endpoints ─────────────────────────────────────────────────────────────────

// GET /health — unauthenticated; mobile app checks this before showing share prompt
app.MapGet("/health", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(5);
    try
    {
        var response = await client.GetAsync($"{ollamaBaseUrl}/api/tags");
        return Results.Ok(new { status = "ok", ollamaAvailable = response.IsSuccessStatusCode });
    }
    catch
    {
        return Results.Ok(new { status = "error", ollamaAvailable = false });
    }
});

// POST /submit — accepts image bytes, immediately returns a job ID, processes asynchronously
app.MapPost("/submit", async (SubmitRequest request, JobDbContext db, IServiceProvider services) =>
{
    byte[] imageBytes;
    try
    {
        imageBytes = Convert.FromBase64String(request.ImageBase64);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid imageBase64 encoding." });
    }

    // Save image to disk
    var job = new Job
    {
        EmployeeName = request.EmployeeName,
    };
    job.ImagePath = Path.Combine(imageDir, $"{job.Id}.jpg");
    await File.WriteAllBytesAsync(job.ImagePath, imageBytes);

    db.Jobs.Add(job);
    await db.SaveChangesAsync();

    // Fire-and-forget background processing
    _ = ProcessJobAsync(job.Id, services, ollamaBaseUrl, ollamaModel, imageDir, db, debugMode);

    return Results.Ok(new SubmitResponse { JobId = job.Id });
});

// GET /jobs/{id}/status — poll job status
app.MapGet("/jobs/{id}/status", async (string id, JobDbContext db) =>
{
    var job = await db.Jobs.FindAsync(id);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    return Results.Ok(new JobStatusResponse
    {
        Status = job.Status.ToString().ToLowerInvariant(),
        Error  = job.Error,
    });
});

// GET /jobs/{id}/result — fetch result when done
app.MapGet("/jobs/{id}/result", async (string id, JobDbContext db) =>
{
    var job = await db.Jobs.FindAsync(id);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    if (job.Status != JobStatus.Done)
        return Results.Json(
            new { error = $"Job is not done (status: {job.Status})." },
            statusCode: 409);

    JobResultResponse? result = null;
    if (job.ResultJson is not null)
        result = JsonSerializer.Deserialize<JobResultResponse>(job.ResultJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    return Results.Ok(result ?? new JobResultResponse());
});

// POST /process — legacy synchronous endpoint (kept for backward compat / CLI use)
app.MapPost("/process", async (ProcessRequest request, HybridCalendarService hybrid) =>
{
    if (debugMode)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        return Results.Ok(BuildMockProcessResponse(request.EmployeeName));
    }

    byte[] imageBytes;
    try
    {
        imageBytes = Convert.FromBase64String(request.ImageBase64);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid imageBase64 encoding." });
    }

    using var stream = new MemoryStream(imageBytes);
    var result = await hybrid.ProcessWithBoundsAsync(stream, request.EmployeeName);

    if (result.IsError)
        return Results.Json(new { error = result.Error }, statusCode: 500);

    return Results.Ok(new ProcessResponse
    {
        Shifts      = result.Shifts,
        ImageWidth  = result.ImageWidth,
        ImageHeight = result.ImageHeight,
    });
});

// POST /confirm — stores employee-corrected shifts for future accuracy improvement
app.MapPost("/confirm", (ConfirmRequest request, ILogger<Program> logger) =>
{
    logger.LogInformation("Received {Count} confirmed shift(s) for training data.", request.Shifts.Count);
    // TODO: persist to corrections store (see TODOS.md)
    return Results.Ok(new { ok = true });
});

// ── Auth scaffolding (stubs — will be wired up when auth is implemented) ──────
app.MapPost("/auth/register", () => Results.StatusCode(501)).AllowAnonymous();
app.MapPost("/auth/login",    () => Results.StatusCode(501)).AllowAnonymous();

app.Run();

// ── Background job processor ──────────────────────────────────────────────────

static async Task ProcessJobAsync(
    string jobId,
    IServiceProvider services,
    string ollamaBaseUrl,
    string ollamaModel,
    string imageDir,
    JobDbContext db,
    bool debugMode = false)
{
    // Mark as processing
    var job = await db.Jobs.FindAsync(jobId);
    if (job is null) return;

    job.Status = JobStatus.Processing;
    await db.SaveChangesAsync();

    try
    {
        if (debugMode)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var mockPayload = BuildMockJobResultResponse(job.EmployeeName);
            job.ResultJson  = JsonSerializer.Serialize(mockPayload);
            job.Status      = JobStatus.Done;
            job.CompletedAt = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG MODE] Job {jobId} — returning mock result.");
            await db.SaveChangesAsync();
            return;
        }

        var imageBytes = await File.ReadAllBytesAsync(job.ImagePath);
        using var stream = new MemoryStream(imageBytes);

        var hybrid = new HybridCalendarService(baseUrl: ollamaBaseUrl, model: ollamaModel);
        var result = await hybrid.ProcessWithBoundsAsync(stream, job.EmployeeName);

        if (result.IsError)
        {
            job.Status      = JobStatus.Error;
            job.Error       = result.Error;
            job.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            var resultPayload = new JobResultResponse
            {
                Shifts      = result.Shifts,
                ImageWidth  = result.ImageWidth,
                ImageHeight = result.ImageHeight,
            };
            job.ResultJson  = JsonSerializer.Serialize(resultPayload);
            job.Status      = JobStatus.Done;
            job.CompletedAt = DateTime.UtcNow;

            Console.WriteLine($"[Job {jobId}] Done — result JSON:\n{job.ResultJson}");

            // Image no longer needed on server after result is stored
            try { File.Delete(job.ImagePath); } catch { /* best-effort */ }
        }
    }
    catch (Exception ex)
    {
        job.Status      = JobStatus.Error;
        job.Error       = ex.Message;
        job.CompletedAt = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();
}

// ── Mock helpers (debug mode) ─────────────────────────────────────────────────

static List<ShiftData> BuildMockShifts(string employeeName)
{
    var name = string.IsNullOrWhiteSpace(employeeName) ? "Franny" : employeeName;
    return
    [
        new ShiftData { Employee = name, Date = "2025-11-02", TimeRange = "10:00-6:30" },
        new ShiftData { Employee = name, Date = "2025-11-03", TimeRange = "2:00-6:30"  },
        new ShiftData { Employee = name, Date = "2025-11-04", TimeRange = "2:00-6:30"  },
        new ShiftData { Employee = name, Date = "2025-11-05", TimeRange = "2:00-6:30"  },
        new ShiftData { Employee = name, Date = "2025-11-06", TimeRange = "2:00-6:30"  },
        new ShiftData { Employee = name, Date = "2025-11-07", TimeRange = "4:00-6:30"  },
        new ShiftData { Employee = name, Date = "2025-11-08", TimeRange = "4:00-6:30"  },
    ];
}

static ProcessResponse BuildMockProcessResponse(string employeeName) => new()
{
    Shifts      = BuildMockShifts(employeeName),
    ImageWidth  = 1080,
    ImageHeight = 773,
};

static JobResultResponse BuildMockJobResultResponse(string employeeName) => new()
{
    Shifts      = BuildMockShifts(employeeName),
    ImageWidth  = 1080,
    ImageHeight = 773,
};

// ── Helpers ───────────────────────────────────────────────────────────────────

static string EnsureApiKey(WebApplicationBuilder builder, int port)
{
    var key = builder.Configuration["CalendarParse:ApiKey"];
    if (!string.IsNullOrWhiteSpace(key))
        return key;

    // Generate a new key, persist it to appsettings.json, and print it once.
    key = Guid.NewGuid().ToString("N");

    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    try
    {
        var json = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
        var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var cp   = root["CalendarParse"] as JsonObject ?? new JsonObject();
        cp["ApiKey"] = key;
        root["CalendarParse"] = cp;
        File.WriteAllText(settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WARNING] Could not persist API key to {settingsPath}: {ex.Message}");
    }

    // Get local IP address
    var hostName   = System.Net.Dns.GetHostName();
    var ipAddresses = System.Net.Dns.GetHostAddresses(hostName);
    var localIp    = ipAddresses.FirstOrDefault(ip =>
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "localhost";
    var serverUrl = $"http://{localIp}:{port}";

    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  CalendarParse API — first-run setup                   ║");
    Console.WriteLine($"║  Server: {serverUrl,-53}║");
    Console.WriteLine($"║  API key: {key,-51}║");
    Console.WriteLine("║  Enter Server + Key in the mobile app Settings tab      ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");

    return key;
}
