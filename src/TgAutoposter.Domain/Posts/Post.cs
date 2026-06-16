using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Domain.Posts;

public sealed class Post : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public Guid? PublicationTypeId { get; set; }
    public PublicationTypeSetting? PublicationType { get; set; }

    public Guid? SourceId { get; set; }
    public Source? Source { get; set; }

    public Guid? SourceCandidateId { get; set; }
    public SourceCandidate? SourceCandidate { get; set; }

    public PublicationKind PublicationKind { get; set; } = PublicationKind.News;
    public string? SourceUrl { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public string OriginalSummary { get; set; } = string.Empty;
    public string? RelatedSourcesJson { get; set; }
    public FactCheckStatus FactCheckStatus { get; set; } = FactCheckStatus.NotChecked;
    public string? FactCheckSummary { get; set; }
    public DeduplicationStatus DeduplicationStatus { get; set; } = DeduplicationStatus.NotChecked;
    public string? DeduplicationSummary { get; set; }
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public string? GeneratedText { get; set; }
    public string? FinalText { get; set; }
    public string? Header { get; set; }
    public string? Footer { get; set; }
    public string? ImagePath { get; set; }
    public string? MediaUrlsJson { get; set; }
    public string? VideoUrl { get; set; }
    public PostStatus Status { get; set; } = PostStatus.CandidateFound;
    public Guid? ApprovedByUserId { get; set; }
    public UserAccount? ApprovedByUser { get; set; }
    public Guid? RejectedByUserId { get; set; }
    public UserAccount? RejectedByUser { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? ScheduledForUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? TelegramMessageId { get; set; }
    public string? TelegramPostUrl { get; set; }
    public decimal? CostAmount { get; set; }
    public string CostCurrency { get; set; } = "USD";

    public List<PostVersion> Versions { get; set; } = [];
}
