namespace TgAutoposter.Infrastructure.Options;

public sealed class TelegramOptions
{
    public string? BotToken { get; set; }
    public string? ModerationChatId { get; set; }
    public string? ModeratorChatIdsCsv { get; set; }
    public ProxyOptions Proxy { get; set; } = new();

    public IReadOnlyList<string> GetModeratorChatIds()
    {
        var ids = new List<string>();
        AddIfPresent(ids, ModerationChatId);

        if (!string.IsNullOrWhiteSpace(ModeratorChatIdsCsv))
        {
            foreach (var value in ModeratorChatIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddIfPresent(ids, value);
            }
        }

        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddIfPresent(List<string> ids, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ids.Add(value.Trim());
        }
    }
}

public sealed class ProxyOptions
{
    public bool Enabled { get; set; }
    public string Type { get; set; } = "Http";
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
