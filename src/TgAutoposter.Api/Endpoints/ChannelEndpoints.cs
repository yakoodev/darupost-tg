using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels").WithTags("Channels");

        group.MapGet("/", async (AppDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await db.Channels
                .OrderBy(channel => channel.Name)
                .Select(channel => new
                {
                    channel.Id,
                    channel.Name,
                    channel.TelegramUsername,
                    channel.Status,
                    channel.DefaultModerationMode,
                    channel.DailyPostLimit,
                    channel.IsEnabled,
                    SourcesCount = channel.Sources.Count(source => source.IsEnabled),
                    QueueCount = channel.Posts.Count(post => post.Status == PostStatus.WaitingModeration || post.Status == PostStatus.Scheduled)
                })
                .ToListAsync(cancellationToken);

            var items = rows
                .Select(channel => new ChannelListItemResponse(
                    channel.Id,
                    channel.Name,
                    channel.TelegramUsername,
                    channel.Status,
                    channel.DefaultModerationMode,
                    channel.DailyPostLimit,
                    channel.IsEnabled,
                    channel.SourcesCount,
                    channel.QueueCount))
                .ToList();

            return Results.Ok(items);
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels
                .Where(channel => channel.Id == id)
                .Select(channel => new ChannelDetailsResponse(
                    channel.Id,
                    channel.Name,
                    channel.TelegramUsername,
                    channel.TelegramChatId,
                    channel.Status,
                    channel.TimeZone,
                    channel.Language,
                    channel.Positioning,
                    channel.SystemPrompt,
                    channel.StyleGuide,
                    channel.DefaultModerationMode,
                    channel.DailyPostLimit,
                    channel.DailyAiBudgetLimit,
                    channel.IsEnabled))
                .FirstOrDefaultAsync(cancellationToken);

            return channel is null ? Results.NotFound() : Results.Ok(channel);
        });

        group.MapPost("/", async (
            UpsertChannelRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channel = new Channel();
            Apply(channel, request);
            channel.Status = string.IsNullOrWhiteSpace(request.TelegramUsername) && string.IsNullOrWhiteSpace(request.TelegramChatId)
                ? ChannelStatus.Draft
                : ChannelStatus.Connected;

            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("channel-created", channel.Id, null, cancellationToken);
            return Results.Created($"/api/channels/{channel.Id}", new { channel.Id });
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpsertChannelRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels.FirstOrDefaultAsync(channel => channel.Id == id, cancellationToken);
            if (channel is null)
            {
                return Results.NotFound();
            }

            Apply(channel, request);
            channel.Status = string.IsNullOrWhiteSpace(request.TelegramUsername) && string.IsNullOrWhiteSpace(request.TelegramChatId)
                ? ChannelStatus.Draft
                : ChannelStatus.Connected;

            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("channel-updated", channel.Id, null, cancellationToken);
            return Results.NoContent();
        });

        group.MapPut("/{id:guid}/autopilot", async (
            Guid id,
            SetAutopilotRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channel = await db.Channels
                .Include(channel => channel.PublicationTypes)
                .FirstOrDefaultAsync(channel => channel.Id == id, cancellationToken);

            if (channel is null)
            {
                return Results.NotFound();
            }

            channel.IsEnabled = true;
            channel.DefaultModerationMode = request.Enabled ? ModerationMode.Automatic : ModerationMode.Manual;

            foreach (var type in channel.PublicationTypes)
            {
                if (!request.Enabled)
                {
                    type.ModerationMode = ModerationMode.Manual;
                    continue;
                }

                if (type.Kind == PublicationKind.Rumor)
                {
                    type.ModerationMode = ModerationMode.Manual;
                    type.FactCheckMode = FactCheckMode.Medium;
                    type.RequiresFactCheck = true;
                    type.MediaMode = MediaGenerationMode.None;
                    continue;
                }

                type.ModerationMode = ModerationMode.Automatic;
                type.FactCheckMode = FactCheckMode.Soft;
                type.MediaMode = type.Kind == PublicationKind.Meme
                    ? MediaGenerationMode.TranslateMeme
                    : MediaGenerationMode.GeneratePoster;
            }

            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("autopilot-updated", channel.Id, null, cancellationToken);

            return Results.Ok(new ChannelDetailsResponse(
                channel.Id,
                channel.Name,
                channel.TelegramUsername,
                channel.TelegramChatId,
                channel.Status,
                channel.TimeZone,
                channel.Language,
                channel.Positioning,
                channel.SystemPrompt,
                channel.StyleGuide,
                channel.DefaultModerationMode,
                channel.DailyPostLimit,
                channel.DailyAiBudgetLimit,
                channel.IsEnabled));
        });

        group.MapGet("/{id:guid}/publication-types", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var items = await db.PublicationTypes
                .Where(type => type.ChannelId == id)
                .OrderByDescending(type => type.Priority)
                .Select(type => new PublicationTypeResponse(
                    type.Id,
                    type.Kind,
                    type.Name,
                    type.Description,
                    type.IsEnabled,
                    type.Priority,
                    type.ModerationMode,
                    type.FactCheckMode,
                    type.RumorPolicy,
                    type.MaxTextLength,
                    type.MediaMode,
                    type.SystemPrompt,
                    type.HeaderTemplate,
                    type.FooterTemplate))
                .ToListAsync(cancellationToken);

            return Results.Ok(items);
        });

        group.MapPut("/{channelId:guid}/publication-types/{typeId:guid}", async (
            Guid channelId,
            Guid typeId,
            UpdatePublicationTypeRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var publicationType = await db.PublicationTypes
                .FirstOrDefaultAsync(type => type.ChannelId == channelId && type.Id == typeId, cancellationToken);

            if (publicationType is null)
            {
                return Results.NotFound();
            }

            publicationType.IsEnabled = request.IsEnabled;
            publicationType.Priority = request.Priority;
            publicationType.ModerationMode = request.ModerationMode;
            publicationType.FactCheckMode = request.FactCheckMode;
            publicationType.RumorPolicy = request.RumorPolicy;
            publicationType.MaxTextLength = Math.Clamp(request.MaxTextLength, 280, 4096);
            publicationType.MediaMode = request.MediaMode;
            publicationType.SystemPrompt = request.SystemPrompt.Trim();
            publicationType.HeaderTemplate = NormalizeOptional(request.HeaderTemplate);
            publicationType.FooterTemplate = NormalizeOptional(request.FooterTemplate);

            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("publication-type-updated", channelId, null, cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/{id:guid}/footer-links", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var channelExists = await db.Channels.AnyAsync(channel => channel.Id == id, cancellationToken);
            if (!channelExists)
            {
                return Results.NotFound();
            }

            var items = await db.FooterLinks
                .Where(link => link.ChannelId == id)
                .OrderBy(link => link.SortOrder)
                .Select(link => new FooterLinkResponse(
                    link.Id,
                    link.Label,
                    link.Url,
                    link.SortOrder,
                    link.IsEnabled,
                    link.PublicationKindsCsv))
                .ToListAsync(cancellationToken);

            return Results.Ok(items);
        });

        group.MapPut("/{id:guid}/footer-links", async (
            Guid id,
            UpdateFooterLinksRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var channelExists = await db.Channels.AnyAsync(channel => channel.Id == id, cancellationToken);
            if (!channelExists)
            {
                return Results.NotFound();
            }

            IReadOnlyCollection<FooterLinkRequest> incomingLinks = request.Links ?? [];
            var normalized = incomingLinks
                .Where(link => !string.IsNullOrWhiteSpace(link.Label) || !string.IsNullOrWhiteSpace(link.Url))
                .Take(8)
                .Select((link, index) => new FooterLink
                {
                    ChannelId = id,
                    Label = link.Label.Trim(),
                    Url = link.Url.Trim(),
                    SortOrder = link.SortOrder == 0 ? (index + 1) * 10 : link.SortOrder,
                    IsEnabled = link.IsEnabled,
                    PublicationKindsCsv = NormalizeOptional(link.PublicationKindsCsv)
                })
                .ToList();

            var invalid = normalized.FirstOrDefault(link =>
                string.IsNullOrWhiteSpace(link.Label) ||
                string.IsNullOrWhiteSpace(link.Url) ||
                !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps));

            if (invalid is not null)
            {
                return Results.BadRequest("У каждой ссылки должны быть название и абсолютный URL.");
            }

            var existing = await db.FooterLinks
                .Where(link => link.ChannelId == id)
                .ToListAsync(cancellationToken);

            db.FooterLinks.RemoveRange(existing);
            db.FooterLinks.AddRange(normalized);

            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("footer-links-updated", id, null, cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static void Apply(Channel channel, UpsertChannelRequest request)
    {
        channel.Name = request.Name.Trim();
        channel.TelegramUsername = NormalizeOptional(request.TelegramUsername);
        channel.TelegramChatId = NormalizeOptional(request.TelegramChatId);
        channel.TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "Europe/Moscow" : request.TimeZone.Trim();
        channel.Language = string.IsNullOrWhiteSpace(request.Language) ? "ru" : request.Language.Trim();
        channel.Positioning = request.Positioning.Trim();
        channel.SystemPrompt = request.SystemPrompt.Trim();
        channel.StyleGuide = request.StyleGuide.Trim();
        channel.DefaultModerationMode = request.DefaultModerationMode;
        channel.DailyPostLimit = Math.Max(1, request.DailyPostLimit);
        channel.DailyAiBudgetLimit = request.DailyAiBudgetLimit;
        channel.IsEnabled = request.IsEnabled;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
