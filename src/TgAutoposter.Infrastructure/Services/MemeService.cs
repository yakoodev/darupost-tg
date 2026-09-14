using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed record MemeRunResult(Guid ChannelId, int CandidatesTried, Guid? PostId, string Note);

/// <summary>
/// Meme lane, separate from the news editor: at most <see cref="EditorialOptions.MemesPerDay"/> memes a day,
/// taken from the freshest image posts of meme-only sources and localized by the regular meme pipeline.
/// </summary>
public sealed class MemeService(
    AppDbContext db,
    IAutopostingPipeline pipeline,
    IDateTimeProvider clock,
    IOptions<EditorialOptions> optionsAccessor,
    ILogger<MemeService> logger)
{
    public async Task<MemeRunResult> RunAsync(Guid channelId, bool force, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (!force && options.MemesPerDay <= 0)
        {
            return new MemeRunResult(channelId, 0, null, "Мемы выключены (MemesPerDay = 0).");
        }

        var channel = await db.Channels
            .Include(item => item.ScheduleWindows)
            .Include(item => item.PublicationTypes)
            .FirstOrDefaultAsync(item => item.Id == channelId && item.IsEnabled, cancellationToken);
        if (channel is null)
        {
            return new MemeRunResult(channelId, 0, null, "Канал не найден или выключен.");
        }

        if (!channel.PublicationTypes.Any(type => type.Kind == PublicationKind.Meme && type.IsEnabled))
        {
            return new MemeRunResult(channel.Id, 0, null, "Тип публикации «Мем» выключен.");
        }

        var now = clock.UtcNow;
        var zone = EditorialService.ResolveTimeZone(channel.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        if (!force && options.RespectScheduleWindows && channel.ScheduleWindows.Count > 0 && !EditorialService.IsInsideWindow(channel, localNow))
        {
            return new MemeRunResult(channel.Id, 0, null, "Вне окон публикации.");
        }

        var localDayStart = localNow.Date;
        var dayStartUtc = new DateTimeOffset(localDayStart, zone.GetUtcOffset(localDayStart)).ToUniversalTime();
        var memesToday = await db.Posts.CountAsync(post =>
            post.ChannelId == channel.Id &&
            post.PublicationKind == PublicationKind.Meme &&
            post.CreatedAtUtc >= dayStartUtc &&
            post.Status != PostStatus.Duplicate &&
            post.Status != PostStatus.FactCheckFailed &&
            post.Status != PostStatus.Rejected, cancellationToken);
        if (!force && memesToday >= options.MemesPerDay)
        {
            return new MemeRunResult(channel.Id, 0, null, $"Сегодня уже есть мем ({memesToday}/{options.MemesPerDay}).");
        }

        var since = now.AddHours(-Math.Max(6, options.MemeLookbackHours));
        var candidates = await db.SourceCandidates
            .Where(candidate =>
                candidate.ChannelId == channel.Id &&
                !candidate.IsConsumed &&
                candidate.FoundAtUtc >= since &&
                candidate.ImageUrl != null &&
                candidate.Source != null &&
                candidate.Source.IsEnabled &&
                candidate.Source.AllowedPublicationKindsCsv != null &&
                EF.Functions.ILike(candidate.Source.AllowedPublicationKindsCsv, "%meme%"))
            .OrderByDescending(candidate => candidate.Score ?? 0)
            .ThenByDescending(candidate => candidate.FoundAtUtc)
            .Select(candidate => new { candidate.Id, candidate.Title })
            .Take(Math.Max(1, options.MemeAttemptsPerRun))
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return new MemeRunResult(channel.Id, 0, null, "Нет свежих мемов с картинкой в пуле.");
        }

        var tried = 0;
        foreach (var candidate in candidates)
        {
            tried++;
            var result = await pipeline.RunForChannelAsync(
                channel.Id,
                new PipelineRunOptions(
                    MaxPostsToCreate: 1,
                    BypassDailyLimit: true,
                    PublicationKind: PublicationKind.Meme,
                    CollectSources: false,
                    CandidateId: candidate.Id),
                cancellationToken);

            if (result.PostsCreated > 0)
            {
                var postId = await db.Posts
                    .Where(post => post.ChannelId == channel.Id && post.PublicationKind == PublicationKind.Meme)
                    .OrderByDescending(post => post.CreatedAtUtc)
                    .Select(post => (Guid?)post.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                logger.LogInformation("Meme lane: drafted meme {PostId} from candidate {CandidateId}.", postId, candidate.Id);
                return new MemeRunResult(channel.Id, tried, postId, $"Мем «{candidate.Title}» отправлен в работу.");
            }

            logger.LogInformation("Meme lane: candidate {CandidateId} did not become a post: {Warnings}", candidate.Id, string.Join("; ", result.Warnings));
        }

        return new MemeRunResult(channel.Id, tried, null, "Ни один мем не прошёл (дубли или ошибка перевода картинки).");
    }
}
