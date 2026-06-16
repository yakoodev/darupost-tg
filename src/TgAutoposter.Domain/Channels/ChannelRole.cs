using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Channels;

public sealed class ChannelRole : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public Guid UserAccountId { get; set; }
    public UserAccount? UserAccount { get; set; }

    public ChannelRoleType Role { get; set; } = ChannelRoleType.Moderator;
}
