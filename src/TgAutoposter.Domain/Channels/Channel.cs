using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Domain.Channels;

public sealed class Channel : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? TelegramUsername { get; set; }
    public string? TelegramChatId { get; set; }
    public string? BotTokenSecretName { get; set; }
    public ChannelStatus Status { get; set; } = ChannelStatus.Draft;
    public string TimeZone { get; set; } = "Europe/Moscow";
    public string Language { get; set; } = "ru";
    public string Positioning { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string StyleGuide { get; set; } = string.Empty;
    public ModerationMode DefaultModerationMode { get; set; } = ModerationMode.Manual;
    public int DailyPostLimit { get; set; } = 6;
    public decimal? DailyAiBudgetLimit { get; set; }
    public bool IsEnabled { get; set; } = true;

    public List<Source> Sources { get; set; } = [];
    public List<PublicationTypeSetting> PublicationTypes { get; set; } = [];
    public List<FooterLink> FooterLinks { get; set; } = [];
    public List<ScheduleWindow> ScheduleWindows { get; set; } = [];
    public List<ChannelRole> Roles { get; set; } = [];
    public List<Post> Posts { get; set; } = [];
}
