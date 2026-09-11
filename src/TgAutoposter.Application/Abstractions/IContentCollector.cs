using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Application.Abstractions;

/// <summary>Facade used by the pipeline: dispatches to the right <see cref="ISourceCollector"/> by <see cref="Source.Kind"/>.</summary>
public interface IContentCollector
{
    Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken);
}

/// <summary>One implementation per source kind (Reddit, RSS/Web, AI web search, YouTube, Telegram, X...).</summary>
public interface ISourceCollector
{
    IReadOnlyCollection<SourceKind> SupportedKinds { get; }

    Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken);
}

public sealed record CollectedCandidate(
    string Title,
    string? Url,
    string Summary,
    string? RawText,
    string? ImageUrl,
    int? Score,
    int? CommentsCount,
    DateTimeOffset FoundAtUtc,
    string? MetadataJson,
    decimal? ProviderCostAmount = null,
    string ProviderCostCurrency = "RUB",
    string? ProviderUsageMetadataJson = null,
    string? VideoUrl = null,
    IReadOnlyCollection<string>? MediaUrls = null,
    /// <summary>Stable id inside the source (reddit id, tweet id, video id, tg message id) for cheap dedup.</summary>
    string? ExternalId = null,
    /// <summary>Author / account handle when known (used for talent matching later).</summary>
    string? Author = null);
