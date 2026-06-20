using TgAutoposter.Domain.Common;

namespace TgAutoposter.Api.Contracts;

public sealed record ChannelListItemResponse(
    Guid Id,
    string Name,
    string? TelegramUsername,
    ChannelStatus Status,
    ModerationMode DefaultModerationMode,
    int DailyPostLimit,
    bool IsEnabled,
    int SourcesCount,
    int QueueCount);

public sealed record ChannelDetailsResponse(
    Guid Id,
    string Name,
    string? TelegramUsername,
    string? TelegramChatId,
    ChannelStatus Status,
    string TimeZone,
    string Language,
    string Positioning,
    string SystemPrompt,
    string StyleGuide,
    ModerationMode DefaultModerationMode,
    int DailyPostLimit,
    decimal? DailyAiBudgetLimit,
    bool IsEnabled);

public sealed record UpsertChannelRequest(
    string Name,
    string? TelegramUsername,
    string? TelegramChatId,
    string TimeZone,
    string Language,
    string Positioning,
    string SystemPrompt,
    string StyleGuide,
    ModerationMode DefaultModerationMode,
    int DailyPostLimit,
    decimal? DailyAiBudgetLimit,
    bool IsEnabled);

public sealed record SetAutopilotRequest(bool Enabled);

/// <summary>Three channel modes per ТЗ: Off (paused, no spend), Moderated (prepare drafts for review), Auto (auto-publish).</summary>
public sealed record SetChannelModeRequest(string Mode);

public sealed record PublicationTypeResponse(
    Guid Id,
    PublicationKind Kind,
    string Name,
    string Description,
    bool IsEnabled,
    int Priority,
    ModerationMode ModerationMode,
    FactCheckMode FactCheckMode,
    RumorPolicy RumorPolicy,
    int MaxTextLength,
    MediaGenerationMode MediaMode,
    string SystemPrompt,
    string? HeaderTemplate,
    string? FooterTemplate);

public sealed record UpdatePublicationTypeRequest(
    bool IsEnabled,
    int Priority,
    ModerationMode ModerationMode,
    FactCheckMode FactCheckMode,
    RumorPolicy RumorPolicy,
    int MaxTextLength,
    MediaGenerationMode MediaMode,
    string SystemPrompt,
    string? HeaderTemplate,
    string? FooterTemplate);

public sealed record FooterLinkResponse(
    Guid Id,
    string Label,
    string Url,
    int SortOrder,
    bool IsEnabled,
    string? PublicationKindsCsv);

public sealed record UpdateFooterLinksRequest(IReadOnlyCollection<FooterLinkRequest> Links);

public sealed record FooterLinkRequest(
    Guid? Id,
    string Label,
    string Url,
    int SortOrder,
    bool IsEnabled,
    string? PublicationKindsCsv);
