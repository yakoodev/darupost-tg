using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Posts;

namespace TgAutoposter.Application.Abstractions;

public interface IImageGenerator
{
    Task<ImageGenerationResult> GenerateForPostAsync(Channel channel, Post post, CancellationToken cancellationToken);
}

public sealed record ImageGenerationResult(
    string? ImageUrl,
    string Prompt,
    string Provider,
    string Model,
    decimal? CostAmount = null,
    string CostCurrency = "RUB",
    string? UsageMetadataJson = null,
    string? RawResponse = null);
