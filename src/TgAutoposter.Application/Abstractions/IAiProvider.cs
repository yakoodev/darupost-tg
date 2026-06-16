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
    bool RequireJson = false);

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
