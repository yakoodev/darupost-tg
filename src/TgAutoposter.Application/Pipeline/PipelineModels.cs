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
    PublicationKind? PublicationKind = null);
