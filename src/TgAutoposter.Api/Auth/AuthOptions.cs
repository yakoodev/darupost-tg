namespace TgAutoposter.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public JwtOptions Jwt { get; set; } = new();

    /// <summary>Bootstrap owner credentials, used by the seeder when no owner exists yet.</summary>
    public string OwnerEmail { get; set; } = "owner@local";
    public string OwnerPassword { get; set; } = "changeme123";
    public string OwnerDisplayName { get; set; } = "Owner";
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "tg-autoposter";
    public string Audience { get; set; } = "tg-autoposter-admin";

    /// <summary>HMAC signing key. MUST be overridden via configuration in production.</summary>
    public string Key { get; set; } = "dev-only-insecure-signing-key-change-me-please-32+chars";

    public int AccessTokenHours { get; set; } = 12;
}
