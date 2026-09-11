namespace TgAutoposter.Infrastructure.Options;

/// <summary>
/// wspanel — the user's remote-desktop panel with a logged-in Chrome. Its <c>POST /api/v1/read</c> renders pages
/// inside that browser (cookies, logins) and returns text/HTML. Used for X (Twitter) and, when enabled,
/// as the fetch path for Telegram previews (the panel lives abroad, so no RU proxy is needed).
/// </summary>
public sealed class WspanelOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>Bearer key: wsp_&lt;id&gt;_&lt;secret&gt; with the cdp.attach capability and scope profile:&lt;id&gt;.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Default desktop (profile) id, 16 hex chars. A source may override it in SettingsJson.</summary>
    public string? Profile { get; set; }
    public string? Program { get; set; } = "google-chrome";
    /// <summary>Extra wait after load, ms (X needs a few seconds to render the timeline).</summary>
    public int WaitMs { get; set; } = 4000;
    /// <summary>Read even when a human is working in the desktop right now.</summary>
    public bool Force { get; set; } = true;
    /// <summary>Skip TLS validation (panel is often reachable by raw IP with a self-signed cert).</summary>
    public bool InsecureTls { get; set; }
    public int TimeoutSeconds { get; set; } = 90;
    /// <summary>Also fetch Telegram previews (t.me/s) through the panel instead of a direct/proxied request.</summary>
    public bool UseForTelegram { get; set; } = true;
}
