namespace TgAutoposter.Infrastructure.Options;

/// <summary>
/// Optional Reddit OAuth "script" app (free): with ClientId/ClientSecret the collector uses oauth.reddit.com,
/// which is far more tolerant than the anonymous JSON endpoint (403/429 from many IPs).
/// </summary>
public sealed class RedditOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string UserAgent { get; set; } = "windows:tg-autoposter:v0.2 (news aggregator)";
    /// <summary>When anonymous access is blocked and wspanel is configured, fetch the listing through the remote browser.</summary>
    public bool UseWspanelFallback { get; set; } = true;
}
