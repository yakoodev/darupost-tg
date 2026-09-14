using TgAutoposter.Domain.Common;

namespace TgAutoposter.Application.Pipeline;

public sealed record PipelineRunResult(
    Guid ChannelId,
    int SourcesChecked,
    int CandidatesCollected,
    int PostsCreated,
    int DuplicatesSkipped,
    int FactCheckFailed,
    int PublishedThisRun,
    int PublishFailed,
    IReadOnlyCollection<string> Warnings);

public sealed record PipelineRunOptions(
    bool PublishNewPostsImmediately = false,
    int? MaxPostsToCreate = null,
    bool IgnoreSourceSchedule = false,
    bool BypassDailyLimit = false,
    PublicationKind? PublicationKind = null,
    /// <summary>Run the ingest phase for due sources before generating. False = only consume what is already collected.</summary>
    bool CollectSources = true,
    /// <summary>Generate from this specific candidate only (manual "make a post" from the Today screen).</summary>
    Guid? CandidateId = null,
    /// <summary>Story the candidate belongs to: its other sources are passed to the writer and the story is marked drafted.</summary>
    Guid? StoryId = null);
