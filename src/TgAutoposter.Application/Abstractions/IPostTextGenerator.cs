using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Application.Abstractions;

public interface IPostTextGenerator
{
    Task<PostTextResult> GenerateAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken,
        string? storyContext = null,
        /// <summary>Facts about the talents in the story from the channel's registry (agency, group, notes).</summary>
        string? talentFacts = null);
}

public sealed record PostTextResult(
    string Text,
    string Header,
    string Footer,
    string Prompt,
    string Provider,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    decimal? CostAmount = null,
    string CostCurrency = "USD",
    string? UsageMetadataJson = null,
    string? Headline = null);
