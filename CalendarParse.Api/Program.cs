using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using System.Threading.RateLimiting;
using CalendarParse.Api;
using CalendarParse.Api.Data;
using CalendarParse.Api.Services;
using CalendarParse.Auth;
using CalendarParse.Models;
using CalendarParse.Parsing.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Sentry;

var builder = WebApplication.CreateBuilder(args);

// ── Sentry ────────────────────────────────────────────────────────────────────
// DSN and options are read from the "Sentry" section in appsettings.json.
// Set Sentry:Dsn in appsettings.Development.json or via env var SENTRY_DSN.
builder.WebHost.UseSentry();

// ── Configuration ─────────────────────────────────────────────────────────────
var port          = builder.Configuration.GetValue<int>("CalendarParse:Port", 5150);
var ollamaBaseUrl = builder.Configuration["CalendarParse:OllamaBaseUrl"] ?? "http://localhost:11434";
var ollamaModel   = builder.Configuration["CalendarParse:OllamaModel"]   ?? "qwen2.5vl:7b";
var debugMode     = builder.Configuration.GetValue<bool>("CalendarParse:DebugMode", false);

// ── Shared secret ─────────────────────────────────────────────────────────────
var apiKey = EnsureApiKey(builder, port);

// ── Auth0 config ──────────────────────────────────────────────────────────────
var auth0Domain   = builder.Configuration["Auth0:Domain"]   ?? string.Empty;
var auth0Audience = builder.Configuration["Auth0:Audience"] ?? string.Empty;

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();

// Transient: each request gets a fresh GlmOcrCalendarService instance.
// Note: GlmOcrCalendarService.Http is a static HttpClient — creating new instances is safe.
// GLM-OCR (98.8% accuracy) does NOT populate ImageWidth/Height or EstimatedBounds.
// The overlay feature in the mobile app is Hybrid-pipeline only; GLM-OCR returns 0,0.
builder.Services.AddTransient<GlmOcrCalendarService>(_ =>
    new GlmOcrCalendarService(baseUrl: ollamaBaseUrl, model: ollamaModel));

// Data directory — override CalendarParse:DataDir (env: CalendarParse__DataDir) for
// cloud/container deployments where %LOCALAPPDATA% may be ephemeral or non-standard.
// Local default: %LOCALAPPDATA%/CalendarParse  (e.g. C:\Users\<you>\AppData\Local\CalendarParse)
// Cloud example: /data  (mount a persistent volume here on Fly.io / Azure Files)
var configuredDataDir = builder.Configuration["CalendarParse:DataDir"];
var jobDbDir = !string.IsNullOrWhiteSpace(configuredDataDir)
    ? configuredDataDir
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalendarParse");
Directory.CreateDirectory(jobDbDir);

var jobDbPath = Path.Combine(jobDbDir, "jobs.db");
builder.Services.AddDbContextFactory<JobDbContext>(opts =>
    opts.UseSqlite($"Data Source={jobDbPath}"));

// Image storage directory
var imageDir = Path.Combine(jobDbDir, "images");
Directory.CreateDirectory(imageDir);

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ── Authentication: JWT Bearer (mobile) + API key (CLI) ───────────────────────
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "DualAuth";
        options.DefaultChallengeScheme    = "DualAuth";
    })
    .AddPolicyScheme("DualAuth", "JWT or API Key", options =>
    {
        options.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey("Authorization")
                ? JwtBearerDefaults.AuthenticationScheme
                : ApiKeyAuthenticationHandler.SchemeName;
    })
    .AddJwtBearer(options =>
    {
        options.Authority        = $"https://{auth0Domain}/";
        options.Audience         = auth0Audience;
        options.MapInboundClaims = false; // keep standard claim names (sub, email, name)
    })
    .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName,
        opts => opts.ApiKey = apiKey);

builder.Services.AddAuthorization();

// Per-IP rate limiting: 10 submit requests per minute.
// UseForwardedHeaders (added below) must run first so the real client IP from
// X-Forwarded-For (set by Cloudflare Tunnel) is visible here.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("submit", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 10,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));
    options.RejectionStatusCode = 429;
});

// Background job processor: polls for Submitted jobs and runs them via GLM-OCR.
// Handles retries with exponential backoff for transient Ollama failures.
builder.Services.AddHostedService(sp =>
    new BackgroundJobProcessor(
        sp.GetRequiredService<IDbContextFactory<JobDbContext>>(),
        ollamaBaseUrl,
        ollamaModel,
        debugMode,
        sp.GetRequiredService<ILogger<BackgroundJobProcessor>>()));

var app = builder.Build();

if (debugMode)
    Console.WriteLine("[DEBUG MODE] API will return mock responses after a 5-second delay.");

// ── Schema init ───────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
    await db.Database.EnsureCreatedAsync();
    // Add UserId for existing deployments — EnsureCreated won't add new columns.
    // Check via PRAGMA first so ALTER TABLE is only executed when the column is absent,
    // avoiding the EF Core fail-level log that fires before the catch block.
    var userIdExists = await db.Database
        .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('Jobs') WHERE name='UserId'")
        .SingleAsync();
    if (userIdExists == 0)
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Jobs ADD COLUMN UserId TEXT");

    var retryCountExists = await db.Database
        .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('Jobs') WHERE name='RetryCount'")
        .SingleAsync();
    if (retryCountExists == 0)
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Jobs ADD COLUMN RetryCount INTEGER NOT NULL DEFAULT 0");

    var nextRetryAtExists = await db.Database
        .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('Jobs') WHERE name='NextRetryAt'")
        .SingleAsync();
    if (nextRetryAtExists == 0)
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Jobs ADD COLUMN NextRetryAt TEXT");
}

// ── Middleware ────────────────────────────────────────────────────────────────
// ForwardedHeaders must come before RateLimiter and Authentication so that the real
// client IP from X-Forwarded-For (set by Cloudflare) is used for rate limiting and logging.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
app.UseRateLimiter();

// ── Auth middleware ────────────────────────────────────────────────────────────
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────────

// GET /sentry-test — DEBUG only; sends a test message + exception to Sentry
app.MapGet("/sentry-test", () =>
{
    SentrySdk.CaptureMessage("Hello from CalendarParse API!", SentryLevel.Info);
    try { throw new InvalidOperationException("Sentry test exception from /sentry-test endpoint."); }
    catch (Exception ex) { SentrySdk.CaptureException(ex); }
    return Results.Ok(new { sent = true, message = "Test event sent to Sentry — check your dashboard." });
});

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
app.MapPost("/submit", async (SubmitRequest request, JobDbContext db, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(request.EmployeeName))
        return Results.BadRequest(new { error = "employeeName is required." });

    // Pre-check base64 length to avoid large allocations from malicious payloads.
    // Base64 overhead ≈ 4/3; 20 MiB image → ~28 MiB base64 string.
    const int MaxImageBytes = 20 * 1024 * 1024;
    if (request.ImageBase64.Length > MaxImageBytes * 4 / 3 + 16)
        return Results.Json(new { error = "Image exceeds 20 MB limit." }, statusCode: 413);

    byte[] imageBytes;
    try
    {
        imageBytes = Convert.FromBase64String(request.ImageBase64);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid imageBase64 encoding." });
    }

    if (imageBytes.Length > MaxImageBytes)
        return Results.Json(new { error = "Image exceeds 20 MB limit." }, statusCode: 413);

    // JWT users must have a 'sub' claim; reject if missing to prevent unscoped job creation
    if (!OwnershipPolicy.IsApiKeyUser(user) && user.FindFirst("sub") is null)
        return Results.Unauthorized();

    var job = new Job
    {
        EmployeeName = request.EmployeeName,
        UserId       = OwnershipPolicy.GetUserId(user),
    };
    job.ImagePath = Path.Combine(imageDir, $"{job.Id}.jpg");
    await File.WriteAllBytesAsync(job.ImagePath, imageBytes);

    db.Jobs.Add(job);
    await db.SaveChangesAsync();

    // BackgroundJobProcessor picks this up within 5 seconds.
    return Results.Ok(new SubmitResponse { JobId = job.Id });
}).RequireAuthorization().RequireRateLimiting("submit");

// GET /jobs/{id}/status — poll job status
app.MapGet("/jobs/{id}/status", async (string id, JobDbContext db, ClaimsPrincipal user) =>
{
    var job = await db.Jobs.FindAsync(id);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    if (!OwnershipPolicy.CanAccess(user, job.UserId))
        return Results.Forbid();

    return Results.Ok(new JobStatusResponse
    {
        Status = job.Status.ToString().ToLowerInvariant(),
        Error  = job.Error,
    });
}).RequireAuthorization();

// GET /jobs/{id}/result — fetch result when done
app.MapGet("/jobs/{id}/result", async (string id, JobDbContext db, ClaimsPrincipal user) =>
{
    var job = await db.Jobs.FindAsync(id);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    if (!OwnershipPolicy.CanAccess(user, job.UserId))
        return Results.Forbid();

    if (job.Status != JobStatus.Done)
        return Results.Json(
            new { error = $"Job is not done (status: {job.Status})." },
            statusCode: 409);

    JobResultResponse? result = null;
    if (job.ResultJson is not null)
        result = JsonSerializer.Deserialize<JobResultResponse>(job.ResultJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    return Results.Ok(result ?? new JobResultResponse());
}).RequireAuthorization();

// POST /process — legacy synchronous endpoint (kept for backward compat / CLI use)
app.MapPost("/process", async (ProcessRequest request, GlmOcrCalendarService glm) =>
{
    if (string.IsNullOrWhiteSpace(request.EmployeeName))
        return Results.BadRequest(new { error = "employeeName is required." });

    if (debugMode)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        return Results.Ok(BuildMockProcessResponse(request.EmployeeName));
    }

    const int MaxImageBytes = 20 * 1024 * 1024;
    if (request.ImageBase64.Length > MaxImageBytes * 4 / 3 + 16)
        return Results.Json(new { error = "Image exceeds 20 MB limit." }, statusCode: 413);

    byte[] imageBytes;
    try
    {
        imageBytes = Convert.FromBase64String(request.ImageBase64);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid imageBase64 encoding." });
    }

    if (imageBytes.Length > MaxImageBytes)
        return Results.Json(new { error = "Image exceeds 20 MB limit." }, statusCode: 413);

    using var stream = new MemoryStream(imageBytes);
    var rawJson = await glm.ProcessAsync(stream, request.EmployeeName);

    if (rawJson.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = rawJson }, statusCode: 500);

    var calendarData = JsonSerializer.Deserialize<CalendarData>(rawJson,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    // Note: GLM-OCR does not populate ImageWidth/Height (overlay is Hybrid-pipeline only).
    return Results.Ok(new ProcessResponse { Shifts = calendarData.FlattenToShiftData() });
}).RequireAuthorization();

// POST /confirm — stores employee-corrected shifts for future accuracy improvement
app.MapPost("/confirm", async (ConfirmRequest request, JobDbContext db, ILogger<Program> logger, ClaimsPrincipal user) =>
{
    logger.LogInformation(
        "Received {Count} confirmed shift(s). jobId={JobId}",
        request.Shifts.Count,
        request.JobId ?? "(none)");

    Job? sourceJob = null;
    if (!string.IsNullOrWhiteSpace(request.JobId))
        sourceJob = await db.Jobs.FindAsync(request.JobId);

    // JWT users can only confirm their own jobs
    if (sourceJob is not null && !OwnershipPolicy.CanAccess(user, sourceJob.UserId))
        return Results.Forbid();

    var correction = new ConfirmedCorrection
    {
        JobId          = request.JobId,
        ImagePath      = sourceJob?.ImagePath,
        EmployeeName   = sourceJob?.EmployeeName ?? request.Shifts.FirstOrDefault()?.Employee ?? string.Empty,
        ShiftsJson     = JsonSerializer.Serialize(request.Shifts),
        ConfirmedAtUtc = DateTime.UtcNow,
    };

    db.ConfirmedCorrections.Add(correction);
    await db.SaveChangesAsync();

    return Results.Ok(new { ok = true, correctionId = correction.Id });
}).RequireAuthorization();

app.Run();

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
