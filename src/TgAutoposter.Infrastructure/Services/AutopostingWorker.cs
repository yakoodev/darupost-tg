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

        var delay = TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Autoposting worker iteration failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
