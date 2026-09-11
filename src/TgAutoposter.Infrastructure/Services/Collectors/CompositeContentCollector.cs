using Microsoft.Extensions.Logging;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>Routes a source to the collector registered for its kind. Unknown kinds yield nothing (with a warning).</summary>
public sealed class CompositeContentCollector : IContentCollector
{
    private readonly Dictionary<SourceKind, ISourceCollector> _byKind;
    private readonly ILogger<CompositeContentCollector> _logger;

    public CompositeContentCollector(IEnumerable<ISourceCollector> collectors, ILogger<CompositeContentCollector> logger)
    {
        _logger = logger;
        _byKind = new Dictionary<SourceKind, ISourceCollector>();
        foreach (var collector in collectors)
        {
            foreach (var kind in collector.SupportedKinds)
            {
                _byKind[kind] = collector;
            }
        }
    }

    public bool Supports(SourceKind kind) => _byKind.ContainsKey(kind);

    public Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        if (_byKind.TryGetValue(source.Kind, out var collector))
        {
            return collector.CollectAsync(source, profile, cancellationToken);
        }

        _logger.LogWarning("No collector registered for source kind {Kind} (source {SourceId}).", source.Kind, source.Id);
        return Task.FromResult<IReadOnlyCollection<CollectedCandidate>>([]);
    }
}
