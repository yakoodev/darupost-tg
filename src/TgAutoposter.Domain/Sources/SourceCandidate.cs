using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Sources;

public sealed class SourceCandidate : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public Guid SourceId { get; set; }
    public Source? Source { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? CanonicalUrl { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? RawText { get; set; }
    public string? ImageUrl { get; set; }
    public string? MediaUrlsJson { get; set; }
    public string? VideoUrl { get; set; }
    public int? Score { get; set; }
    public int? CommentsCount { get; set; }
    public DateTimeOffset FoundAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string NormalizedHash { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public bool IsConsumed { get; set; }
}
