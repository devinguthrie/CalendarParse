using CalendarParse.Core.Services;

namespace CalendarParse.Tests.Auth;

public class AuthHeaderSelectorTests
{
    // ── Bearer token ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAsync_ValidToken_ReturnsBearerDecision()
    {
        var provider = new FakeTokenProvider("valid_token");
        var result   = await AuthHeaderSelector.SelectAsync(provider, "my-api-key");

        Assert.Equal(AuthDecision.Bearer, result.Decision);
        Assert.Equal("valid_token", result.Value);
    }

    [Fact]
    public async Task SelectAsync_ValidToken_BearerTakesPriorityOverApiKey()
    {
        var provider = new FakeTokenProvider("valid_token");
        var result   = await AuthHeaderSelector.SelectAsync(provider, "my-api-key");

        // API key should not be selected when a token is available
        Assert.Equal(AuthDecision.Bearer, result.Decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]  // whitespace-only token must NOT be used as Bearer
    public async Task SelectAsync_NullEmptyOrWhitespaceToken_DoesNotSelectBearer(string? token)
    {
        var provider = new FakeTokenProvider(token);
        var result   = await AuthHeaderSelector.SelectAsync(provider, "my-api-key");

        Assert.NotEqual(AuthDecision.Bearer, result.Decision);
    }

    // ── Offline ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAsync_OfflineWithNoToken_ReturnsOfflineDecision()
    {
        var provider = new FakeTokenProvider(token: null, isOffline: true);
        var result   = await AuthHeaderSelector.SelectAsync(provider, "my-api-key");

        Assert.Equal(AuthDecision.Offline, result.Decision);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task SelectAsync_OfflineWithNoToken_OfflineTakesPriorityOverApiKey()
    {
        var provider = new FakeTokenProvider(token: null, isOffline: true);
        var result   = await AuthHeaderSelector.SelectAsync(provider, "my-api-key");

        // Even with an API key available, offline blocks the request
        Assert.Equal(AuthDecision.Offline, result.Decision);
    }

    // ── API key ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAsync_NoTokenNotOffline_WithApiKey_ReturnsApiKeyDecision()
    {
        var provider = new FakeTokenProvider(token: null, isOffline: false);
        var result   = await AuthHeaderSelector.SelectAsync(provider, "my-api-key");

        Assert.Equal(AuthDecision.ApiKey, result.Decision);
        Assert.Equal("my-api-key", result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SelectAsync_NoTokenNotOffline_NullOrWhitespaceApiKey_ReturnsNoneDecision(string? apiKey)
    {
        var provider = new FakeTokenProvider(token: null, isOffline: false);
        var result   = await AuthHeaderSelector.SelectAsync(provider, apiKey);

        Assert.Equal(AuthDecision.None, result.Decision);
        Assert.Null(result.Value);
    }

    // ── None ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAsync_NoToken_NotOffline_NoApiKey_ReturnsNoneDecision()
    {
        var provider = new FakeTokenProvider(token: null, isOffline: false);
        var result   = await AuthHeaderSelector.SelectAsync(provider, null);

        Assert.Equal(AuthDecision.None, result.Decision);
        Assert.Null(result.Value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class FakeTokenProvider(string? token, bool isOffline = false) : IAccessTokenProvider
    {
        public bool IsOffline { get; } = isOffline;
        public Task<string?> GetAccessTokenAsync() => Task.FromResult(token);
    }
}
