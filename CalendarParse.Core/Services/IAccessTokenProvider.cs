namespace CalendarParse.Core.Services;

/// <summary>
/// Minimal token-provider abstraction used by the HTTP layer to attach auth headers.
/// Implemented by the MAUI IAuthService; also usable in tests via a simple fake.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>True when a network error prevented token refresh.</summary>
    bool IsOffline { get; }

    /// <summary>
    /// Returns a valid Bearer access token, refreshing silently if needed.
    /// Returns null when unauthenticated or when offline (IsOffline is set).
    /// </summary>
    Task<string?> GetAccessTokenAsync();
}
