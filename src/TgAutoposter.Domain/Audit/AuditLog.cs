using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Audit;

public sealed class AuditLog : Entity
{
    public Guid? ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public Guid? UserAccountId { get; set; }
    public UserAccount? UserAccount { get; set; }

    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? DetailsJson { get; set; }
}
