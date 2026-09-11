using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;
using TgAutoposter.Infrastructure.Services;

namespace TgAutoposter.Api.Endpoints;

/// <summary>"Сегодня": everything the ingest phase collected, per source, with actions to turn an item into a post.</summary>
public static class CandidateEndpoints
{
    public sealed record CandidateItemResponse(
        Guid Id,
        Guid SourceId,
        string SourceName,
        SourceKind SourceKind,
        string Title,
        string? Url,
        string Summary,
        string? ImageUrl,
        string? VideoUrl,
        string? Author,
        int? Score,
        int? CommentsCount,
        DateTimeOffset FoundAtUtc,
        DateTimeOffset CreatedAtUtc,
        bool IsConsumed,
        string? ConsumedReason);

    public sealed record CandidateSourceSummary(
        Guid SourceId,
        string Name,
        SourceKind Kind,
        bool IsEnabled,
        int Total,
        int Pending,
        DateTimeOffset? LastCheckedAtUtc,
        int LastCollectedCount,
        string? LastError);

    public sealed record CandidateListResponse(
        int Hours,
        int Total,
        int Pending,
        IReadOnlyList<CandidateSourceSummary> Sources,
        IReadOnlyList<CandidateItemResponse> Items);

    public sealed record GenerateFromCandidateRequest(PublicationKind? PublicationKind, bool PublishImmediately = false);

    public static IEndpointRouteBuilder MapCandidateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels/{channelId:guid}/candidates").WithTags("Candidates");

        group.MapGet("/", async (
            Guid channelId,
            int? hours,
            bool? includeConsumed,
            Guid? sourceId,
            AppDbContext db,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var window = Math.Clamp(hours ?? 24, 1, 24 * 7);
            var since = clock.UtcNow.AddHours(-window);

            var query = db.SourceCandidates
                .AsNoTracking()
                .Where(candidate => candidate.ChannelId == channelId && candidate.CreatedAtUtc >= since);

            var sources = await db.Sources
                .AsNoTracking()
                .Where(source => source.ChannelId == channelId)
                .OrderBy(source => source.Name)
                .ToListAsync(cancellationToken);

            var perSource = await query
                .GroupBy(candidate => candidate.SourceId)
                .Select(grouping => new
                {
                    SourceId = grouping.Key,
                    Total = grouping.Count(),
                    Pending = grouping.Count(candidate => !candidate.IsConsumed)
                })
                .ToListAsync(cancellationToken);
            var perSourceMap = perSource.ToDictionary(item => item.SourceId);

            var sourceSummaries = sources
                .Select(source =>
                {
                    perSourceMap.TryGetValue(source.Id, out var stats);
                    return new CandidateSourceSummary(
                        source.Id,
                        source.Name,
                        source.Kind,
                        source.IsEnabled,
                        stats?.Total ?? 0,
                        stats?.Pending ?? 0,
                        source.LastCheckedAtUtc,
                        source.LastCollectedCount,
                        source.LastError);
                })
                .ToList();

            if (includeConsumed != true)
            {
                query = query.Where(candidate => !candidate.IsConsumed);
            }

            if (sourceId is not null)
            {
                query = query.Where(candidate => candidate.SourceId == sourceId);
            }

            var sourceNames = sources.ToDictionary(source => source.Id, source => (source.Name, source.Kind));
            var items = await query
                .OrderByDescending(candidate => candidate.FoundAtUtc)
                .Take(500)
                .ToListAsync(cancellationToken);

            var responses = items.Select(candidate =>
            {
                sourceNames.TryGetValue(candidate.SourceId, out var source);
                return new CandidateItemResponse(
                    candidate.Id,
                    candidate.SourceId,
                    source.Name ?? "—",
                    source.Kind,
                    candidate.Title,
                    candidate.Url,
                    Trim(candidate.Summary, 600),
                    candidate.ImageUrl,
                    candidate.VideoUrl,
                    candidate.Author,
                    candidate.Score,
                    candidate.CommentsCount,
                    candidate.FoundAtUtc,
                    candidate.CreatedAtUtc,
                    candidate.IsConsumed,
                    candidate.ConsumedReason);
            }).ToList();

            return Results.Ok(new CandidateListResponse(
                window,
                perSource.Sum(item => item.Total),
                perSource.Sum(item => item.Pending),
                sourceSummaries,
                responses));
        });

        group.MapPost("/ingest", async (
            Guid channelId,
            AppDbContext db,
            INicheProfileProvider profiles,
            CandidateIngestService ingest,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels
                .Include(channel => channel.Sources)
                .FirstOrDefaultAsync(channel => channel.Id == channelId, cancellationToken);
            if (channel is null)
            {
                return Results.NotFound();
            }

            var profile = profiles.Get(channel.ProfileKey);
            var results = new List<IngestResult>();
            foreach (var source in channel.Sources.Where(source => source.IsEnabled).OrderBy(source => source.Name))
            {
                results.Add(await ingest.IngestSourceAsync(channel, profile, source, cancellationToken));
            }

            await realtimeNotifier.StateChangedAsync("ingest", channelId, null, cancellationToken);
            return Results.Ok(results);
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        group.MapPost("/{candidateId:guid}/generate", async (
            Guid channelId,
            Guid candidateId,
            GenerateFromCandidateRequest? request,
            AppDbContext db,
            IAutopostingPipeline pipeline,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var exists = await db.SourceCandidates.AnyAsync(
                candidate => candidate.Id == candidateId && candidate.ChannelId == channelId,
                cancellationToken);
            if (!exists)
            {
                return Results.NotFound();
            }

            var result = await pipeline.RunForChannelAsync(
                channelId,
                new PipelineRunOptions(
                    PublishNewPostsImmediately: request?.PublishImmediately == true,
                    MaxPostsToCreate: 1,
                    IgnoreSourceSchedule: false,
                    BypassDailyLimit: true,
                    PublicationKind: request?.PublicationKind,
                    CollectSources: false,
                    CandidateId: candidateId),
                cancellationToken);

            await realtimeNotifier.StateChangedAsync("pipeline-run", channelId, null, cancellationToken);
            return Results.Ok(result);
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        group.MapPost("/{candidateId:guid}/dismiss", async (
            Guid channelId,
            Guid candidateId,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var candidate = await db.SourceCandidates.FirstOrDefaultAsync(
                candidate => candidate.Id == candidateId && candidate.ChannelId == channelId,
                cancellationToken);
            if (candidate is null)
            {
                return Results.NotFound();
            }

            candidate.IsConsumed = true;
            candidate.ConsumedReason = "dismissed";
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("candidate-dismissed", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        group.MapPost("/{candidateId:guid}/restore", async (
            Guid channelId,
            Guid candidateId,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var candidate = await db.SourceCandidates.FirstOrDefaultAsync(
                candidate => candidate.Id == candidateId && candidate.ChannelId == channelId,
                cancellationToken);
            if (candidate is null)
            {
                return Results.NotFound();
            }

            candidate.IsConsumed = false;
            candidate.ConsumedReason = null;
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("candidate-restored", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.Moderator, "channelId");

        return app;
    }

    private static string Trim(string value, int max)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= max ? normalized : $"{normalized[..max]}…";
    }
}
