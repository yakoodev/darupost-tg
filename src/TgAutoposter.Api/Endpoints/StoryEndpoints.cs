using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Stories;
using TgAutoposter.Infrastructure.Persistence;
using TgAutoposter.Infrastructure.Services;

namespace TgAutoposter.Api.Endpoints;

/// <summary>Stories (clustered candidates) and the evening digest.</summary>
public static class StoryEndpoints
{
    public sealed record StoryCandidateResponse(Guid Id, string SourceName, SourceKind SourceKind, string Title, string? Url, int? Score, DateTimeOffset FoundAtUtc, string? Author);

    public sealed record StoryResponse(
        Guid Id,
        string Title,
        string Summary,
        DateTimeOffset FirstSeenAtUtc,
        DateTimeOffset LastSeenAtUtc,
        int CandidatesCount,
        int SourcesCount,
        double Score,
        bool IsBreaking,
        string? TalentsCsv,
        PublicationKind? KindHint,
        StoryStatus Status,
        Guid? LeadCandidateId,
        Guid? PostId,
        Guid? DigestPostId,
        IReadOnlyList<StoryCandidateResponse> Candidates);

    public sealed record DigestStatusResponse(
        bool DigestEnabled,
        string DigestTimeLocal,
        int DigestMaxStories,
        int DigestMaxDrafts,
        DateTimeOffset? LastDigestAtUtc,
        int OpenStories);

    public sealed record GenerateFromStoryRequest(PublicationKind? PublicationKind);

    public static IEndpointRouteBuilder MapStoryEndpoints(this IEndpointRouteBuilder app)
    {
        var stories = app.MapGroup("/api/channels/{channelId:guid}/stories").WithTags("Stories");

        stories.MapGet("/", async (
            Guid channelId,
            int? hours,
            bool? includeClosed,
            AppDbContext db,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var window = Math.Clamp(hours ?? 24, 1, 24 * 7);
            var since = clock.UtcNow.AddHours(-window);

            var query = db.Stories
                .AsNoTracking()
                .Where(story => story.ChannelId == channelId && story.LastSeenAtUtc >= since);
            if (includeClosed != true)
            {
                query = query.Where(story => story.Status == StoryStatus.Open);
            }

            var items = await query
                .OrderByDescending(story => story.Score)
                .ThenByDescending(story => story.LastSeenAtUtc)
                .Take(200)
                .ToListAsync(cancellationToken);

            var ids = items.Select(story => story.Id).ToList();
            var candidates = await db.SourceCandidates
                .AsNoTracking()
                .Where(candidate => candidate.StoryId != null && ids.Contains(candidate.StoryId.Value))
                .Select(candidate => new { candidate.Id, StoryId = candidate.StoryId!.Value, candidate.SourceId, candidate.Title, candidate.Url, candidate.Score, candidate.FoundAtUtc, candidate.Author })
                .ToListAsync(cancellationToken);
            var sources = await db.Sources
                .AsNoTracking()
                .Where(source => source.ChannelId == channelId)
                .ToDictionaryAsync(source => source.Id, source => (source.Name, source.Kind), cancellationToken);

            var response = items.Select(story => new StoryResponse(
                story.Id,
                story.Title,
                story.Summary,
                story.FirstSeenAtUtc,
                story.LastSeenAtUtc,
                story.CandidatesCount,
                story.SourcesCount,
                Math.Round(story.Score, 1),
                story.IsBreaking,
                story.TalentsCsv,
                story.KindHint,
                story.Status,
                story.LeadCandidateId,
                story.PostId,
                story.DigestPostId,
                candidates
                    .Where(candidate => candidate.StoryId == story.Id)
                    .OrderByDescending(candidate => candidate.Id == story.LeadCandidateId)
                    .ThenByDescending(candidate => candidate.Score ?? 0)
                    .Take(8)
                    .Select(candidate =>
                    {
                        sources.TryGetValue(candidate.SourceId, out var source);
                        return new StoryCandidateResponse(candidate.Id, source.Name ?? "—", source.Kind, candidate.Title, candidate.Url, candidate.Score, candidate.FoundAtUtc, candidate.Author);
                    })
                    .ToList())).ToList();

            return Results.Ok(response);
        });

        stories.MapPost("/cluster", async (
            Guid channelId,
            AppDbContext db,
            StoryClusteringService clustering,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels.FirstOrDefaultAsync(channel => channel.Id == channelId, cancellationToken);
            if (channel is null)
            {
                return Results.NotFound();
            }

            var assigned = await clustering.ClusterPendingAsync(channel, cancellationToken);
            await realtimeNotifier.StateChangedAsync("stories-clustered", channelId, null, cancellationToken);
            return Results.Ok(new { assigned });
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        stories.MapPost("/{storyId:guid}/generate", async (
            Guid channelId,
            Guid storyId,
            GenerateFromStoryRequest? request,
            AppDbContext db,
            IAutopostingPipeline pipeline,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var story = await db.Stories.FirstOrDefaultAsync(story => story.Id == storyId && story.ChannelId == channelId, cancellationToken);
            if (story?.LeadCandidateId is null)
            {
                return Results.NotFound();
            }

            var result = await pipeline.RunForChannelAsync(
                channelId,
                new PipelineRunOptions(
                    MaxPostsToCreate: 1,
                    BypassDailyLimit: true,
                    PublicationKind: request?.PublicationKind ?? story.KindHint,
                    CollectSources: false,
                    CandidateId: story.LeadCandidateId),
                cancellationToken);

            if (result.PostsCreated > 0)
            {
                story.Status = StoryStatus.Drafted;
                await db.SaveChangesAsync(cancellationToken);
            }

            await realtimeNotifier.StateChangedAsync("pipeline-run", channelId, null, cancellationToken);
            return Results.Ok(result);
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        stories.MapPost("/{storyId:guid}/dismiss", async (
            Guid channelId,
            Guid storyId,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var story = await db.Stories.FirstOrDefaultAsync(story => story.Id == storyId && story.ChannelId == channelId, cancellationToken);
            if (story is null)
            {
                return Results.NotFound();
            }

            story.Status = StoryStatus.Dismissed;
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("story-dismissed", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        stories.MapPost("/{storyId:guid}/reopen", async (
            Guid channelId,
            Guid storyId,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var story = await db.Stories.FirstOrDefaultAsync(story => story.Id == storyId && story.ChannelId == channelId, cancellationToken);
            if (story is null)
            {
                return Results.NotFound();
            }

            story.Status = StoryStatus.Open;
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("story-reopened", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        var digest = app.MapGroup("/api/channels/{channelId:guid}/digest").WithTags("Digest");

        digest.MapGet("/status", async (Guid channelId, AppDbContext db, IDateTimeProvider clock, CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(channel => channel.Id == channelId, cancellationToken);
            if (channel is null)
            {
                return Results.NotFound();
            }

            var since = clock.UtcNow.AddHours(-24);
            var open = await db.Stories.CountAsync(story => story.ChannelId == channelId && story.Status == StoryStatus.Open && story.LastSeenAtUtc >= since, cancellationToken);
            return Results.Ok(new DigestStatusResponse(
                channel.DigestEnabled,
                channel.DigestTimeLocal.ToString("HH:mm"),
                channel.DigestMaxStories,
                channel.DigestMaxDrafts,
                channel.LastDigestAtUtc,
                open));
        });

        digest.MapPost("/run", async (Guid channelId, DigestService service, CancellationToken cancellationToken) =>
        {
            var result = await service.RunAsync(channelId, cancellationToken);
            return Results.Ok(result);
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        return app;
    }
}
