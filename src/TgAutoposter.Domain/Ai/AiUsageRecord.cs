using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;

namespace TgAutoposter.Domain.Ai;

public sealed class AiUsageRecord : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public Guid? PostId { get; set; }
    public Post? Post { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public AiTaskType TaskType { get; set; } = AiTaskType.PostGeneration;
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public decimal? CostAmount { get; set; }
    public string CostCurrency { get; set; } = "USD";
    public decimal? ProviderCostAmount { get; set; }
    public string ProviderCostCurrency { get; set; } = "RUB";
    public string? RequestMetadataJson { get; set; }
}
