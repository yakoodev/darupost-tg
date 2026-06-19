using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Channels;

public sealed class UserAccount : Entity
{
    public long? TelegramUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? TelegramUsername { get; set; }
    public string? Email { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>PBKDF2 hash of the login password. Null until a password is set.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Owner per ТЗ §4.1 — full access to every channel, bypasses per-channel role checks.</summary>
    public bool IsGlobalOwner { get; set; }

    public List<ChannelRole> Roles { get; set; } = [];
}
