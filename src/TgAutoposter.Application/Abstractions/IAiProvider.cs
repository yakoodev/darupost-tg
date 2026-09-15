using TgAutoposter.Domain.Common;

namespace TgAutoposter.Application.Abstractions;

public interface IAiProvider
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

public sealed record AiRequest(
    Guid ChannelId,
    AiTaskType TaskType,
    string SystemPrompt,
    string UserPrompt,
    string? Model = null,
    bool RequireJson = false,
    /// <summary>Override the provider's default completion budget (long structured outputs like the digest plan).</summary>
    int? MaxTokens = null,
    /// <summary>Per-request sampling temperature (judge ~0.2, writer ~0.7); null uses the provider default.</summary>
    double? Temperature = null);

public sealed record AiResponse(
    string Text,
    string Provider,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    decimal? CostAmount = null,
    string CostCurrency = "USD",
    string? UsageMetadataJson = null,
    string? RawResponse = null);
