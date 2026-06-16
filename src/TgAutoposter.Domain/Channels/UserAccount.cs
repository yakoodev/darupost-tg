using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Channels;

public sealed class UserAccount : Entity
{
    public long? TelegramUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? TelegramUsername { get; set; }
    public string? Email { get; set; }
    public bool IsEnabled { get; set; } = true;

    public List<ChannelRole> Roles { get; set; } = [];
}
