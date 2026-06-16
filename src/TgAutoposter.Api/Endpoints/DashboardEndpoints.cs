using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", async (Guid? channelId, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.UtcNow;
            var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var monthStart = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), TimeSpan.Zero);
            var posts = db.Posts.AsQueryable();
            var sources = db.Sources.AsQueryable();
            var usage = db.AiUsageRecords.AsQueryable();

            if (channelId.HasValue)
            {
                posts = posts.Where(post => post.ChannelId == channelId.Value);
                sources = sources.Where(source => source.ChannelId == channelId.Value);
                usage = usage.Where(record => record.ChannelId == channelId.Value);
            }

            var publishedToday = await posts.CountAsync(post =>
                post.Status == PostStatus.Published && post.PublishedAtUtc >= dayStart,
                cancellationToken);

            var publishedMonth = await posts.CountAsync(post =>
                post.Status == PostStatus.Published && post.PublishedAtUtc >= monthStart,
                cancellationToken);

            var spendToday = await usage
                .Where(record => record.CreatedAtUtc >= dayStart && record.CostAmount != null)
                .SumAsync(record => record.CostAmount ?? 0, cancellationToken);

            var spendMonth = await usage
                .Where(record => record.CreatedAtUtc >= monthStart && record.CostAmount != null)
                .SumAsync(record => record.CostAmount ?? 0, cancellationToken);

            var providerSpendToday = await usage
                .Where(record => record.CreatedAtUtc >= dayStart && record.ProviderCostAmount != null)
                .SumAsync(record => record.ProviderCostAmount ?? 0, cancellationToken);

            var providerSpendMonth = await usage
                .Where(record => record.CreatedAtUtc >= monthStart && record.ProviderCostAmount != null)
                .SumAsync(record => record.ProviderCostAmount ?? 0, cancellationToken);

            var publishedAll = await posts.CountAsync(post => post.Status == PostStatus.Published, cancellationToken);
            var spendAll = await usage
                .Where(record => record.CostAmount != null)
                .SumAsync(record => record.CostAmount ?? 0, cancellationToken);
            var averagePublishedCost = publishedAll == 0 ? 0 : spendAll / publishedAll;

            var response = new DashboardResponse(
                channelId.HasValue ? 1 : await db.Channels.CountAsync(cancellationToken),
                await sources.CountAsync(source => source.IsEnabled, cancellationToken),
                await posts.CountAsync(post =>
                    post.ScheduledForUtc >= dayStart &&
                    post.ScheduledForUtc < dayStart.AddDays(1) &&
                    (post.Status == PostStatus.Scheduled || post.Status == PostStatus.WaitingModeration),
                    cancellationToken),
                await posts.CountAsync(post => post.Status == PostStatus.WaitingModeration, cancellationToken),
                publishedToday,
                publishedMonth,
                await posts.CountAsync(post => post.Status == PostStatus.Rejected, cancellationToken),
                await posts.CountAsync(post => post.DeduplicationStatus == DeduplicationStatus.Duplicate, cancellationToken),
                await posts.CountAsync(post => post.Status == PostStatus.PublishFailed, cancellationToken),
                spendToday,
                spendMonth,
                averagePublishedCost,
                providerSpendToday,
                providerSpendMonth);

            return Results.Ok(response);
        }).WithTags("Dashboard");

        return app;
    }
}
