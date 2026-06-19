using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class PipelineEndpoints
{
    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pipeline").WithTags("Pipeline");

        group.MapPost("/channels/{channelId:guid}/run", async (
            Guid channelId,
            bool? publishNewPostsImmediately,
            int? maxPostsToCreate,
            bool? ignoreSourceSchedule,
            bool? bypassDailyLimit,
            PublicationKind? publicationKind,
            AppDbContext db,
            IAutopostingPipeline pipeline,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            if (!await db.Channels.AnyAsync(channel => channel.Id == channelId, cancellationToken))
            {
                return Results.NotFound();
            }

            var result = await pipeline.RunForChannelAsync(
                channelId,
                new PipelineRunOptions(
                    PublishNewPostsImmediately: publishNewPostsImmediately == true,
                    MaxPostsToCreate: maxPostsToCreate,
                    IgnoreSourceSchedule: ignoreSourceSchedule == true,
                    BypassDailyLimit: bypassDailyLimit == true,
                    PublicationKind: publicationKind),
                cancellationToken);

            await realtimeNotifier.StateChangedAsync("pipeline-run", channelId, null, cancellationToken);
            return Results.Ok(result);
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        return app;
    }
}
