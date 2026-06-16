using TgAutoposter.Domain.Common;

namespace TgAutoposter.Api.Contracts;

public sealed record SourceResponse(
    Guid Id,
    Guid ChannelId,
    string Name,
    SourceKind Kind,
    string? Url,
    bool IsEnabled,
    int CheckEveryMinutes,
    string? Subreddit,
    RedditListingKind RedditListing,
    int MinimumScore,
    int MinimumComments,
    string? AllowedPublicationKindsCsv,
    DateTimeOffset? LastCheckedAtUtc);

public sealed record UpsertSourceRequest(
    string Name,
    SourceKind Kind,
    bool IsEnabled,
    int CheckEveryMinutes,
    string? Url,
    string? Subreddit,
    RedditListingKind RedditListing,
    int MinimumScore,
    int MinimumComments,
    string? WhitelistKeywordsCsv,
    string? BlacklistKeywordsCsv,
    string? AllowedPublicationKindsCsv,
    bool AllowNsfw,
    bool AllowRumors);
