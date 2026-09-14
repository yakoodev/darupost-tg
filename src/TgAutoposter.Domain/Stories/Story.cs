using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Domain.Stories;

/// <summary>
/// A story (сюжет): one real-world event seen through several candidates from different sources.
/// Built by the clustering pass over <see cref="SourceCandidate"/>; the evening digest and "probable posts" are picked from stories.
/// </summary>
public sealed class Story : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public int CandidatesCount { get; set; }
    public int SourcesCount { get; set; }
    /// <summary>Importance: distinct sources, engagement, breaking markers, recency.</summary>
    public double Score { get; set; }
    public bool IsBreaking { get; set; }
    /// <summary>Registry talents mentioned in the story (canonical names, comma-separated).</summary>
    public string? TalentsCsv { get; set; }
    public PublicationKind? KindHint { get; set; }
    /// <summary>Centroid embedding (JSON float array) of the attached candidates.</summary>
    public string? EmbeddingJson { get; set; }
    public StoryStatus Status { get; set; } = StoryStatus.Open;
    /// <summary>Best candidate to generate a standalone post from.</summary>
    public Guid? LeadCandidateId { get; set; }
    public Guid? PostId { get; set; }

    /// <summary>Editor's newsworthiness rating 0–10 (null = not rated yet).</summary>
    public int? EditorScore { get; set; }
    public string? EditorNote { get; set; }
    public string? EditorRubric { get; set; }
    public DateTimeOffset? EditorCheckedAtUtc { get; set; }
    /// <summary>Candidates count at the time of rating; the story is re-rated when it grows.</summary>
    public int EditorCandidatesCount { get; set; }
    public Guid? DigestPostId { get; set; }

    public List<SourceCandidate> Candidates { get; set; } = [];
}
