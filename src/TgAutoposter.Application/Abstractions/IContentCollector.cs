using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Application.Abstractions;

public interface IContentCollector
{
    Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, CancellationToken cancellationToken);
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
    IReadOnlyCollection<string>? MediaUrls = null);
