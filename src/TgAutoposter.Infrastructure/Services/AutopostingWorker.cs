using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class AutopostingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> optionsAccessor,
    ILogger<AutopostingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled)
        {
            logger.LogInformation("Autoposting worker is disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = interval;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var pipeline = scope.ServiceProvider.GetRequiredService<IAutopostingPipeline>();
                var runOptions = new PipelineRunOptions(
                    PublishNewPostsImmediately: false,
                    MaxPostsToCreate: Math.Max(1, options.MaxPostsPerRun));

                var channelIds = await db.Channels
                    .Where(channel => channel.IsEnabled)
                    .Select(channel => channel.Id)
                    .ToListAsync(stoppingToken);

                foreach (var channelId in channelIds)
                {
                    await pipeline.RunForChannelAsync(channelId, runOptions, stoppingToken);
                }

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                // Exponential backoff capped at 30 min so a persistent failure (e.g. DB down)
                // doesn't hot-loop and flood the logs every interval.
                var backoffSeconds = Math.Min(30 * 60, 30 * (int)Math.Pow(2, Math.Min(consecutiveFailures - 1, 6)));
                delay = TimeSpan.FromSeconds(backoffSeconds);
                logger.LogError(
                    ex,
                    "Autoposting worker iteration failed ({FailureCount} in a row). Backing off for {Delay}.",
                    consecutiveFailures,
                    delay);
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
}
