namespace TgAutoposter.Infrastructure.Options;

/// <summary>
/// VK API access for reading public community walls (e.g. VtuN — the alive RU VTuber aggregator).
/// A service token (сервисный ключ доступа) from a standalone/mini app is enough to read open groups
/// via wall.get; groups.search needs a user token and is not used.
/// </summary>
public sealed class VkOptions
{
    public bool Enabled { get; set; } = true;
    public string? ServiceToken { get; set; }
    public string ApiVersion { get; set; } = "5.199";
    public string ApiBaseUrl { get; set; } = "https://api.vk.com/method";
    /// <summary>How many latest wall posts to pull per community each sweep.</summary>
    public int PostsPerCommunity { get; set; } = 30;
    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ServiceToken);
}
