using System.Security.Claims;

namespace CalendarParse.Auth;

/// <summary>
/// Pure static helpers for API key detection and per-job ownership enforcement.
/// Lives in CalendarParse.Core so it can be tested without an ASP.NET pipeline dependency.
/// </summary>
public static class OwnershipPolicy
{
    /// <summary>Returns true when the request was authenticated via the API key scheme (CLI).</summary>
    public static bool IsApiKeyUser(ClaimsPrincipal user) =>
        user.HasClaim("auth_type", "apikey");

    /// <summary>
    /// Returns the Auth0 'sub' claim value for JWT users, or null for API key users.
    /// Returns null when no 'sub' claim is present (malformed token).
    /// </summary>
    public static string? GetUserId(ClaimsPrincipal user) =>
        IsApiKeyUser(user) ? null : user.FindFirst("sub")?.Value;

    /// <summary>
    /// Returns true when the authenticated principal is allowed to read or modify a job.
    /// Rules:
    /// <list type="bullet">
    ///   <item>API key users (CLI) can access any job.</item>
    ///   <item>Jobs with no UserId (submitted before auth was added) are accessible to everyone.</item>
    ///   <item>JWT users may only access jobs whose UserId matches their own 'sub' claim.</item>
    /// </list>
    /// </summary>
    public static bool CanAccess(ClaimsPrincipal user, string? jobUserId)
    {
        if (IsApiKeyUser(user))  return true;   // CLI bypasses ownership
        if (jobUserId is null)   return true;   // pre-auth job — accessible to anyone
        var sub = user.FindFirst("sub")?.Value;
        return sub is not null && sub == jobUserId;
    }
}
