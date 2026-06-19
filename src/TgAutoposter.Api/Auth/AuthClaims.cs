using System.Security.Claims;

namespace TgAutoposter.Api.Auth;

public static class AuthClaims
{
    public const string GlobalOwner = "global_owner";

    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static bool IsGlobalOwner(this ClaimsPrincipal principal)
        => principal.HasClaim(GlobalOwner, "true");

    public static string GetDisplayName(this ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.Name) ?? "Unknown";
}
