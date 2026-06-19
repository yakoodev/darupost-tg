using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels/{channelId:guid}/sources").WithTags("Sources");

        group.MapGet("/", async (Guid channelId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var items = await db.Sources
                .Where(source => source.ChannelId == channelId)
                .OrderBy(source => source.Name)
                .Select(source => new SourceResponse(
                    source.Id,
                    source.ChannelId,
                    source.Name,
                    source.Kind,
                    source.Url,
                    source.IsEnabled,
                    source.CheckEveryMinutes,
                    source.Subreddit,
                    source.RedditListing,
                    source.MinimumScore,
                    source.MinimumComments,
                    source.AllowedPublicationKindsCsv,
                    source.LastCheckedAtUtc))
                .ToListAsync(cancellationToken);

            return Results.Ok(items);
        });

        group.MapPost("/", async (
            Guid channelId,
            UpsertSourceRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            if (!await db.Channels.AnyAsync(channel => channel.Id == channelId, cancellationToken))
            {
                return Results.NotFound();
            }

            var source = new Source { ChannelId = channelId };
            Apply(source, request);

            db.Sources.Add(source);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("source-created", channelId, null, cancellationToken);
            return Results.Created($"/api/channels/{channelId}/sources/{source.Id}", new { source.Id });
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        group.MapPut("/{sourceId:guid}", async (
            Guid channelId,
            Guid sourceId,
            UpsertSourceRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var source = await db.Sources.FirstOrDefaultAsync(source =>
                source.Id == sourceId && source.ChannelId == channelId,
                cancellationToken);

            if (source is null)
            {
                return Results.NotFound();
            }

            Apply(source, request);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("source-updated", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        return app;
    }

    private static void Apply(Source source, UpsertSourceRequest request)
    {
        source.Name = request.Name.Trim();
        source.Kind = request.Kind;
        source.IsEnabled = request.IsEnabled;
        source.CheckEveryMinutes = Math.Max(1, request.CheckEveryMinutes);
        source.Url = NormalizeOptional(request.Url);
        source.Subreddit = NormalizeOptional(request.Subreddit);
        source.RedditListing = request.RedditListing;
        source.MinimumScore = Math.Max(0, request.MinimumScore);
        source.MinimumComments = Math.Max(0, request.MinimumComments);
        source.WhitelistKeywordsCsv = NormalizeOptional(request.WhitelistKeywordsCsv);
        source.BlacklistKeywordsCsv = NormalizeOptional(request.BlacklistKeywordsCsv);
        source.AllowedPublicationKindsCsv = NormalizeOptional(request.AllowedPublicationKindsCsv);
        source.AllowNsfw = request.AllowNsfw;
        source.AllowRumors = request.AllowRumors;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
