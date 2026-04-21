using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarParse.Parsing.Services;

/// <summary>
/// LLM backend that sends vision requests to Fireworks AI instead of local Ollama.
/// Extends <see cref="OllamaCalendarService"/> and overrides only the HTTP transport
/// methods — all pipeline logic (OCR, grid detection, voting, scrubbing) is unchanged.
///
/// Activation: pass <c>--fireworks</c> on the CLI.
/// API key:    set the <c>FIREWORKS_API_KEY</c> environment variable.
///
/// Model IDs:
///   Serverless (pay-per-token):
///     accounts/fireworks/models/qwen3-vl-30b-a3b-instruct      ($0.15/$0.60 per M)
///     accounts/fireworks/models/llama4-maverick-instruct-basic
///   On-demand (dedicated GPU, must deploy first):
///     accounts/fireworks/models/qwen2p5-vl-7b-instruct         (same weights as local qwen2.5vl:7b)
/// </summary>
public sealed class FireworksCalendarService : OllamaCalendarService
{
    private const string FireworksEndpoint =
        "https://api.fireworks.ai/inference/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public FireworksCalendarService(
        string apiKey,
        string model,
        IEnumerable<string>? knownNames = null)
        : this(apiKey, model, knownNames, httpClient: null) { }

    /// <summary>Test-only constructor that accepts a pre-configured <see cref="HttpClient"/>.</summary>
    internal FireworksCalendarService(
        string apiKey,
        string model,
        IEnumerable<string>? knownNames,
        HttpClient? httpClient)
        : base(model: model, knownNames: knownNames)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Fireworks API key must not be empty.", nameof(apiKey));

        _apiKey = apiKey;

        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    /// <inheritdoc/>
    internal override async Task EnsureModelLoadedAsync(CancellationToken ct)
    {
        // Fireworks models are always ready — no warm-up needed.
    }

    /// <inheritdoc/>
    internal override async Task<string> CallOllamaAsync(
        string base64Image, string prompt, CancellationToken ct, bool isJson = true, int numPredict = -1)
    {
        var requestBody = BuildRequest(_model, base64Image, prompt, numPredict);

        const int maxAttempts = 3;
        string lastRaw = "";

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1) await Task.Delay(3000 * (attempt - 1), ct); // 3 s, then 6 s

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _http.PostAsJsonAsync(
                    FireworksEndpoint, requestBody, ct);
            }
            catch (HttpRequestException ex)
            {
                return MakeError($"Fireworks not reachable.\n{ex.Message}");
            }
            catch (TaskCanceledException)
            {
                return MakeError("Fireworks request timed out.");
            }

            string rawBody = await httpResponse.Content.ReadAsStringAsync(ct);

            if (httpResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Rate-limited — retry after backoff
                lastRaw = rawBody;
                continue;
            }

            if (!httpResponse.IsSuccessStatusCode)
                return MakeError($"Fireworks HTTP {(int)httpResponse.StatusCode}: {Truncate(rawBody, 300)}");

            string modelText;
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                modelText = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";
            }
            catch { return MakeError($"Could not parse Fireworks response: {Truncate(rawBody, 300)}"); }

            var (cleaned, parsedOk) = ApplyScrubbing(modelText, isJson);
            if (parsedOk) return cleaned;
            lastRaw = cleaned;
        }

        return lastRaw;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static FireworksRequest BuildRequest(string model, string base64Image, string prompt, int numPredict)
    {
        var content = new List<FireworksContent>
        {
            new() { Type = "text", Text = prompt },
            new()
            {
                Type     = "image_url",
                ImageUrl = new FireworksImageUrl
                {
                    Url = $"data:image/jpeg;base64,{base64Image}"
                }
            }
        };

        return new FireworksRequest
        {
            Model           = model,
            Messages        = [new FireworksMessage { Role = "user", Content = content }],
            Temperature     = 0.0,
            Seed            = 42,
            Stream          = false,
            MaxTokens       = numPredict > 0 ? numPredict : 4096,
            ReasoningEffort = model.Contains("qwen3", StringComparison.OrdinalIgnoreCase) ? "none" : null
        };
    }

    // ── Request/response record types ─────────────────────────────────────────

    private sealed class FireworksRequest
    {
        [JsonPropertyName("model")]            public string Model { get; init; } = "";
        [JsonPropertyName("messages")]         public List<FireworksMessage> Messages { get; init; } = [];
        [JsonPropertyName("temperature")]      public double Temperature { get; init; }
        [JsonPropertyName("seed")]             public int Seed { get; init; }
        [JsonPropertyName("stream")]           public bool Stream { get; init; }
        [JsonPropertyName("max_tokens")]       public int MaxTokens { get; init; }
        [JsonPropertyName("reasoning_effort")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningEffort { get; init; }
    }

    private sealed class FireworksMessage
    {
        [JsonPropertyName("role")]    public string Role { get; init; } = "";
        [JsonPropertyName("content")] public List<FireworksContent> Content { get; init; } = [];
    }

    private sealed class FireworksContent
    {
        [JsonPropertyName("type")]      public string Type { get; init; } = "";
        [JsonPropertyName("text")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; init; }
        [JsonPropertyName("image_url")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FireworksImageUrl? ImageUrl { get; init; }
    }

    private sealed class FireworksImageUrl
    {
        [JsonPropertyName("url")] public string Url { get; init; } = "";
    }
}
