using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Application.Abstractions;

public interface IDeduplicationService
{
    Task<DeduplicationResult> CheckAsync(SourceCandidate candidate, CancellationToken cancellationToken);
}

public sealed record DeduplicationResult(
    DeduplicationStatus Status,
    string Summary,
    Guid? MatchedPostId = null);
