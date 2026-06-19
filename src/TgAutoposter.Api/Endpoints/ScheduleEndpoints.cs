using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels/{channelId:guid}/schedule").WithTags("Schedule");

        group.MapGet("/", async (Guid channelId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var windows = await db.ScheduleWindows
                .AsNoTracking()
                .Where(window => window.ChannelId == channelId)
                .OrderBy(window => window.DayOfWeek)
                .ThenBy(window => window.StartTime)
                .Select(window => new ScheduleWindowResponse(
                    window.Id,
                    window.DayOfWeek == null ? null : (int)window.DayOfWeek.Value,
                    window.StartTime.ToString("HH:mm"),
                    window.EndTime.ToString("HH:mm"),
                    window.MinimumIntervalMinutes,
                    window.AllowBreakingNewsBypass))
                .ToListAsync(cancellationToken);

            return Results.Ok(windows);
        });

        group.MapPut("/", async (
            Guid channelId,
            UpdateScheduleRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            if (!await db.Channels.AnyAsync(channel => channel.Id == channelId, cancellationToken))
            {
                return Results.NotFound();
            }

            var parsed = new List<ScheduleWindow>();
            foreach (var window in request.Windows ?? [])
            {
                if (!TimeOnly.TryParse(window.StartTime, out var start) || !TimeOnly.TryParse(window.EndTime, out var end))
                {
                    return Results.BadRequest(new { error = "Время должно быть в формате HH:mm." });
                }

                if (end <= start)
                {
                    return Results.BadRequest(new { error = "Конец окна должен быть позже начала." });
                }

                if (window.DayOfWeek is < 0 or > 6)
                {
                    return Results.BadRequest(new { error = "День недели должен быть от 0 (вс) до 6 (сб) или пустым." });
                }

                parsed.Add(new ScheduleWindow
                {
                    ChannelId = channelId,
                    DayOfWeek = window.DayOfWeek is null ? null : (DayOfWeek)window.DayOfWeek.Value,
                    StartTime = start,
                    EndTime = end,
                    MinimumIntervalMinutes = Math.Max(0, window.MinimumIntervalMinutes),
                    AllowBreakingNewsBypass = window.AllowBreakingNewsBypass
                });
            }

            var existing = await db.ScheduleWindows
                .Where(window => window.ChannelId == channelId)
                .ToListAsync(cancellationToken);

            db.ScheduleWindows.RemoveRange(existing);
            db.ScheduleWindows.AddRange(parsed);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("schedule-updated", channelId, null, cancellationToken);
            return Results.NoContent();
        }).RequireChannelRole(ChannelRoleType.ChannelAdmin, "channelId");

        return app;
    }
}
