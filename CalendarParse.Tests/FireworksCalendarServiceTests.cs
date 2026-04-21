using System.Net;
using CalendarParse.Parsing.Services;

namespace CalendarParse.Tests;

/// <summary>
/// Unit tests for <see cref="FireworksCalendarService"/>.
/// All HTTP calls are intercepted by <see cref="FakeHttpMessageHandler"/>.
/// </summary>
public class FireworksCalendarServiceTests
{
    // ── Constructor guards ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnEmptyApiKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new FireworksCalendarService(
                apiKey: "",
                model:  "accounts/fireworks/models/qwen3-vl-30b-a3b-instruct"));
    }

    [Fact]
    public void Constructor_ThrowsOnWhitespaceApiKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new FireworksCalendarService(
                apiKey: "   ",
                model:  "accounts/fireworks/models/qwen3-vl-30b-a3b-instruct"));
    }

    // ── EnsureModelLoadedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task EnsureModelLoaded_IsNoOp()
    {
        // Should complete without any HTTP traffic
        var svc = new FireworksCalendarService(
            apiKey: "fw_test",
            model:  "accounts/fireworks/models/qwen3-vl-30b-a3b-instruct");

        // If this method issued HTTP calls they'd fail; no exception = no calls
        await svc.EnsureModelLoadedAsync(CancellationToken.None);
    }

    // ── CallOllamaAsync happy path ────────────────────────────────────────────

    [Fact]
    public async Task CallOllamaAsync_HappyPath_ReturnsJsonFromChoices()
    {
        string expectedJson = """["9:00-5:30","x","x"]""";
        string responseBody = BuildChatCompletionBody(expectedJson);

        using var handler = new FakeHttpMessageHandler(
            statusCode: HttpStatusCode.OK,
            body: responseBody);

        var svc = createServiceWithHandler(handler);

        string result = await svc.CallOllamaAsync(
            base64Image: "AABB",
            prompt:      "What are the shifts?",
            ct:          CancellationToken.None,
            isJson:      true,
            numPredict:  -1);

        Assert.Equal(expectedJson, result);
    }

    [Fact]
    public async Task CallOllamaAsync_NonJson_ReturnsRawText()
    {
        const string expected = "Hello from the model";
        string responseBody = BuildChatCompletionBody(expected);

        using var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, responseBody);
        var svc = createServiceWithHandler(handler);

        string result = await svc.CallOllamaAsync(
            "AABB", "Say hello", CancellationToken.None, isJson: false);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CallOllamaAsync_JsonWithMarkdownFences_IsStripped()
    {
        string rawPayload = "```json\n[\"9:00-5:30\"]\n```";
        string responseBody = BuildChatCompletionBody(rawPayload);

        using var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, responseBody);
        var svc = createServiceWithHandler(handler);

        string result = await svc.CallOllamaAsync(
            "AABB", "shifts?", CancellationToken.None, isJson: true);

        Assert.Equal("""["9:00-5:30"]""", result);
    }

    // ── HTTP error handling ───────────────────────────────────────────────────

    [Fact]
    public async Task CallOllamaAsync_Unauthorized_ReturnsError()
    {
        using var handler = new FakeHttpMessageHandler(
            HttpStatusCode.Unauthorized,
            body: """{"error":"invalid api key"}""");
        var svc = createServiceWithHandler(handler);

        string result = await svc.CallOllamaAsync(
            "AABB", "test", CancellationToken.None);

        Assert.Contains("ERROR", result);
        Assert.Contains("401", result);
    }

    [Fact]
    public async Task CallOllamaAsync_RateLimit_RetriesAndReturnsLastRaw()
    {
        // All 3 attempts return 429
        using var handler = new FakeHttpMessageHandler(
            HttpStatusCode.TooManyRequests,
            body: """{"error":"rate limited"}""");
        var svc = createServiceWithHandler(handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string result = await svc.CallOllamaAsync(
            "AABB", "test", cts.Token);

        // After 3 failed attempts the last raw body is returned
        Assert.NotNull(result);
        Assert.Equal(3, handler.CallCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a service instance that uses the given handler for HTTP calls.</summary>
    private static FireworksCalendarService createServiceWithHandler(FakeHttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new FireworksCalendarService(
            apiKey:     "fw_test_key",
            model:      "accounts/fireworks/models/qwen3-vl-30b-a3b-instruct",
            knownNames: null,
            httpClient: client);
    }

    private static string BuildChatCompletionBody(string content) =>
        $$"""
        {
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": {{System.Text.Json.JsonSerializer.Serialize(content)}}
                    },
                    "finish_reason": "stop"
                }
            ]
        }
        """;
}

/// <summary>
/// Minimal test-double for <see cref="HttpMessageHandler"/> — always returns
/// the configured status code and body.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _body;
    private int _callCount;

    public int CallCount => _callCount;

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body       = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        System.Threading.Interlocked.Increment(ref _callCount);
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body,
                System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
