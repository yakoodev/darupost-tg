using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>Fires each channel's evening digest once per local day at <c>Channel.DigestTimeLocal</c>.</summary>
public sealed class DigestWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DigestOptions> optionsAccessor,
    ILogger<DigestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled)
        {
            logger.LogInformation("Digest worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.CheckIntervalMinutes));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Digest sweep failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(DigestOptions options, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var now = clock.UtcNow;

        var channels = await db.Channels
            .AsNoTracking()
            .Where(channel => channel.IsEnabled && channel.DigestEnabled)
            .Select(channel => new { channel.Id, channel.Name, channel.TimeZone, channel.DigestTimeLocal, channel.LastDigestAtUtc })
            .ToListAsync(cancellationToken);

        foreach (var channel in channels)
        {
            var zone = ResolveTimeZone(channel.TimeZone);
            var localNow = TimeZoneInfo.ConvertTime(now, zone);
            var due = localNow.Date + channel.DigestTimeLocal.ToTimeSpan();
            if (localNow.DateTime < due)
            {
                continue;
            }

            if (localNow.DateTime > due.AddHours(Math.Max(1, options.LateWindowHours)))
            {
                continue; // missed today's window (downtime) — don't post a stale digest at night
            }

            if (channel.LastDigestAtUtc is not null &&
                TimeZoneInfo.ConvertTime(channel.LastDigestAtUtc.Value, zone).Date == localNow.Date)
            {
                continue;
            }

            logger.LogInformation("Digest: running for channel {Channel} ({LocalTime} local).", channel.Name, localNow.ToString("HH:mm"));
            var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
            var result = await digest.RunAsync(channel.Id, cancellationToken);
            logger.LogInformation(
                "Digest: channel {Channel} → post {PostId}, {Items} items, {Drafts} drafts, {Warnings} warning(s).",
                channel.Name,
                result.DigestPostId,
                result.DigestItems,
                result.DraftsCreated,
                result.Warnings.Count);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
