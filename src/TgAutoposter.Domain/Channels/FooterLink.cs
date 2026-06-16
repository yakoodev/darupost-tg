using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Channels;

public sealed class FooterLink : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? PublicationKindsCsv { get; set; }
}
