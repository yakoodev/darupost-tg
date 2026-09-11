using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Domain.Talents;
using TgAutoposter.Infrastructure.Persistence;
using TgAutoposter.Infrastructure.Services;

namespace TgAutoposter.Api.Endpoints;

/// <summary>Talent registry: who the channel covers, their aliases and official accounts.</summary>
public static class TalentEndpoints
{
    public sealed record TalentResponse(
        Guid Id,
        string Name,
        string? Agency,
        string? Group,
        string? AliasesCsv,
        int Priority,
        string? YouTube,
        string? Twitter,
        string? Telegram,
        bool TrackYouTube,
        bool TrackTwitter,
        bool IsActive,
        string? Notes);

    public sealed record UpsertTalentRequest(
        string Name,
        string? Agency,
        string? Group,
        string? AliasesCsv,
        int Priority,
        string? YouTube,
        string? Twitter,
        string? Telegram,
        bool TrackYouTube,
        bool TrackTwitter,
        bool IsActive,
        string? Notes);

    /// <summary>One talent per line: Name; Agency; Group; alias1, alias2; youtube; x</summary>
    public sealed record ImportTalentsRequest(string Text);

    public sealed record SyncSourcesResult(int Created, int Disabled, IReadOnlyList<string> Names);

    public static IEndpointRouteBuilder MapTalentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels/{channelId:guid}/talents").WithTags("Talents");

        group.MapGet("/", async (Guid channelId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var items = await db.Talents
                .AsNoTracking()
                .Where(talent => talent.ChannelId == channelId)
                .OrderBy(talent => talent.Priority)
                .ThenBy(talent => talent.Agency)
                .ThenBy(talent => talent.Name)
                .Select(talent => ToResponse(talent))
                .ToListAsync(cancellationToken);
            return Results.Ok(items);
        });

        group.MapPost("/", async (
            Guid channelId,
            UpsertTalentRequest request,
            AppDbContext db,
            TalentMatcher matcher,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            if (!await db.Channels.AnyAsync(channel => channel.Id == channelId, cancellationToken))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "Имя обязательно." });
            }

            var talent = new Talent { ChannelId = channelId };
            Apply(talent, request);
            db.Talents.Add(talent);
            await db.SaveChangesAsync(cancellationToken);
            matcher.Invalidate(channelId);
            await realtimeNotifier.StateChangedAsync("talent-created", channelId, null, cancellationToken);
            return Results.Created($"/api/channels/{channelId}/talents/{talent.Id}", ToResponse(talent));
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        group.MapPut("/{talentId:guid}", async (
            Guid channelId,
            Guid talentId,
            UpsertTalentRequest request,
            AppDbContext db,
            TalentMatcher matcher,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var talent = await db.Talents.FirstOrDefaultAsync(talent => talent.Id == talentId && talent.ChannelId == channelId, cancellationToken);
            if (talent is null)
            {
                return Results.NotFound();
            }

            Apply(talent, request);
            await db.SaveChangesAsync(cancellationToken);
            matcher.Invalidate(channelId);
            await realtimeNotifier.StateChangedAsync("talent-updated", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        group.MapDelete("/{talentId:guid}", async (
            Guid channelId,
            Guid talentId,
            AppDbContext db,
            TalentMatcher matcher,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var talent = await db.Talents.FirstOrDefaultAsync(talent => talent.Id == talentId && talent.ChannelId == channelId, cancellationToken);
            if (talent is null)
            {
                return Results.NotFound();
            }

            db.Talents.Remove(talent);
            await db.SaveChangesAsync(cancellationToken);
            matcher.Invalidate(channelId);
            await realtimeNotifier.StateChangedAsync("talent-deleted", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        group.MapPost("/import", async (
            Guid channelId,
            ImportTalentsRequest request,
            AppDbContext db,
            TalentMatcher matcher,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            if (!await db.Channels.AnyAsync(channel => channel.Id == channelId, cancellationToken))
            {
                return Results.NotFound();
            }

            var existing = await db.Talents
                .Where(talent => talent.ChannelId == channelId)
                .ToDictionaryAsync(talent => talent.Name.ToLowerInvariant(), talent => talent, cancellationToken);

            var created = 0;
            var updated = 0;
            foreach (var line in (request.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split(';').Select(part => part.Trim()).ToList();
                var name = parts.ElementAtOrDefault(0);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var key = name.ToLowerInvariant();
                if (!existing.TryGetValue(key, out var talent))
                {
                    talent = new Talent { ChannelId = channelId, Name = name };
                    db.Talents.Add(talent);
                    existing[key] = talent;
                    created++;
                }
                else
                {
                    updated++;
                }

                talent.Agency = Blank(parts.ElementAtOrDefault(1)) ?? talent.Agency;
                talent.Group = Blank(parts.ElementAtOrDefault(2)) ?? talent.Group;
                talent.AliasesCsv = Blank(parts.ElementAtOrDefault(3)) ?? talent.AliasesCsv;
                talent.YouTube = Blank(parts.ElementAtOrDefault(4)) ?? talent.YouTube;
                talent.Twitter = Blank(parts.ElementAtOrDefault(5)) ?? talent.Twitter;
            }

            await db.SaveChangesAsync(cancellationToken);
            matcher.Invalidate(channelId);
            await realtimeNotifier.StateChangedAsync("talents-imported", channelId, null, cancellationToken);
            return Results.Ok(new { created, updated });
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        group.MapPost("/sync-sources", async (
            Guid channelId,
            AppDbContext db,
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

            var talents = await db.Talents
                .AsNoTracking()
                .Where(talent => talent.ChannelId == channelId && talent.IsActive)
                .ToListAsync(cancellationToken);

            var created = 0;
            var disabled = 0;
            var names = new List<string>();
            var byKey = channel.Sources
                .Where(source => source.Kind is SourceKind.YouTube or SourceKind.Twitter && !string.IsNullOrWhiteSpace(source.Url))
                .GroupBy(source => $"{source.Kind}|{Normalize(source.Url!)}")
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var talent in talents)
            {
                foreach (var (kind, url, track) in new[]
                         {
                             (SourceKind.YouTube, talent.YouTube, talent.TrackYouTube),
                             (SourceKind.Twitter, talent.Twitter, talent.TrackTwitter)
                         })
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var key = $"{kind}|{Normalize(url)}";
                    byKey.TryGetValue(key, out var source);
                    if (track)
                    {
                        if (source is null)
                        {
                            source = new Source
                            {
                                ChannelId = channelId,
                                Name = $"{talent.Name} ({(kind == SourceKind.YouTube ? "YouTube" : "X")})",
                                Kind = kind,
                                Url = url.Trim(),
                                CheckEveryMinutes = kind == SourceKind.YouTube ? 30 : 20,
                                AllowedPublicationKindsCsv = "News,BreakingNews,Trailer,Deal,Digest",
                                RequireNewsSignal = false,
                                Language = "en",
                                IsEnabled = true
                            };
                            db.Sources.Add(source);
                            byKey[key] = source;
                            created++;
                            names.Add(source.Name);
                        }
                        else if (!source.IsEnabled)
                        {
                            source.IsEnabled = true;
                        }
                    }
                    else if (source is not null && source.IsEnabled && source.Name.StartsWith(talent.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        source.IsEnabled = false;
                        disabled++;
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("talent-sources-synced", channelId, null, cancellationToken);
            return Results.Ok(new SyncSourcesResult(created, disabled, names));
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        return app;
    }

    private static TalentResponse ToResponse(Talent talent) => new(
        talent.Id,
        talent.Name,
        talent.Agency,
        talent.Group,
        talent.AliasesCsv,
        talent.Priority,
        talent.YouTube,
        talent.Twitter,
        talent.Telegram,
        talent.TrackYouTube,
        talent.TrackTwitter,
        talent.IsActive,
        talent.Notes);

    private static void Apply(Talent talent, UpsertTalentRequest request)
    {
        talent.Name = request.Name.Trim();
        talent.Agency = Blank(request.Agency);
        talent.Group = Blank(request.Group);
        talent.AliasesCsv = Blank(request.AliasesCsv);
        talent.Priority = Math.Clamp(request.Priority, 1, 3);
        talent.YouTube = Blank(request.YouTube);
        talent.Twitter = Blank(request.Twitter);
        talent.Telegram = Blank(request.Telegram);
        talent.TrackYouTube = request.TrackYouTube;
        talent.TrackTwitter = request.TrackTwitter;
        talent.IsActive = request.IsActive;
        talent.Notes = Blank(request.Notes);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string url) => url.Trim().TrimEnd('/').ToLowerInvariant().Replace("https://", string.Empty).Replace("http://", string.Empty).Replace("www.", string.Empty).TrimStart('@');
}
