using System.Security.Claims;
using CalendarParse.Auth;

namespace CalendarParse.Tests.Auth;

public class OwnershipPolicyTests
{
    // ── IsApiKeyUser ──────────────────────────────────────────────────────────

    [Fact]
    public void IsApiKeyUser_WithApiKeyAuthClaim_ReturnsTrue()
    {
        var user = MakeUser(("auth_type", "apikey"));
        Assert.True(OwnershipPolicy.IsApiKeyUser(user));
    }

    [Fact]
    public void IsApiKeyUser_WithJwtSubClaim_ReturnsFalse()
    {
        var user = MakeUser(("sub", "auth0|abc123"));
        Assert.False(OwnershipPolicy.IsApiKeyUser(user));
    }

    [Fact]
    public void IsApiKeyUser_NoClaims_ReturnsFalse()
    {
        var user = MakeUser();
        Assert.False(OwnershipPolicy.IsApiKeyUser(user));
    }

    // ── GetUserId ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetUserId_ApiKeyUser_ReturnsNull()
    {
        var user = MakeUser(("auth_type", "apikey"), ("sub", "auth0|abc123"));
        Assert.Null(OwnershipPolicy.GetUserId(user));
    }

    [Fact]
    public void GetUserId_JwtUserWithSub_ReturnsSub()
    {
        var user = MakeUser(("sub", "auth0|abc123"));
        Assert.Equal("auth0|abc123", OwnershipPolicy.GetUserId(user));
    }

    [Fact]
    public void GetUserId_JwtUserWithoutSub_ReturnsNull()
    {
        // Malformed JWT — no sub claim. Program.cs rejects this at /submit.
        var user = MakeUser(("email", "user@example.com"));
        Assert.Null(OwnershipPolicy.GetUserId(user));
    }

    // ── CanAccess ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanAccess_ApiKeyUser_AlwaysAllowed()
    {
        var user = MakeUser(("auth_type", "apikey"));
        Assert.True(OwnershipPolicy.CanAccess(user, "auth0|other"));
        Assert.True(OwnershipPolicy.CanAccess(user, "auth0|mine"));
        Assert.True(OwnershipPolicy.CanAccess(user, null));
    }

    [Fact]
    public void CanAccess_JwtUser_OwnsJob_Allowed()
    {
        var user = MakeUser(("sub", "auth0|abc123"));
        Assert.True(OwnershipPolicy.CanAccess(user, "auth0|abc123"));
    }

    [Fact]
    public void CanAccess_JwtUser_DoesNotOwnJob_Denied()
    {
        var user = MakeUser(("sub", "auth0|abc123"));
        Assert.False(OwnershipPolicy.CanAccess(user, "auth0|other-user"));
    }

    [Fact]
    public void CanAccess_JwtUser_JobUserIdNull_Allowed()
    {
        // Backward-compat: jobs submitted before auth existed have null UserId
        var user = MakeUser(("sub", "auth0|abc123"));
        Assert.True(OwnershipPolicy.CanAccess(user, null));
    }

    [Fact]
    public void CanAccess_JwtUserWithoutSub_JobHasUserId_Denied()
    {
        // JWT user without 'sub' claim cannot access a job that has an owner
        var user = MakeUser(("email", "user@example.com"));
        Assert.False(OwnershipPolicy.CanAccess(user, "auth0|abc123"));
    }

    [Fact]
    public void CanAccess_JwtUserWithoutSub_JobUserIdNull_Allowed()
    {
        // Pre-auth job (no UserId) is still accessible to anyone
        var user = MakeUser(("email", "user@example.com"));
        Assert.True(OwnershipPolicy.CanAccess(user, null));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ClaimsPrincipal MakeUser(params (string type, string value)[] claims)
    {
        var claimObjects = claims.Select(c => new Claim(c.type, c.value));
        return new ClaimsPrincipal(new ClaimsIdentity(claimObjects, "Test"));
    }
}
