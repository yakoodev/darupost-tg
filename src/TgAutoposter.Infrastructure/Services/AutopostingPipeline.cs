using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class AutopostingPipeline(
    AppDbContext db,
    IContentCollector collector,
    IDeduplicationService deduplicationService,
    IFactCheckService factCheckService,
    IPostTextGenerator postTextGenerator,
    IImageGenerator imageGenerator,
    IEmbeddingProvider embeddingProvider,
    IModerationNotifier moderationNotifier,
    ITelegramPublisher telegramPublisher,
    IDateTimeProvider clock,
    IRealtimeNotifier realtimeNotifier,
    ILogger<AutopostingPipeline> logger) : IAutopostingPipeline
{
    public async Task<PipelineRunResult> RunForChannelAsync(
        Guid channelId,
        PipelineRunOptions options,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var channel = await LoadChannelAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return new PipelineRunResult(channelId, 0, 0, 0, 0, 0, 0, 0, ["Канал не найден."]);
        }

        var initialPublish = await PublishDuePostsAsync(channel, cancellationToken);

        var sourcesToCheck = channel.Sources
            .Where(source => source.IsEnabled && (options.IgnoreSourceSchedule || IsDue(source)))
            .Where(source => options.PublicationKind is null || SourceAllowsKind(source, options.PublicationKind.Value))
            .OrderBy(source => options.PublicationKind.HasValue ? SourcePriority(source.Kind, options.PublicationKind.Value) : 0)
            .ThenBy(source => source.LastCheckedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(source => SourcePriority(source.Kind, null))
            .ThenBy(source => source.Name)
            .ToList();

        if (sourcesToCheck.Count > 0)
        {
            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = channel.Id,
                Provider = "fixed",
                Model = "news-research",
                TaskType = AiTaskType.StructuredOutput,
                CostAmount = AiCostDefaults.NewsResearchRub,
                CostCurrency = AiCostDefaults.Currency,
                RequestMetadataJson = $$"""
                {"sourcesToCheck":{{sourcesToCheck.Count}},"ignoreSourceSchedule":{{options.IgnoreSourceSchedule.ToString().ToLowerInvariant()}}}
                """
            });
        }

        var sourcesChecked = 0;
        var candidatesCollected = 0;
        var postsCreated = 0;
        var duplicatesSkipped = 0;
        var factCheckFailed = 0;
        var publishedThisRun = initialPublish.Published;
        var publishFailed = initialPublish.Failed;
        var dailyLimitReached = false;

        foreach (var source in sourcesToCheck)
        {
            if (HasCreatedEnough(options, postsCreated) || dailyLimitReached)
            {
                break;
            }

            IReadOnlyCollection<CollectedCandidate> collected;
            try
            {
                collected = await collector.CollectAsync(source, cancellationToken);
                sourcesChecked++;
                source.LastCheckedAtUtc = clock.UtcNow;
                AddCollectorProviderUsageIfPresent(channel, source, collected);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Source {SourceId} collection failed.", source.Id);
                warnings.Add($"Источник {source.Name}: ошибка сбора ({ex.Message}).");
                continue;
            }

            foreach (var item in collected)
            {
                if (HasCreatedEnough(options, postsCreated))
                {
                    break;
                }

                if (IsStale(item))
                {
                    warnings.Add($"Пропущен старый инфоповод: {item.Title}.");
                    continue;
                }

                if (!options.BypassDailyLimit && await IsDailyLimitReachedAsync(channel.Id, channel.DailyPostLimit, cancellationToken))
                {
                    warnings.Add($"Канал {channel.Name}: дневной лимит {channel.DailyPostLimit} постов достигнут.");
                    dailyLimitReached = true;
                    break;
                }

                var candidate = await UpsertCandidateAsync(channel.Id, source.Id, item, cancellationToken);
                if (candidate is null)
                {
                    continue;
                }

                candidatesCollected++;
                var publicationType = PickPublicationType(channel, source, candidate, options);
                if (publicationType is null)
                {
                    warnings.Add($"Канал {channel.Name}: не найден включённый тип публикации для кандидата {candidate.Title}.");
                    continue;
                }

                var deduplication = await deduplicationService.CheckAsync(candidate, cancellationToken);
                if (deduplication.Status == DeduplicationStatus.Duplicate)
                {
                    duplicatesSkipped++;
                    await CreateDuplicatePostAsync(channel, source, candidate, publicationType, deduplication, cancellationToken);
                    candidate.IsConsumed = true;
                    continue;
                }

                var factCheck = await factCheckService.CheckAsync(channel, publicationType, candidate, cancellationToken);
                if (factCheck.Status == FactCheckStatus.Failed ||
                    (channel.DefaultModerationMode == ModerationMode.Automatic && factCheck.Status != FactCheckStatus.Passed))
                {
                    factCheckFailed++;
                    await CreateFactCheckFailedPostAsync(channel, source, candidate, publicationType, factCheck, deduplication, cancellationToken);
                    candidate.IsConsumed = true;
                    continue;
                }

                var generated = await postTextGenerator.GenerateAsync(channel, publicationType, candidate, cancellationToken);
                var post = CreatePost(channel, source, candidate, publicationType, deduplication, factCheck, generated, options);
                post.EmbeddingJson = await ComputeEmbeddingJsonAsync(channel.Id, post, cancellationToken);

                if (ShouldGenerateImage(channel, publicationType, post))
                {
                    await GenerateImageAsync(channel, publicationType, post, warnings, cancellationToken);
                }

                db.Posts.Add(post);
                db.PostVersions.Add(new PostVersion
                {
                    Post = post,
                    VersionNumber = 1,
                    Text = generated.Text,
                    Prompt = generated.Prompt,
                    Model = generated.Model,
                    Reason = "initial-generation"
                });

                db.AiUsageRecords.Add(new AiUsageRecord
                {
                    ChannelId = channel.Id,
                    Post = post,
                    Provider = generated.Provider,
                    Model = generated.Model,
                    TaskType = AiTaskType.PostGeneration,
                    PromptTokens = generated.PromptTokens,
                    CompletionTokens = generated.CompletionTokens,
                    TotalTokens = generated.TotalTokens,
                    CostAmount = null,
                    CostCurrency = generated.CostCurrency,
                    ProviderCostAmount = generated.CostAmount,
                    ProviderCostCurrency = generated.CostCurrency,
                    RequestMetadataJson = generated.UsageMetadataJson
                });

                candidate.IsConsumed = true;
                await db.SaveChangesAsync(cancellationToken);
                postsCreated++;

                if (post.Status == PostStatus.WaitingModeration)
                {
                    await moderationNotifier.NotifyAsync(channel, post, cancellationToken);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var finalPublish = await PublishDuePostsAsync(channel, cancellationToken);
        publishedThisRun += finalPublish.Published;
        publishFailed += finalPublish.Failed;

        await db.SaveChangesAsync(cancellationToken);
        await realtimeNotifier.StateChangedAsync("pipeline-run", channel.Id, null, cancellationToken);

        return new PipelineRunResult(
            channel.Id,
            sourcesChecked,
            candidatesCollected,
            postsCreated,
            duplicatesSkipped,
            factCheckFailed,
            publishedThisRun,
            publishFailed,
            warnings);
    }

    private async Task<Channel?> LoadChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        return await db.Channels
            .Include(channel => channel.Sources)
            .Include(channel => channel.PublicationTypes)
            .Include(channel => channel.FooterLinks)
            .Include(channel => channel.ScheduleWindows)
            .FirstOrDefaultAsync(channel => channel.Id == channelId && channel.IsEnabled, cancellationToken);
    }

    private bool IsDue(Source source)
    {
        if (source.LastCheckedAtUtc is null)
        {
            return true;
        }

        return source.LastCheckedAtUtc.Value.AddMinutes(Math.Max(1, source.CheckEveryMinutes)) <= clock.UtcNow;
    }

    private async Task<SourceCandidate?> UpsertCandidateAsync(
        Guid channelId,
        Guid sourceId,
        CollectedCandidate item,
        CancellationToken cancellationToken)
    {
        var hash = ComputeHash($"{item.Url}|{item.Title}|{item.Summary}");
        var existing = await db.SourceCandidates
            .FirstOrDefaultAsync(candidate => candidate.ChannelId == channelId && candidate.NormalizedHash == hash, cancellationToken);

        if (existing is not null)
        {
            return existing.IsConsumed ? null : existing;
        }

        var candidate = new SourceCandidate
        {
            ChannelId = channelId,
            SourceId = sourceId,
            Title = item.Title,
            Url = item.Url,
            CanonicalUrl = item.Url,
            Summary = item.Summary,
            RawText = item.RawText,
            ImageUrl = item.ImageUrl,
            MediaUrlsJson = SerializeMediaUrls(item.MediaUrls),
            VideoUrl = item.VideoUrl,
            Score = item.Score,
            CommentsCount = item.CommentsCount,
            FoundAtUtc = item.FoundAtUtc.ToUniversalTime(),
            NormalizedHash = hash,
            MetadataJson = item.MetadataJson
        };

        db.SourceCandidates.Add(candidate);
        await db.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    private PublicationTypeSetting? PickPublicationType(Channel channel, Source source, SourceCandidate candidate, PipelineRunOptions options)
    {
        var enabled = channel.PublicationTypes
            .Where(type => type.IsEnabled)
            .OrderByDescending(type => type.Priority)
            .ToList();

        if (enabled.Count == 0)
        {
            return null;
        }

        if (options.PublicationKind is not null && SourceAllowsKind(source, options.PublicationKind.Value))
        {
            var requested = enabled.FirstOrDefault(type => type.Kind == options.PublicationKind.Value);
            if (requested is not null)
            {
                return requested;
            }
        }

        var text = $"{candidate.Title}\n{candidate.Summary}";
        if (source.Subreddit?.Contains("meme", StringComparison.OrdinalIgnoreCase) == true ||
            ContainsAny(text, ["meme", "memes", "мем"]))
        {
            var memeType = enabled.FirstOrDefault(type => type.Kind == PublicationKind.Meme);
            if (memeType is not null && SourceAllowsKind(source, memeType.Kind))
            {
                return memeType;
            }
        }

        if (ContainsAny(text, ["leak", "rumor", "rumour", "insider", "слух", "утеч"]))
        {
            var rumorType = enabled.FirstOrDefault(type => type.Kind == PublicationKind.Rumor);
            if (rumorType is not null && SourceAllowsKind(source, rumorType.Kind))
            {
                return rumorType;
            }
        }

        if (ContainsAny(text, ["free", "giveaway", "sale", "discount", "скид", "раздач", "бесплат"]))
        {
            var dealType = enabled.FirstOrDefault(type => type.Kind == PublicationKind.Deal);
            if (dealType is not null && SourceAllowsKind(source, dealType.Kind))
            {
                return dealType;
            }
        }

        if (ContainsAny(text, ["trailer", "showcase", "announce", "reveal", "анонс", "трейлер", "показали"]))
        {
            var trailerType = enabled.FirstOrDefault(type => type.Kind == PublicationKind.Trailer);
            if (trailerType is not null && SourceAllowsKind(source, trailerType.Kind))
            {
                return trailerType;
            }
        }

        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        if (allowed.Count > 0)
        {
            var firstAllowed = enabled.FirstOrDefault(type => allowed.Contains(type.Kind.ToString(), StringComparer.OrdinalIgnoreCase));
            if (firstAllowed is not null)
            {
                return firstAllowed;
            }
        }

        return enabled.FirstOrDefault(type => type.Kind == PublicationKind.News) ?? enabled[0];
    }

    private async Task<bool> IsDailyLimitReachedAsync(Guid channelId, int dailyLimit, CancellationToken cancellationToken)
    {
        if (dailyLimit <= 0)
        {
            return false;
        }

        var start = new DateTimeOffset(clock.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(1);
        var count = await db.Posts.CountAsync(post =>
            post.ChannelId == channelId &&
            post.CreatedAtUtc >= start &&
            post.CreatedAtUtc < end &&
            post.Status != PostStatus.Duplicate &&
            post.Status != PostStatus.Rejected,
            cancellationToken);

        return count >= dailyLimit;
    }

    private async Task CreateFactCheckFailedPostAsync(
        Channel channel,
        Source source,
        SourceCandidate candidate,
        PublicationTypeSetting publicationType,
        FactCheckResult factCheck,
        DeduplicationResult deduplication,
        CancellationToken cancellationToken)
    {
        db.Posts.Add(new Post
        {
            ChannelId = channel.Id,
            PublicationTypeId = publicationType.Id,
            SourceId = source.Id,
            SourceCandidateId = candidate.Id,
            PublicationKind = publicationType.Kind,
            SourceUrl = candidate.Url,
            SourceTitle = candidate.Title,
            OriginalSummary = candidate.Summary,
            VideoUrl = candidate.VideoUrl,
            FactCheckStatus = factCheck.Status,
            FactCheckSummary = factCheck.Summary,
            DeduplicationStatus = deduplication.Status,
            DeduplicationSummary = deduplication.Summary,
            Status = PostStatus.FactCheckFailed
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateDuplicatePostAsync(
        Channel channel,
        Source source,
        SourceCandidate candidate,
        PublicationTypeSetting publicationType,
        DeduplicationResult deduplication,
        CancellationToken cancellationToken)
    {
        db.Posts.Add(new Post
        {
            ChannelId = channel.Id,
            PublicationTypeId = publicationType.Id,
            SourceId = source.Id,
            SourceCandidateId = candidate.Id,
            PublicationKind = publicationType.Kind,
            SourceUrl = candidate.Url,
            SourceTitle = candidate.Title,
            OriginalSummary = candidate.Summary,
            VideoUrl = candidate.VideoUrl,
            MediaUrlsJson = candidate.MediaUrlsJson,
            FactCheckStatus = FactCheckStatus.NotChecked,
            DeduplicationStatus = deduplication.Status,
            DeduplicationSummary = deduplication.Summary,
            Status = PostStatus.Duplicate
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private Post CreatePost(
        Channel channel,
        Source source,
        SourceCandidate candidate,
        PublicationTypeSetting publicationType,
        DeduplicationResult deduplication,
        FactCheckResult factCheck,
        PostTextResult generated,
        PipelineRunOptions options)
    {
        var moderationMode = ResolveModerationMode(channel, publicationType);
        var status = moderationMode == ModerationMode.Automatic &&
                     factCheck.Status == FactCheckStatus.Passed &&
                     deduplication.Status == DeduplicationStatus.Unique
            ? PostStatus.Scheduled
            : PostStatus.WaitingModeration;

        return new Post
        {
            ChannelId = channel.Id,
            PublicationTypeId = publicationType.Id,
            SourceId = source.Id,
            SourceCandidateId = candidate.Id,
            PublicationKind = publicationType.Kind,
            SourceUrl = candidate.Url,
            SourceTitle = candidate.Title,
            OriginalSummary = candidate.Summary,
            VideoUrl = candidate.VideoUrl,
            FactCheckStatus = factCheck.Status,
            FactCheckSummary = factCheck.Summary,
            DeduplicationStatus = deduplication.Status,
            DeduplicationSummary = deduplication.Summary,
            Prompt = generated.Prompt,
            Model = generated.Model,
            GeneratedText = generated.Text,
            FinalText = generated.Text,
            Header = generated.Header,
            Footer = generated.Footer,
            ImagePath = publicationType.Kind == PublicationKind.Trailer && !string.IsNullOrWhiteSpace(candidate.VideoUrl)
                ? null
                : candidate.ImageUrl,
            MediaUrlsJson = candidate.MediaUrlsJson,
            Status = status,
            ScheduledForUtc = status == PostStatus.Scheduled && options.PublishNewPostsImmediately
                ? clock.UtcNow
                : FindNextSlot(channel),
            CostAmount = null,
            CostCurrency = generated.CostCurrency
        };
    }

    private static ModerationMode ResolveModerationMode(Channel channel, PublicationTypeSetting publicationType)
    {
        return channel.DefaultModerationMode == ModerationMode.Manual
            ? ModerationMode.Manual
            : publicationType.ModerationMode;
    }

    private async Task GenerateImageAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        Post post,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var image = await imageGenerator.GenerateForPostAsync(channel, post, cancellationToken);
            if (!string.IsNullOrWhiteSpace(image.ImageUrl))
            {
                post.ImagePath = image.ImageUrl;
            }

            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = channel.Id,
                Post = post,
                Provider = image.Provider,
                Model = image.Model,
                TaskType = AiTaskType.ImageGeneration,
                CostAmount = AiCostDefaults.ImageGenerationRub,
                CostCurrency = AiCostDefaults.Currency,
                ProviderCostAmount = image.CostAmount,
                ProviderCostCurrency = image.CostCurrency,
                RequestMetadataJson = image.UsageMetadataJson
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image generation failed for post {PostId}.", post.Id);
            warnings.Add($"Картинка не сгенерирована для {publicationType.Name}: {ex.Message}");
        }
    }

    private async Task<string?> ComputeEmbeddingJsonAsync(Guid channelId, Post post, CancellationToken cancellationToken)
    {
        var vector = await embeddingProvider.EmbedAsync(
            channelId,
            AiDeduplicationService.EmbeddingText(post.SourceTitle, post.OriginalSummary),
            cancellationToken);

        return vector is null ? null : JsonSerializer.Serialize(vector);
    }

    private static bool ShouldGenerateImage(Channel channel, PublicationTypeSetting publicationType, Post post)
    {
        if (post.PublicationKind == PublicationKind.Trailer && !string.IsNullOrWhiteSpace(post.VideoUrl))
        {
            return false;
        }

        if (publicationType.MediaMode == MediaGenerationMode.GeneratePoster)
        {
            return true;
        }

        if (publicationType.MediaMode == MediaGenerationMode.TranslateMeme)
        {
            return true;
        }

        return channel.DefaultModerationMode == ModerationMode.Automatic &&
               publicationType.Kind is PublicationKind.News or PublicationKind.BreakingNews or PublicationKind.Digest or PublicationKind.Deal or PublicationKind.Trailer;
    }

    private void AddCollectorProviderUsageIfPresent(
        Channel channel,
        Source source,
        IReadOnlyCollection<CollectedCandidate> collected)
    {
        var usageCandidate = collected.FirstOrDefault(candidate => candidate.ProviderCostAmount is not null);
        if (usageCandidate is null)
        {
            return;
        }

        db.AiUsageRecords.Add(new AiUsageRecord
        {
            ChannelId = channel.Id,
            Provider = "polza",
            Model = source.Name,
            TaskType = AiTaskType.StructuredOutput,
            CostAmount = null,
            CostCurrency = AiCostDefaults.Currency,
            ProviderCostAmount = usageCandidate.ProviderCostAmount,
            ProviderCostCurrency = usageCandidate.ProviderCostCurrency,
            RequestMetadataJson = usageCandidate.ProviderUsageMetadataJson
        });
    }

    private DateTimeOffset FindNextSlot(Channel channel)
    {
        if (channel.ScheduleWindows.Count == 0)
        {
            return clock.UtcNow.AddMinutes(5);
        }

        var zone = ResolveTimeZone(channel.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);

        for (var dayOffset = 0; dayOffset < 8; dayOffset++)
        {
            var date = DateOnly.FromDateTime(localNow.DateTime.AddDays(dayOffset));
            var dayOfWeek = date.DayOfWeek;
            var windows = channel.ScheduleWindows
                .Where(window => window.DayOfWeek is null || window.DayOfWeek == dayOfWeek)
                .OrderBy(window => window.StartTime)
                .ToList();

            foreach (var window in windows)
            {
                var localStart = date.ToDateTime(window.StartTime);
                if (dayOffset == 0 && localStart <= localNow.DateTime)
                {
                    localStart = localNow.DateTime.AddMinutes(Math.Max(5, window.MinimumIntervalMinutes));
                }

                var localEnd = date.ToDateTime(window.EndTime);
                if (localStart <= localEnd)
                {
                    return new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)).ToUniversalTime();
                }
            }
        }

        return clock.UtcNow.AddMinutes(5);
    }

    private async Task<PublishSweepResult> PublishDuePostsAsync(Channel channel, CancellationToken cancellationToken)
    {
        var duePosts = await db.Posts
            .Where(post => post.ChannelId == channel.Id &&
                           post.Status == PostStatus.Scheduled &&
                           post.ScheduledForUtc <= clock.UtcNow)
            .OrderBy(post => post.ScheduledForUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        var published = 0;
        var failed = 0;

        foreach (var post in duePosts)
        {
            var result = await telegramPublisher.PublishAsync(channel, post, cancellationToken);
            if (result.Success)
            {
                post.Status = PostStatus.Published;
                post.PublishedAtUtc = clock.UtcNow;
                post.TelegramMessageId = result.TelegramMessageId;
                post.TelegramPostUrl = result.PublicUrl;
                published++;
            }
            else
            {
                post.Status = PostStatus.PublishFailed;
                post.RejectionReason = result.Error;
                failed++;
            }
        }

        return new PublishSweepResult(published, failed);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static List<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasCreatedEnough(PipelineRunOptions options, int postsCreated)
    {
        return options.MaxPostsToCreate.HasValue && postsCreated >= Math.Max(1, options.MaxPostsToCreate.Value);
    }

    private static bool SourceAllowsKind(Source source, PublicationKind kind)
    {
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        return allowed.Count == 0 || allowed.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private static int SourcePriority(SourceKind kind, PublicationKind? requestedKind)
    {
        if (requestedKind is PublicationKind.Meme or PublicationKind.Rumor)
        {
            return kind switch
            {
                SourceKind.Reddit => 0,
                SourceKind.AiWebSearch => 1,
                SourceKind.Rss => 2,
                SourceKind.Web => 3,
                _ => 9
            };
        }

        return kind switch
        {
            SourceKind.AiWebSearch => 0,
            SourceKind.Rss => 1,
            SourceKind.Web => 2,
            SourceKind.Reddit => 3,
            _ => 9
        };
    }

    private bool IsStale(CollectedCandidate item)
    {
        return item.FoundAtUtc < clock.UtcNow.AddHours(-48);
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant().Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? SerializeMediaUrls(IReadOnlyCollection<string>? mediaUrls)
    {
        if (mediaUrls is null || mediaUrls.Count == 0)
        {
            return null;
        }

        var normalized = mediaUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return normalized.Length == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    private sealed record PublishSweepResult(int Published, int Failed);
}
