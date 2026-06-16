using TgAutoposter.Application.Pipeline;

namespace TgAutoposter.Application.Abstractions;

public interface IAutopostingPipeline
{
    Task<PipelineRunResult> RunForChannelAsync(
        Guid channelId,
        PipelineRunOptions options,
        CancellationToken cancellationToken);
}
