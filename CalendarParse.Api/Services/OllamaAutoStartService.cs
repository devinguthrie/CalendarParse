using System.Diagnostics;
using System.Net.Http;

namespace CalendarParse.Api.Services;

/// <summary>
/// On startup, ensures Ollama is running. If the configured OllamaBaseUrl is a
/// localhost address and Ollama isn't responding, this service spawns `ollama serve`
/// and waits for it to become ready.
///
/// Only active when OllamaApiStyle is "ollama" (skipped for RunPod mode).
/// Set CalendarParse:OllamaAutoStart=false to disable.
/// </summary>
public class OllamaAutoStartService : IHostedService
{
    private readonly string _ollamaBaseUrl;
    private readonly ILogger<OllamaAutoStartService> _logger;
    private Process? _spawnedProcess;

    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    public OllamaAutoStartService(string ollamaBaseUrl, ILogger<OllamaAutoStartService> logger)
    {
        _ollamaBaseUrl = ollamaBaseUrl;
        _logger        = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsLocalhost(_ollamaBaseUrl))
        {
            _logger.LogDebug("OllamaAutoStart: skipping — OllamaBaseUrl is not localhost.");
            return;
        }

        if (await IsOllamaReadyAsync())
        {
            _logger.LogInformation("OllamaAutoStart: Ollama already running at {Url}.", _ollamaBaseUrl);
            return;
        }

        _logger.LogInformation("OllamaAutoStart: Ollama not detected — starting `ollama serve`...");

        try
        {
            _spawnedProcess = Process.Start(new ProcessStartInfo("ollama", "serve")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
                CreateNoWindow         = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OllamaAutoStart: could not launch `ollama serve` — is Ollama installed and on PATH?");
            return;
        }

        // Poll until ready (up to 30 seconds)
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await IsOllamaReadyAsync())
            {
                _logger.LogInformation("OllamaAutoStart: Ollama is ready.");
                return;
            }
            await Task.Delay(1_000, cancellationToken);
        }

        _logger.LogWarning("OllamaAutoStart: Ollama did not become ready within 30 seconds. Continuing anyway.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Only shut down Ollama if this service spawned it.
        // If Ollama was already running before the API started, leave it alone.
        if (_spawnedProcess is { HasExited: false })
        {
            _logger.LogInformation("OllamaAutoStart: stopping spawned Ollama process.");
            try { _spawnedProcess.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "OllamaAutoStart: error stopping Ollama process."); }
            _spawnedProcess.Dispose();
            _spawnedProcess = null;
        }
        return Task.CompletedTask;
    }

    private async Task<bool> IsOllamaReadyAsync()
    {
        try
        {
            var resp = await _probe.GetAsync($"{_ollamaBaseUrl}/api/tags");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalhost(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("127.0.0.1") ||
        url.Contains("::1");
}
