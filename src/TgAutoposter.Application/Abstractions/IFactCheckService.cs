using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Application.Abstractions;

public interface IFactCheckService
{
    Task<FactCheckResult> CheckAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken);
}

public sealed record FactCheckResult(FactCheckStatus Status, string Summary);
