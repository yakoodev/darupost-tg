using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Domain.Stories;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class AutopostingPipeline(
    AppDbContext db,
    CandidateIngestService candidateIngest,
    IDeduplicationService deduplicationService,
    IFactCheckService factCheckService,
    IPostTextGenerator postTextGenerator,
    IImageGenerator imageGenerator,
    IEmbeddingProvider embeddingProvider,
    IModerationNotifier moderationNotifier,
    ITelegramPublisher telegramPublisher,
    IDateTimeProvider clock,
    IRealtimeNotifier realtimeNotifier,
    INicheProfileProvider profiles,
    IHttpClientFactory httpClientFactory,
    IOptions<MediaOptions> mediaOptionsAccessor,
    ILogger<AutopostingPipeline> logger) : IAutopostingPipeline
{
    /// <summary>Rubric chip label for scene posts, from the source category (VtuN tags each post, e.g. "° КАВЕР").</summary>
    private static string StoreRubric(SourceCandidate candidate)
    {
        // Twitch clips are tagged in metadata by the collector — give them their own chip by kind.
        if (!string.IsNullOrEmpty(candidate.MetadataJson) && candidate.MetadataJson.Contains("twitch-clip", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.MetadataJson.Contains("\"kind\":\"meme\"", StringComparison.OrdinalIgnoreCase) ? "МЕМ" : "КЛИП";
        }

        var h = ((candidate.Title ?? string.Empty) + " " + (candidate.RawText ?? string.Empty)).ToUpperInvariant();
        if (h.Contains("ДЕБЮТ")) return "ДЕБЮТ";
        if (h.Contains("КАВЕР")) return "КАВЕР";
        if (h.Contains("3D")) return "3D";
        if (h.Contains("АУТФИТ") || h.Contains("НОВАЯ МОДЕЛ") || h.Contains("НОВЫЙ ОБРАЗ")) return "МОДЕЛЬ";
        if (h.Contains("ДЕНЬ РОЖДЕНИЯ")) return "ДР";
        if (h.Contains("ИНТЕРВЬЮ") || h.Contains("ШОУ") || h.Contains("ПОДКАСТ")) return "ИНТЕРВЬЮ";
        if (h.Contains("ТУРНИР") || h.Contains("КОНКУРС") || h.Contains("ИВЕНТ") || h.Contains("КОЛЛАБ")) return "ИВЕНТ";
        return "ВИТУБ";
    }

    private async Task LocalizeSourceImageAsync(Post post, List<string> warnings, CancellationToken cancellationToken)
    {
        var url = post.ImagePath;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; tg-autoposter/0.2)");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
            {
                return;
            }

            var publicPath = LocalMediaPaths.BuildPublicPath("source", $"{Guid.NewGuid():N}.jpg");
            var fullPath = LocalMediaPaths.BuildFullPath(mediaOptionsAccessor.Value, publicPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
            post.ImagePath = publicPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to localize source image {Url} for post {PostId}.", url, post.Id);
            warnings.Add($"Не удалось скачать картинку источника: {ex.Message}");
        }
    }

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

        var profile = profiles.Get(channel.ProfileKey);
        var initialPublish = await PublishDuePostsAsync(channel, cancellationToken);

        // Phase 1 — ingest: cheap collection into SourceCandidates (also done all day by IngestWorker).
        var sourcesChecked = 0;
        var candidatesCollected = 0;
        if (options.CollectSources && options.CandidateId is null)
        {
            var sourcesToCheck = channel.Sources
                .Where(source => source.IsEnabled && (options.IgnoreSourceSchedule || IsDue(source)))
                .Where(source => options.PublicationKind is null || SourceAllowsKind(source, options.PublicationKind.Value))
                .OrderBy(source => options.PublicationKind.HasValue ? SourcePriority(source.Kind, options.PublicationKind.Value) : 0)
                .ThenBy(source => source.LastCheckedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(source => SourcePriority(source.Kind, null))
                .ThenBy(source => source.Name)
                .ToList();

            foreach (var source in sourcesToCheck)
            {
                var ingest = await candidateIngest.IngestSourceAsync(channel, profile, source, cancellationToken);
                sourcesChecked++;
                candidatesCollected += ingest.NewCandidates;
                if (ingest.Error is not null)
                {
                    warnings.Add($"Источник {source.Name}: ошибка сбора ({ingest.Error}).");
                }
            }
        }

        // Phase 2 — generation: take pending candidates from the pool (newest first) and turn them into posts.
        var postsCreated = 0;
        var duplicatesSkipped = 0;
        var factCheckFailed = 0;
        var publishedThisRun = initialPublish.Published;
        var publishFailed = initialPublish.Failed;

        var pending = await LoadPendingCandidatesAsync(channel, options, cancellationToken);
        foreach (var candidate in pending)
        {
            if (HasCreatedEnough(options, postsCreated))
            {
                break;
            }

            // The daily limit is about news posts; memes have their own daily lane.
            if (!options.BypassDailyLimit &&
                options.PublicationKind != PublicationKind.Meme &&
                await IsDailyLimitReachedAsync(channel.Id, channel.DailyPostLimit, cancellationToken))
            {
                warnings.Add($"Канал {channel.Name}: дневной лимит {channel.DailyPostLimit} постов достигнут.");
                break;
            }

            var source = channel.Sources.FirstOrDefault(item => item.Id == candidate.SourceId);
            if (source is null)
            {
                candidate.IsConsumed = true;
                candidate.ConsumedReason = "orphan";
                continue;
            }

            if (options.PublicationKind is not null && !SourceAllowsKind(source, options.PublicationKind.Value))
            {
                continue;
            }

            var publicationType = PickPublicationType(channel, profile, source, candidate, options);
            if (publicationType is null)
            {
                warnings.Add($"Канал {channel.Name}: не найден включённый тип публикации для кандидата {candidate.Title}.");
                continue;
            }

            // Store lane posts the source image as-is, so a candidate without any image is not worth drafting — skip it.
            if (publicationType.MediaMode == MediaGenerationMode.UseSourceImage && string.IsNullOrWhiteSpace(candidate.ImageUrl))
            {
                candidate.IsConsumed = true;
                candidate.ConsumedReason = "no-image";
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            // Scene lane (Deal): publish only real events (debut/cover/model/birthday/interview/event/3D),
            // never plain "X streams today" announcements — those fall to the default "ВИТУБ" rubric.
            if (publicationType.Kind == PublicationKind.Deal && StoreRubric(candidate) == "ВИТУБ")
            {
                candidate.IsConsumed = true;
                candidate.ConsumedReason = "not-an-event";
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            // Claim the candidate before the slow AI steps: a scheduled run and a manual one must not both draft it.
            if (options.CandidateId is null)
            {
                var claimed = await db.SourceCandidates
                    .Where(item => item.Id == candidate.Id && !item.IsConsumed)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(item => item.IsConsumed, true)
                        .SetProperty(item => item.ConsumedReason, "processing"),
                        cancellationToken);
                if (claimed == 0)
                {
                    continue;
                }
            }

            var deduplication = await deduplicationService.CheckAsync(candidate, cancellationToken);
            if (deduplication.Status == DeduplicationStatus.Duplicate)
            {
                duplicatesSkipped++;
                await CreateDuplicatePostAsync(channel, source, candidate, publicationType, deduplication, cancellationToken);
                candidate.IsConsumed = true;
                candidate.ConsumedReason = "duplicate";
                continue;
            }

            var factCheck = await factCheckService.CheckAsync(channel, publicationType, candidate, cancellationToken);
            // Only a hard "Failed" is dropped. "NeedsManualReview" must go to moderation, not the bin —
            // otherwise autopilot silently publishes nothing whenever the fact check is unsure.
            if (factCheck.Status == FactCheckStatus.Failed)
            {
                factCheckFailed++;
                await CreateFactCheckFailedPostAsync(channel, source, candidate, publicationType, factCheck, deduplication, cancellationToken);
                candidate.IsConsumed = true;
                candidate.ConsumedReason = "factcheck";
                continue;
            }

            var story = options.StoryId is Guid storyId
                ? await db.Stories.FirstOrDefaultAsync(item => item.Id == storyId && item.ChannelId == channel.Id, cancellationToken)
                : null;
            var storyContext = story is null ? null : await BuildStoryContextAsync(story, candidate.Id, cancellationToken);

            var talentFacts = await BuildTalentFactsAsync(channel.Id, story?.TalentsCsv, cancellationToken);
            var generated = await postTextGenerator.GenerateAsync(channel, publicationType, candidate, cancellationToken, storyContext, talentFacts);
            var post = CreatePost(channel, source, candidate, publicationType, deduplication, factCheck, generated, options);
            post.Headline = generated.Headline;
            post.Rubric = !string.IsNullOrWhiteSpace(story?.EditorRubric) ? story!.EditorRubric : StoreRubric(candidate);
            post.EmbeddingJson = await ComputeEmbeddingJsonAsync(channel.Id, post, cancellationToken);

            if (ShouldGenerateImage(channel, publicationType, post))
            {
                await GenerateImageAsync(channel, publicationType, post, warnings, cancellationToken);
            }
            else if (publicationType.MediaMode == MediaGenerationMode.UseSourceImage)
            {
                // Telegram can't fetch some source hosts (t.me CDN, VK) by URL — download the image and send it as a file.
                await LocalizeSourceImageAsync(post, warnings, cancellationToken);
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
                CostAmount = generated.CostAmount,
                CostCurrency = generated.CostCurrency,
                ProviderCostAmount = generated.CostAmount,
                ProviderCostCurrency = generated.CostCurrency,
                RequestMetadataJson = generated.UsageMetadataJson
            });

            candidate.IsConsumed = true;
            candidate.ConsumedReason = "post";
            if (story is not null)
            {
                story.PostId = post.Id;
                story.Status = StoryStatus.Drafted;
            }

            await db.SaveChangesAsync(cancellationToken);
            postsCreated++;

            if (post.Status == PostStatus.WaitingModeration)
            {
                await moderationNotifier.NotifyAsync(channel, post, cancellationToken);
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

    /// <summary>
    /// Pending pool: unconsumed candidates newer than the stale window, newest first. Older ones are expired in place.
    /// With <see cref="PipelineRunOptions.CandidateId"/> the specific candidate is returned regardless of its state.
    /// </summary>
    private async Task<List<SourceCandidate>> LoadPendingCandidatesAsync(Channel channel, PipelineRunOptions options, CancellationToken cancellationToken)
    {
        if (options.CandidateId is Guid candidateId)
        {
            var one = await db.SourceCandidates.FirstOrDefaultAsync(
                candidate => candidate.Id == candidateId && candidate.ChannelId == channel.Id,
                cancellationToken);
            return one is null ? [] : [one];
        }

        var staleSince = clock.UtcNow.AddHours(-CandidateIngestService.StaleHours);
        await db.SourceCandidates
            .Where(candidate => candidate.ChannelId == channel.Id && !candidate.IsConsumed && candidate.FoundAtUtc < staleSince)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(candidate => candidate.IsConsumed, true)
                .SetProperty(candidate => candidate.ConsumedReason, "expired"),
                cancellationToken);

        var take = Math.Max(20, (options.MaxPostsToCreate ?? 5) * 10);
        var pool = db.SourceCandidates
            .Where(candidate => candidate.ChannelId == channel.Id && !candidate.IsConsumed && candidate.FoundAtUtc >= staleSince);

        if (options.PublicationKind is PublicationKind requestedKind)
        {
            // A requested kind (e.g. "Мем" from the dashboard) must not lose to the newest items of other kinds:
            // narrow the pool to sources that allow it before taking the newest N. Exact matching happens later.
            var pattern = $"%{requestedKind}%";
            pool = pool.Where(candidate =>
                candidate.Source != null &&
                (candidate.Source.AllowedPublicationKindsCsv == null ||
                 candidate.Source.AllowedPublicationKindsCsv == "" ||
                 EF.Functions.ILike(candidate.Source.AllowedPublicationKindsCsv, pattern)));

            if (requestedKind == PublicationKind.Meme)
            {
                return await pool
                    .Where(candidate => candidate.ImageUrl != null)
                    .OrderByDescending(candidate => candidate.Score ?? 0)
                    .ThenByDescending(candidate => candidate.FoundAtUtc)
                    .Take(take)
                    .ToListAsync(cancellationToken);
            }
        }

        return await pool
            .OrderByDescending(candidate => candidate.FoundAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
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

    private static PublicationTypeSetting? PickPublicationType(
        Channel channel,
        NicheProfile profile,
        Source source,
        SourceCandidate candidate,
        PipelineRunOptions options)
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
        var sourceLabel = $"{source.Name} {source.Subreddit}";

        // Profile-driven classification: first matching rule wins (rules are ordered in the profile).
        foreach (var rule in profile.Classification)
        {
            var matches = ContainsAny(text, rule.Keywords) ||
                          (rule.SourceNameHints.Count > 0 && ContainsAny(sourceLabel, rule.SourceNameHints));
            if (!matches)
            {
                continue;
            }

            var type = enabled.FirstOrDefault(item => item.Kind == rule.Kind);
            if (type is not null && SourceAllowsKind(source, type.Kind))
            {
                return type;
            }
        }

        // Nothing classified: plain News when the source allows it; otherwise the best allowed kind,
        // but never Breaking/Rumor/Digest by default — those are only ever picked explicitly or by a rule.
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        var allowsKind = (PublicationKind kind) => allowed.Count == 0 || allowed.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase);
        var news = enabled.FirstOrDefault(type => type.Kind == PublicationKind.News);
        if (news is not null && allowsKind(PublicationKind.News))
        {
            return news;
        }

        var neutral = enabled.FirstOrDefault(type =>
            allowsKind(type.Kind) &&
            type.Kind is not (PublicationKind.BreakingNews or PublicationKind.Rumor or PublicationKind.Digest or PublicationKind.Meme));
        if (neutral is not null)
        {
            return neutral;
        }

        return enabled.FirstOrDefault(type => allowsKind(type.Kind)) ?? news ?? enabled[0];
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
            post.Status != PostStatus.Rejected &&
            post.Status != PostStatus.FactCheckFailed &&
            post.PublicationKind != PublicationKind.Meme &&
            post.PublicationKind != PublicationKind.Digest,
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
                CostAmount = image.CostAmount ?? AiCostDefaults.ImageGenerationRub,
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

    /// <summary>Registry facts for the story's talents, so the writer explains who they are without inventing it.</summary>
    private async Task<string?> BuildTalentFactsAsync(Guid channelId, string? talentsCsv, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(talentsCsv))
        {
            return null;
        }

        var names = talentsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var talents = await db.Talents
            .AsNoTracking()
            .Where(talent => talent.ChannelId == channelId && names.Contains(talent.Name))
            .Select(talent => new { talent.Name, talent.Agency, talent.Group, talent.Notes })
            .ToListAsync(cancellationToken);
        if (talents.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, talents.Select(talent =>
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(talent.Agency)) parts.Add($"агентство: {talent.Agency}");
            if (!string.IsNullOrWhiteSpace(talent.Group)) parts.Add($"группа: {talent.Group}");
            if (!string.IsNullOrWhiteSpace(talent.Notes)) parts.Add($"заметка: {talent.Notes}");
            return $"- {talent.Name}" + (parts.Count > 0 ? ": " + string.Join("; ", parts) : string.Empty);
        }));
    }

    private async Task<string> BuildStoryContextAsync(Story story, Guid leadCandidateId, CancellationToken cancellationToken)
    {
        var others = await db.SourceCandidates
            .AsNoTracking()
            .Where(item => item.StoryId == story.Id && item.Id != leadCandidateId)
            .OrderByDescending(item => item.Score ?? 0)
            .ThenBy(item => item.FoundAtUtc)
            .Take(6)
            .Select(item => new { item.Title, item.Summary, item.RawText, item.Url, item.FoundAtUtc, SourceName = item.Source!.Name })
            .ToListAsync(cancellationToken);

        return string.Join(Environment.NewLine, others.Select(item =>
        {
            var body = (string.IsNullOrWhiteSpace(item.RawText) ? item.Summary : item.RawText).ReplaceLineEndings(" ").Trim();
            if (body.Length > 700)
            {
                body = body[..700] + "…";
            }

            return $"- ({item.SourceName}, {item.FoundAtUtc:dd.MM HH:mm} UTC) {item.Title}: {body} [{item.Url}]";
        }));
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
        // Branded cards are built from the source image (YouTube thumbnail for trailers), so they apply to every kind.
        if (publicationType.MediaMode == MediaGenerationMode.BrandCard)
        {
            return true;
        }

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
        return MarkerMatcher.ContainsAny(text, markers);
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

    private sealed record PublishSweepResult(int Published, int Failed);
}
