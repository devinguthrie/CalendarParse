using Auth0.OidcClient;
using Microsoft.Extensions.Configuration;

namespace CalendarParse.Services;

public class Auth0AuthService : IAuthService
{
    private readonly Auth0Client _client;
    private readonly string _audience;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _tokenExpiry;
    private string? _userEmail;
    private string? _userName;

    private const string KeyAccessToken  = "auth0_access_token";
    private const string KeyRefreshToken = "auth0_refresh_token";
    private const string KeyTokenExpiry  = "auth0_token_expiry";
    private const string KeyUserEmail    = "auth0_user_email";
    private const string KeyUserName     = "auth0_user_name";

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) || !string.IsNullOrEmpty(_refreshToken);
    public bool IsOffline        { get; private set; }
    public string? UserEmail     => _userEmail;
    public string? UserName      => _userName;
    public string? LastLoginError { get; private set; }

    public Auth0AuthService(IConfiguration configuration)
    {
        var domain      = configuration["Auth0:Domain"]      ?? string.Empty;
        var clientId    = configuration["Auth0:ClientId"]    ?? string.Empty;
        var callbackUrl = configuration["Auth0:CallbackUrl"] ?? "com.companyname.calendarparse://callback";
        _audience       = configuration["Auth0:Audience"]    ?? string.Empty;

        _client = new Auth0Client(new Auth0ClientOptions
        {
            Domain              = domain,
            ClientId            = clientId,
            Scope               = "openid profile email offline_access",
            RedirectUri         = callbackUrl,
            PostLogoutRedirectUri = callbackUrl,
        });
    }

    public async Task RestoreSessionAsync()
    {
        try
        {
            _accessToken  = await SecureStorage.GetAsync(KeyAccessToken);
            _refreshToken = await SecureStorage.GetAsync(KeyRefreshToken);
            var expiryStr = await SecureStorage.GetAsync(KeyTokenExpiry);
            if (DateTimeOffset.TryParse(expiryStr, out var expiry))
                _tokenExpiry = expiry;
            _userEmail = await SecureStorage.GetAsync(KeyUserEmail);
            _userName  = await SecureStorage.GetAsync(KeyUserName);
        }
        catch
        {
            // Keystore reset or backup/restore failure — force re-login
            ClearLocalState();
            ClearSecureStorage();
        }
    }

    public async Task<bool> LoginAsync(bool signUp = false)
    {
        LastLoginError = null;
        try
        {
            var extraParams = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(_audience))
                extraParams["audience"] = _audience;
            if (signUp)
                extraParams["screen_hint"] = "signup";

            var result = await _client.LoginAsync(extraParams.Count > 0 ? extraParams : null);
            if (result.IsError)
            {
                LastLoginError = string.IsNullOrEmpty(result.ErrorDescription)
                    ? result.Error
                    : $"{result.Error}: {result.ErrorDescription}";
                System.Diagnostics.Debug.WriteLine($"[Auth0AuthService] LoginAsync failed: {LastLoginError}");
                return false;
            }

            _accessToken  = result.AccessToken;
            _refreshToken = result.RefreshToken;
            _tokenExpiry  = result.AccessTokenExpiration;
            _userEmail    = result.User?.FindFirst("email")?.Value;
            _userName     = result.User?.FindFirst("name")?.Value;
            IsOffline     = false;

            await PersistTokensAsync();
            return true;
        }
        catch (Exception ex)
        {
            LastLoginError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[Auth0AuthService] LoginAsync exception: {ex}");
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        try { await _client.LogoutAsync(); } catch { }
        ClearLocalState();
        ClearSecureStorage();
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_accessToken) && string.IsNullOrEmpty(_refreshToken))
            return null;

        // Token is still valid
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-1))
        {
            IsOffline = false;
            return _accessToken;
        }

        if (string.IsNullOrEmpty(_refreshToken))
            return null;

        // Serialize refresh attempts — prevents multiple in-flight refreshes racing
        await _refreshLock.WaitAsync();
        try
        {
            // Double-check inside the lock
            if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-1))
                return _accessToken;

            var result = await _client.RefreshTokenAsync(_refreshToken);

            if (result.IsError)
            {
                if (result.Error?.Contains("network", StringComparison.OrdinalIgnoreCase) == true)
                {
                    IsOffline = true;
                    return null;
                }
                // Auth failure (revoked, expired refresh token) — force re-login
                ClearLocalState();
                ClearSecureStorage();
                return null;
            }

            _accessToken = result.AccessToken;
            if (!string.IsNullOrEmpty(result.RefreshToken))
                _refreshToken = result.RefreshToken;
            _tokenExpiry = result.AccessTokenExpiration;
            IsOffline    = false;

            await PersistTokensAsync();
            return _accessToken;
        }
        catch (HttpRequestException)
        {
            IsOffline = true;
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task PersistTokensAsync()
    {
        try
        {
            await SecureStorage.SetAsync(KeyAccessToken,  _accessToken  ?? string.Empty);
            await SecureStorage.SetAsync(KeyRefreshToken, _refreshToken ?? string.Empty);
            await SecureStorage.SetAsync(KeyTokenExpiry,  _tokenExpiry.ToString("O"));
            await SecureStorage.SetAsync(KeyUserEmail,    _userEmail    ?? string.Empty);
            await SecureStorage.SetAsync(KeyUserName,     _userName     ?? string.Empty);
        }
        catch { /* Ignore storage failures — tokens remain in memory */ }
    }

    private void ClearLocalState()
    {
        _accessToken  = null;
        _refreshToken = null;
        _tokenExpiry  = default;
        _userEmail    = null;
        _userName     = null;
        IsOffline     = false;
    }

    private static void ClearSecureStorage()
    {
        try
        {
            SecureStorage.Remove(KeyAccessToken);
            SecureStorage.Remove(KeyRefreshToken);
            SecureStorage.Remove(KeyTokenExpiry);
            SecureStorage.Remove(KeyUserEmail);
            SecureStorage.Remove(KeyUserName);
        }
        catch { }
    }
}
