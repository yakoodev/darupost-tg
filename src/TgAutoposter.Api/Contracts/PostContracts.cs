using TgAutoposter.Domain.Common;

namespace TgAutoposter.Api.Contracts;

public sealed record PostResponse(
    Guid Id,
    Guid ChannelId,
    string ChannelName,
    PublicationKind PublicationKind,
    PostStatus Status,
    string SourceTitle,
    string? SourceUrl,
    string? VideoUrl,
    string? Model,
    string? FinalText,
    string? ImagePath,
    string? MediaUrlsJson,
    FactCheckStatus FactCheckStatus,
    string? FactCheckSummary,
    DeduplicationStatus DeduplicationStatus,
    string? DeduplicationSummary,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset? PublishedAtUtc,
    string? TelegramPostUrl,
    decimal? CostAmount,
    string CostCurrency,
    DateTimeOffset CreatedAtUtc);

public sealed record RejectPostRequest(string Reason);

public sealed record UpdatePostRequest(string FinalText, DateTimeOffset? ScheduledForUtc);

public sealed record GenerateDraftPostRequest(
    Guid ChannelId,
    PublicationKind PublicationKind,
    string SourceTitle,
    string? SourceUrl,
    string Summary,
    DateTimeOffset? ScheduledForUtc);
