using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;
using TgAutoposter.Infrastructure.Profiles;

namespace TgAutoposter.Api.Endpoints;

public static class ProfileEndpoints
{
    public sealed record NicheProfileResponse(
        string Key,
        string DisplayName,
        string Language,
        string DefaultName,
        string Positioning,
        string SystemPrompt,
        string StyleGuide,
        int DailyPostLimit,
        int PublicationTypesCount,
        int SourcesCount);

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profiles").WithTags("Profiles").RequireAuthorization();

        group.MapGet("/", (INicheProfileProvider profiles) =>
        {
            var items = profiles.All()
                .Select(profile => new NicheProfileResponse(
                    profile.Key,
                    profile.DisplayName,
                    profile.Language,
                    profile.Channel.Name,
                    profile.Channel.Positioning,
                    profile.Channel.SystemPrompt,
                    profile.Channel.StyleGuide,
                    profile.Channel.DailyPostLimit,
                    profile.PublicationTypes.Count,
                    profile.Sources.Count))
                .ToList();

            return Results.Ok(items);
        });

        // Bring an existing channel up to date with its profile (new sources / talents / types shipped in the JSON).
        app.MapPost("/api/channels/{channelId:guid}/profile/sync", async (
            Guid channelId,
            AppDbContext db,
            INicheProfileProvider profiles,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels
                .Include(channel => channel.Sources)
                .Include(channel => channel.PublicationTypes)
                .Include(channel => channel.Talents)
                .FirstOrDefaultAsync(channel => channel.Id == channelId, cancellationToken);
            if (channel is null)
            {
                return Results.NotFound();
            }

            var result = ChannelProvisioner.SyncFromProfile(channel, profiles.Get(channel.ProfileKey));
            db.Sources.AddRange(result.NewSources);
            db.Talents.AddRange(result.NewTalents);
            db.PublicationTypes.AddRange(result.NewTypes);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("profile-synced", channelId, null, cancellationToken);
            // Counts only: the entity lists carry navigation cycles and must not be serialized.
            return Results.Ok(new { result.SourcesAdded, result.TalentsAdded, result.TypesAdded });
        }).WithTags("Profiles").RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        return app;
    }
}
