using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Channels;

public sealed class PublicationTypeSetting : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public PublicationKind Kind { get; set; } = PublicationKind.News;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public decimal FrequencyPerDay { get; set; } = 1;
    public int Priority { get; set; } = 100;
    public ModerationMode ModerationMode { get; set; } = ModerationMode.Manual;
    public string SystemPrompt { get; set; } = string.Empty;
    public string? TextTemplate { get; set; }
    public string? HeaderTemplate { get; set; }
    public string? FooterTemplate { get; set; }
    public bool RequiresPoster { get; set; }
    public MediaGenerationMode MediaMode { get; set; } = MediaGenerationMode.None;
    public bool CanUseSourceImage { get; set; } = true;
    public bool RequiresFactCheck { get; set; } = true;
    public FactCheckMode FactCheckMode { get; set; } = FactCheckMode.Soft;
    public RumorPolicy RumorPolicy { get; set; } = RumorPolicy.AllowWithLabel;
    public int MaxTextLength { get; set; } = 1000;
    public string? TimeWindowsJson { get; set; }
}
