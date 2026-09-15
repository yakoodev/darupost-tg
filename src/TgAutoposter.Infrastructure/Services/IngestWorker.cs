using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// Runs collectors for every due source of every enabled channel and stores candidates.
/// Independent from <see cref="AutopostingWorker"/> (generation) so parsing keeps going all day
/// regardless of post limits, and the evening digest has a full picture.
/// </summary>
public sealed class IngestWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestOptions> optionsAccessor,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled)
        {
            logger.LogInformation("Ingest worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));
        var consecutiveFailures = 0;

        // Give the API a moment to finish migrations/seeding before the first sweep.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = interval;
            try
            {
                await RunSweepAsync(stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var backoffSeconds = Math.Min(30 * 60, 30 * (int)Math.Pow(2, Math.Min(consecutiveFailures - 1, 6)));
                delay = TimeSpan.FromSeconds(backoffSeconds);
                logger.LogError(ex, "Ingest sweep failed ({FailureCount} in a row). Backing off for {Delay}.", consecutiveFailures, delay);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profiles = scope.ServiceProvider.GetRequiredService<INicheProfileProvider>();
        var ingest = scope.ServiceProvider.GetRequiredService<CandidateIngestService>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var realtime = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
        var clustering = scope.ServiceProvider.GetRequiredService<StoryClusteringService>();

        var channels = await db.Channels
            .Include(channel => channel.Sources)
            .Where(channel => channel.IsEnabled)
            .ToListAsync(cancellationToken);

        foreach (var channel in channels)
        {
            var profile = profiles.Get(channel.ProfileKey);
            var now = clock.UtcNow;
            var due = channel.Sources
                .Where(source => source.IsEnabled)
                .Where(source => source.LastCheckedAtUtc is null ||
                                 source.LastCheckedAtUtc.Value.AddMinutes(Math.Max(1, source.CheckEveryMinutes)) <= now)
                .OrderBy(source => source.LastCheckedAtUtc ?? DateTimeOffset.MinValue)
                .ToList();

            if (due.Count == 0)
            {
                continue;
            }

            var newTotal = 0;
            foreach (var source in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A single hung source (network, panel, retries) must not freeze the whole sweep for hours.
                using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sourceTimeout.CancelAfter(SourceTimeout);
                try
                {
                    var result = await ingest.IngestSourceAsync(channel, profile, source, sourceTimeout.Token);
                    newTotal += result.NewCandidates;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Ingest: source {Source} timed out after {Timeout} and was skipped.", source.Name, SourceTimeout);
                    source.LastCheckedAtUtc = clock.UtcNow;
                    source.LastError = $"Таймаут сбора ({SourceTimeout.TotalMinutes:0} мин)";
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            logger.LogInformation(
                "Ingest: channel {Channel} checked {Sources} source(s), {New} new candidate(s).",
                channel.Name,
                due.Count,
                newTotal);

            if (newTotal > 0)
            {
                try
                {
                    await clustering.ClusterPendingAsync(channel, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Story clustering failed for channel {Channel}.", channel.Name);
                }

                await realtime.StateChangedAsync("ingest", channel.Id, null, cancellationToken);
            }
        }
    }
}
