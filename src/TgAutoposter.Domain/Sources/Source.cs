using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Sources;

public sealed class Source : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public string Name { get; set; } = string.Empty;
    public SourceKind Kind { get; set; } = SourceKind.Reddit;
    public string? Url { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int CheckEveryMinutes { get; set; } = 60;
    public string? AllowedPublicationKindsCsv { get; set; }

    public string? Subreddit { get; set; }
    public RedditListingKind RedditListing { get; set; } = RedditListingKind.Hot;
    public string? RedditTopPeriod { get; set; }
    public int MinimumScore { get; set; } = 50;
    public int MinimumComments { get; set; }
    public string? WhitelistKeywordsCsv { get; set; }
    public string? BlacklistKeywordsCsv { get; set; }
    public string Language { get; set; } = "en";
    public bool AllowNsfw { get; set; }
    public bool AllowRumors { get; set; }
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
}
