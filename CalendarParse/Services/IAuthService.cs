using CalendarParse.Core.Services;

namespace CalendarParse.Services;

public interface IAuthService : IAccessTokenProvider
{
    bool IsAuthenticated { get; }
    string? UserEmail { get; }
    string? UserName { get; }

    /// <summary>The human-readable error from the last failed login, or null on success.</summary>
    string? LastLoginError { get; }

    /// <summary>Loads tokens from SecureStorage. Fast — no network call.</summary>
    Task RestoreSessionAsync();

    /// <summary>
    /// Opens Auth0 login via system browser (PKCE). Returns true on success.
    /// Pass signUp=true to pre-select the Sign Up tab in Auth0 Universal Login.
    /// </summary>
    Task<bool> LoginAsync(bool signUp = false);

    /// <summary>Clears local tokens and calls Auth0 logout endpoint.</summary>
    Task LogoutAsync();
}
