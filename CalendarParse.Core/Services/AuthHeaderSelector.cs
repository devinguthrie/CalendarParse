namespace CalendarParse.Core.Services;

/// <summary>Which auth scheme was chosen for the outgoing request.</summary>
public enum AuthDecision
{
    /// <summary>Authorization: Bearer &lt;token&gt;</summary>
    Bearer,
    /// <summary>X-CalendarParse-Key: &lt;key&gt;</summary>
    ApiKey,
    /// <summary>No auth header — request will be sent unauthenticated.</summary>
    None,
    /// <summary>Network is unavailable; caller should surface an offline error.</summary>
    Offline,
}

/// <param name="Decision">The chosen scheme.</param>
/// <param name="Value">Token or API key value; null for <see cref="AuthDecision.None"/> and <see cref="AuthDecision.Offline"/>.</param>
public record AuthSelection(AuthDecision Decision, string? Value);

/// <summary>
/// Pure stateless helper that chooses how to authenticate an outgoing API request.
/// Priority: Bearer token > offline gate > API key > none.
/// </summary>
public static class AuthHeaderSelector
{
    public static async Task<AuthSelection> SelectAsync(
        IAccessTokenProvider tokenProvider,
        string?              apiKey)
    {
        var token = await tokenProvider.GetAccessTokenAsync();

        // Non-whitespace token wins regardless of API key presence
        if (!string.IsNullOrWhiteSpace(token))
            return new AuthSelection(AuthDecision.Bearer, token);

        // No token acquired; check if offline before falling back to API key
        if (tokenProvider.IsOffline)
            return new AuthSelection(AuthDecision.Offline, null);

        if (!string.IsNullOrWhiteSpace(apiKey))
            return new AuthSelection(AuthDecision.ApiKey, apiKey);

        return new AuthSelection(AuthDecision.None, null);
    }
}
