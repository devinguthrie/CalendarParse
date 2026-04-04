using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CalendarParse.Data;
using CalendarParse.Models;
using Microsoft.EntityFrameworkCore;

namespace CalendarParse.Services;

public class HealthResult
{
    public string Status         { get; set; } = string.Empty;
    public bool   OllamaAvailable { get; set; }
}

/// <summary>
/// HTTP client for CalendarParse.Api endpoints.
/// All methods return null on recoverable network errors (caller shows toast + retry).
/// </summary>
public class ApiClient(
    IHttpClientFactory httpClientFactory,
    IServerDiscovery serverDiscovery,
    ScheduleHistoryDb db)
{
    private static readonly JsonSerializerOptions _json =
        new() { PropertyNameCaseInsensitive = true };

    // ── Health ────────────────────────────────────────────────────────────────

    /// <summary>Returns health status, or null if the server is unreachable.</summary>
    public async Task<HealthResult?> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var client = await BuildClientAsync(ct);
            var resp   = await client.GetAsync("health", ct);
            return await resp.Content.ReadFromJsonAsync<HealthResult>(_json, ct);
        }
        catch
        {
            return null;
        }
    }

    // ── Process ───────────────────────────────────────────────────────────────

    public record ProcessResult(List<ShiftData> Shifts, string? Error, int ImageWidth = 0, int ImageHeight = 0);

    /// <summary>Result wrapper that carries either a successful ProcessResult or a user-facing error message.</summary>
    public record ProcessOutcome(ProcessResult? Result, string UserMessage);

    /// <summary>
    /// Sends image bytes to POST /process and returns parsed shifts.
    /// <see cref="ProcessOutcome.Result"/> is null on network/auth/parse errors; check <see cref="ProcessOutcome.UserMessage"/>.
    /// </summary>
    public async Task<ProcessOutcome> ProcessAsync(
        byte[]  imageBytes,
        string  employeeName,
        CancellationToken ct = default)
    {
        try
        {
            var client  = await BuildClientAsync(ct);
            var payload = new
            {
                imageBase64  = Convert.ToBase64String(imageBytes),
                employeeName,
            };

            // Retry once on timeout before surfacing the error
            var resp = await client.PostAsJsonAsync("process", payload, ct)
                .ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new ProcessOutcome(null, "Invalid server key — check Settings.");

            var body = await resp.Content.ReadFromJsonAsync<ProcessResponseBody>(_json, ct);

            if (!resp.IsSuccessStatusCode)
                return new ProcessOutcome(null, $"Processing failed: {body?.Error ?? resp.ReasonPhrase}");

            return new ProcessOutcome(
                new ProcessResult(body?.Shifts ?? [], null, body?.ImageWidth ?? 0, body?.ImageHeight ?? 0),
                string.Empty);
        }
        catch (HttpRequestException)
        {
            return new ProcessOutcome(null, "Can't reach server — check IP:PORT in Settings.");
        }
        catch (InvalidOperationException)
        {
            return new ProcessOutcome(null, "Server URL is missing or invalid — check Settings.");
        }
        catch (TaskCanceledException)
        {
            return new ProcessOutcome(null, "Server timed out — try again.");
        }
        catch (JsonException)
        {
            return new ProcessOutcome(null, "Unexpected server response.");
        }
    }

    // ── Async job endpoints ───────────────────────────────────────────────────

    public record JobStatusResult(string Status, string? Error);

    /// <summary>
    /// POSTs image to /submit and returns the server-assigned job ID.
    /// Returns null on network or auth errors.
    /// </summary>
    public async Task<string?> SubmitAsync(
        byte[]  imageBytes,
        string  employeeName,
        CancellationToken ct = default)
    {
        try
        {
            var client  = await BuildClientAsync(ct);
            var payload = new
            {
                imageBase64  = Convert.ToBase64String(imageBytes),
                employeeName,
            };
            var resp = await client.PostAsJsonAsync("submit", payload, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<SubmitResponseBody>(_json, ct);
            return body?.JobId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Polls GET /jobs/{id}/status.
    /// Returns null on network error (caller retries).
    /// </summary>
    public async Task<JobStatusResult?> GetJobStatusAsync(
        string jobId,
        CancellationToken ct = default)
    {
        try
        {
            var client = await BuildClientAsync(ct);
            var resp   = await client.GetAsync($"jobs/{jobId}/status", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<JobStatusBody>(_json, ct);
            return body is null ? null : new JobStatusResult(body.Status, body.Error);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches GET /jobs/{id}/result when status is "done".
    /// Returns null if the job isn't done or on network error.
    /// </summary>
    public async Task<ProcessResult?> GetJobResultAsync(
        string jobId,
        CancellationToken ct = default)
    {
        try
        {
            var client = await BuildClientAsync(ct);
            var resp   = await client.GetAsync($"jobs/{jobId}/result", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<ProcessResponseBody>(_json, ct);
            return body is null
                ? null
                : new ProcessResult(body.Shifts ?? [], null, body.ImageWidth, body.ImageHeight);
        }
        catch
        {
            return null;
        }
    }

    // ── Confirm ───────────────────────────────────────────────────────────────

    /// <summary>
    /// POSTs corrected shifts to /confirm.
    /// On network failure, flushes to SQLite for retry.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> ConfirmAsync(List<ShiftData> shifts, CancellationToken ct = default)
    {
        try
        {
            var client  = await BuildClientAsync(ct);
            var payload = new { shifts };
            var resp    = await client.PostAsJsonAsync("confirm", payload, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            // Flush to SQLite retry buffer
            var json = JsonSerializer.Serialize(shifts);
            db.PendingConfirmations.Add(new PendingConfirmation { ShiftsJson = json });
            await db.SaveChangesWithRetryAsync(ct);
            return false;
        }
    }

    /// <summary>
    /// Re-submits any queued confirmations that previously failed.
    /// Clears each entry on success.
    /// </summary>
    public async Task RetryPendingConfirmationsAsync(CancellationToken ct = default)
    {
        var pending = await db.PendingConfirmations
            .OrderBy(p => p.QueuedAt)
            .ToListAsync(ct);


        foreach (var item in pending)
        {
            try
            {
                var shifts = JsonSerializer.Deserialize<List<ShiftData>>(item.ShiftsJson, _json);
                if (shifts is null) continue;

                var client  = await BuildClientAsync(ct);
                var payload = new { shifts };
                var resp    = await client.PostAsJsonAsync("confirm", payload, ct);
                if (resp.IsSuccessStatusCode)
                {
                    db.PendingConfirmations.Remove(item);
                    await db.SaveChangesWithRetryAsync(ct);
                }
            }
            catch
            {
                // Leave in queue; try again next time
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpClient> BuildClientAsync(CancellationToken ct)
    {
        var baseUrl = await serverDiscovery.GetBaseUrlAsync(ct);
        var prefs   = await db.GetPreferencesAsync(ct);

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Server URL is not configured.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUri))
            throw new InvalidOperationException("Server URL is invalid.");

        var client = httpClientFactory.CreateClient("CalendarParseApi");
        client.BaseAddress = new Uri(parsedBaseUri.ToString().TrimEnd('/') + "/");
        client.Timeout     = TimeSpan.FromSeconds(600); // LLM calls can take ~120s

        if (!string.IsNullOrWhiteSpace(prefs.ServerKey))
            client.DefaultRequestHeaders.Add("X-CalendarParse-Key", prefs.ServerKey);

        return client;
    }

    private class ProcessResponseBody
    {
        public List<ShiftData>? Shifts      { get; set; }
        public string?          Error       { get; set; }
        public int              ImageWidth  { get; set; }
        public int              ImageHeight { get; set; }
    }

    private class SubmitResponseBody
    {
        public string JobId { get; set; } = string.Empty;
    }

    private class JobStatusBody
    {
        public string  Status { get; set; } = string.Empty;
        public string? Error  { get; set; }
    }
}

