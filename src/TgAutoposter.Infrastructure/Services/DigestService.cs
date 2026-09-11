using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Stories;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed record DigestRunResult(
    Guid ChannelId,
    Guid? DigestPostId,
    int StoriesConsidered,
    int DigestItems,
    int DraftsCreated,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Evening pass: cluster what was collected, ask the model to rank the day's stories, publish a digest draft
/// and spin off standalone drafts ("вероятные посты") for the strongest stories via the regular pipeline.
/// </summary>
public sealed class DigestService(
    AppDbContext db,
    StoryClusteringService clustering,
    IAiProvider aiProvider,
    INicheProfileProvider profiles,
    IAutopostingPipeline pipeline,
    IImageGenerator imageGenerator,
    IModerationNotifier moderationNotifier,
    IRealtimeNotifier realtimeNotifier,
    IDateTimeProvider clock,
    ILogger<DigestService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DigestRunResult> RunAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var channel = await db.Channels
            .Include(channel => channel.PublicationTypes)
            .Include(channel => channel.FooterLinks)
            .FirstOrDefaultAsync(channel => channel.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return new DigestRunResult(channelId, null, 0, 0, 0, ["Канал не найден."]);
        }

        var profile = profiles.Get(channel.ProfileKey);
        var now = clock.UtcNow;

        await clustering.ClusterPendingAsync(channel, cancellationToken);

        var since = channel.LastDigestAtUtc ?? now.AddHours(-24);
        if (since < now.AddHours(-48))
        {
            since = now.AddHours(-48);
        }

        var stories = await db.Stories
            .Where(story => story.ChannelId == channel.Id && story.Status == StoryStatus.Open && story.LastSeenAtUtc >= since)
            .OrderByDescending(story => story.Score)
            .ThenByDescending(story => story.LastSeenAtUtc)
            .Take(Math.Clamp(channel.DigestMaxStories, 3, 30))
            .ToListAsync(cancellationToken);

        if (stories.Count == 0)
        {
            warnings.Add("За период нет сюжетов для дайджеста.");
            channel.LastDigestAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return new DigestRunResult(channel.Id, null, 0, 0, 0, warnings);
        }

        var storyIds = stories.Select(story => story.Id).ToList();
        var candidates = await db.SourceCandidates
            .AsNoTracking()
            .Where(candidate => candidate.StoryId != null && storyIds.Contains(candidate.StoryId.Value))
            .Select(candidate => new { candidate.Id, StoryId = candidate.StoryId!.Value, candidate.Title, candidate.Summary, candidate.Url, candidate.Score, candidate.Author, candidate.SourceId })
            .ToListAsync(cancellationToken);
        var sourceNames = await db.Sources
            .Where(source => source.ChannelId == channel.Id)
            .ToDictionaryAsync(source => source.Id, source => source.Name, cancellationToken);

        var views = stories.Select((story, index) =>
        {
            var own = candidates.Where(candidate => candidate.StoryId == story.Id)
                .OrderByDescending(candidate => candidate.Score ?? 0)
                .Take(3)
                .ToList();
            var lead = own.FirstOrDefault(candidate => candidate.Id == story.LeadCandidateId) ?? own.FirstOrDefault();
            return new StoryView(
                index,
                story,
                lead?.Url,
                own.Select(candidate => new StoryItemView(
                    sourceNames.TryGetValue(candidate.SourceId, out var name) ? name : "source",
                    candidate.Title,
                    Truncate(candidate.Summary, 300),
                    candidate.Url)).ToList());
        }).ToList();

        var plan = await PlanAsync(channel, profile, views, cancellationToken)
                   ?? FallbackPlan(views, channel.DigestMaxDrafts);

        Post? digestPost = null;
        var digestType = channel.PublicationTypes.FirstOrDefault(type => type.IsEnabled && type.Kind == PublicationKind.Digest);
        if (digestType is null)
        {
            warnings.Add("Тип «Дайджест» выключен — пост-выжимка не создан, только черновики.");
        }
        else if (plan.Items.Count == 0)
        {
            warnings.Add("Модель не выбрала ни одного пункта для дайджеста.");
        }
        else
        {
            digestPost = await CreateDigestPostAsync(channel, profile, digestType, views, plan, warnings, cancellationToken);
        }

        var draftsCreated = 0;
        foreach (var pick in plan.Posts.Take(Math.Clamp(channel.DigestMaxDrafts, 0, 10)))
        {
            var view = views.FirstOrDefault(item => item.Index == pick.Index);
            if (view?.Story.LeadCandidateId is null)
            {
                continue;
            }

            try
            {
                var result = await pipeline.RunForChannelAsync(
                    channel.Id,
                    new PipelineRunOptions(
                        PublishNewPostsImmediately: false,
                        MaxPostsToCreate: 1,
                        IgnoreSourceSchedule: false,
                        BypassDailyLimit: false,
                        PublicationKind: pick.Kind,
                        CollectSources: false,
                        CandidateId: view.Story.LeadCandidateId),
                    cancellationToken);

                if (result.PostsCreated > 0)
                {
                    draftsCreated++;
                    view.Story.Status = StoryStatus.Drafted;
                }
                else
                {
                    warnings.Add($"Черновик по сюжету «{Truncate(view.Story.Title, 60)}» не создан: {result.Warnings.FirstOrDefault() ?? (result.DuplicatesSkipped > 0 ? "дубль" : result.FactCheckFailed > 0 ? "фактчек" : "без причины")}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Draft generation from story {StoryId} failed.", view.Story.Id);
                warnings.Add($"Черновик по сюжету «{Truncate(view.Story.Title, 60)}»: {ex.Message}");
            }
        }

        foreach (var view in views)
        {
            if (view.Story.Status == StoryStatus.Open && plan.Items.Any(item => item.Index == view.Index))
            {
                view.Story.Status = StoryStatus.InDigest;
            }

            if (digestPost is not null && plan.Items.Any(item => item.Index == view.Index))
            {
                view.Story.DigestPostId = digestPost.Id;
            }
        }

        channel.LastDigestAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await realtimeNotifier.StateChangedAsync("digest-run", channel.Id, digestPost?.Id, cancellationToken);

        return new DigestRunResult(channel.Id, digestPost?.Id, stories.Count, plan.Items.Count, draftsCreated, warnings);
    }

    private async Task<DigestPlan?> PlanAsync(Channel channel, NicheProfile profile, IReadOnlyList<StoryView> views, CancellationToken cancellationToken)
    {
        var system = new StringBuilder()
            .AppendLine(channel.SystemPrompt)
            .AppendLine()
            .AppendLine("Ты составляешь вечерний дайджест Telegram-канала и выбираешь, какие сюжеты заслуживают отдельного поста.")
            .AppendLine(profile.Prompts.DigestInstructions)
            .AppendLine("Ответь СТРОГО одним JSON-объектом без markdown:")
            .AppendLine("{\"items\":[{\"index\":0,\"headline\":\"короткий заголовок по-русски\",\"text\":\"1-2 предложения по-русски\"}],\"posts\":[{\"index\":0,\"kind\":\"News|BreakingNews|Rumor|Trailer|Deal\",\"reason\":\"почему нужен отдельный пост\"}]}")
            .AppendLine("items — 4-7 самых важных сюжетов в порядке важности (index из списка). posts — до 3 сюжетов для отдельных постов, самые сильные. Не выдумывай факты, используй только данные из списка.")
            .ToString();

        var user = new StringBuilder();
        user.AppendLine($"Канал: {channel.Name}. Позиционирование: {channel.Positioning}");
        user.AppendLine($"Сюжеты за день ({views.Count}):");
        foreach (var view in views)
        {
            user.AppendLine();
            user.AppendLine($"[{view.Index}] {view.Story.Title}");
            user.AppendLine($"    источников: {view.Story.SourcesCount}, упоминаний: {view.Story.CandidatesCount}, важность: {view.Story.Score:0.0}{(view.Story.IsBreaking ? ", СРОЧНОЕ" : string.Empty)}{(string.IsNullOrWhiteSpace(view.Story.TalentsCsv) ? string.Empty : $", таланты: {view.Story.TalentsCsv}")}");
            foreach (var item in view.Items)
            {
                user.AppendLine($"    - ({item.Source}) {item.Title}: {item.Summary}");
            }
        }

        try
        {
            var response = await aiProvider.CompleteAsync(
                new AiRequest(channel.Id, AiTaskType.StructuredOutput, system, user.ToString(), RequireJson: true),
                cancellationToken);

            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = channel.Id,
                Provider = response.Provider,
                Model = response.Model,
                TaskType = AiTaskType.StructuredOutput,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                TotalTokens = response.TotalTokens,
                CostAmount = null,
                CostCurrency = response.CostCurrency,
                ProviderCostAmount = response.CostAmount,
                ProviderCostCurrency = response.CostCurrency,
                RequestMetadataJson = response.UsageMetadataJson
            });

            if (response.Provider == "local-fallback" || string.IsNullOrWhiteSpace(response.Text))
            {
                return null;
            }

            var json = ExtractJsonObject(response.Text);
            if (json is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = new List<DigestItem>();
            if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in itemsEl.EnumerateArray())
                {
                    var index = ReadInt(el, "index");
                    if (index is null || views.All(view => view.Index != index.Value))
                    {
                        continue;
                    }

                    var headline = ReadString(el, "headline");
                    var text = ReadString(el, "text");
                    if (string.IsNullOrWhiteSpace(headline))
                    {
                        continue;
                    }

                    items.Add(new DigestItem(index.Value, headline.Trim(), (text ?? string.Empty).Trim()));
                }
            }

            var posts = new List<DigestPostPick>();
            if (root.TryGetProperty("posts", out var postsEl) && postsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in postsEl.EnumerateArray())
                {
                    var index = ReadInt(el, "index");
                    if (index is null || views.All(view => view.Index != index.Value))
                    {
                        continue;
                    }

                    PublicationKind? kind = Enum.TryParse<PublicationKind>(ReadString(el, "kind"), true, out var parsed) && parsed != PublicationKind.Digest && parsed != PublicationKind.Meme
                        ? parsed
                        : null;
                    posts.Add(new DigestPostPick(index.Value, kind));
                }
            }

            return items.Count == 0 && posts.Count == 0 ? null : new DigestPlan(items, posts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Digest planning via AI failed for channel {ChannelId}; using fallback ranking.", channel.Id);
            return null;
        }
    }

    private static DigestPlan FallbackPlan(IReadOnlyList<StoryView> views, int maxDrafts)
    {
        var items = views.Take(6)
            .Select(view => new DigestItem(view.Index, Truncate(view.Story.Title, 120), FirstSentence(view.Story.Summary, view.Story.Title)))
            .ToList();
        var posts = views.Take(Math.Clamp(maxDrafts, 0, 10))
            .Select(view => new DigestPostPick(view.Index, view.Story.KindHint))
            .ToList();
        return new DigestPlan(items, posts);
    }

    private async Task<Post> CreateDigestPostAsync(
        Channel channel,
        NicheProfile profile,
        PublicationTypeSetting digestType,
        IReadOnlyList<StoryView> views,
        DigestPlan plan,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var localDate = TimeZoneInfo.ConvertTime(clock.UtcNow, ResolveTimeZone(channel.TimeZone)).Date;
        var body = new StringBuilder();
        var related = new List<object>();
        foreach (var item in plan.Items)
        {
            var view = views.First(view => view.Index == item.Index);
            var url = view.LeadUrl;
            body.Append("• ");
            body.Append(string.IsNullOrWhiteSpace(url) ? $"**{item.Headline}**" : $"[{item.Headline}]({url})");
            if (!string.IsNullOrWhiteSpace(item.Text))
            {
                body.Append(" — ").Append(item.Text);
            }

            body.AppendLine();
            body.AppendLine();
            related.Add(new { storyId = view.Story.Id, title = view.Story.Title, url });
        }

        var text = body.ToString().Trim();
        var header = string.IsNullOrWhiteSpace(digestType.HeaderTemplate) ? string.Empty : digestType.HeaderTemplate.Trim();
        var footer = BuildFooter(channel, digestType);
        var moderationMode = channel.DefaultModerationMode == ModerationMode.Manual ? ModerationMode.Manual : digestType.ModerationMode;
        var status = moderationMode == ModerationMode.Automatic ? PostStatus.Scheduled : PostStatus.WaitingModeration;

        var post = new Post
        {
            ChannelId = channel.Id,
            PublicationTypeId = digestType.Id,
            PublicationKind = PublicationKind.Digest,
            SourceTitle = $"Дайджест за {localDate:dd.MM.yyyy}",
            SourceUrl = null,
            OriginalSummary = string.Join("\n", plan.Items.Select(item => views.First(view => view.Index == item.Index).Story.Title)),
            RelatedSourcesJson = JsonSerializer.Serialize(related, JsonOptions),
            FactCheckStatus = FactCheckStatus.Passed,
            FactCheckSummary = "Дайджест собран из сюжетов дня; отдельные пункты проверяются при создании постов.",
            DeduplicationStatus = DeduplicationStatus.Unique,
            DeduplicationSummary = "Дайджест.",
            Prompt = "digest",
            Model = "digest-planner",
            GeneratedText = text,
            FinalText = text,
            Header = header,
            Footer = footer,
            Status = status,
            ScheduledForUtc = clock.UtcNow.AddMinutes(5),
            CostAmount = null,
            CostCurrency = "RUB"
        };

        if (digestType.MediaMode == MediaGenerationMode.GeneratePoster)
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Digest image generation failed for channel {ChannelId}.", channel.Id);
                warnings.Add($"Картинка дайджеста не сгенерирована: {ex.Message}");
            }
        }

        db.Posts.Add(post);
        db.PostVersions.Add(new PostVersion
        {
            Post = post,
            VersionNumber = 1,
            Text = text,
            Prompt = "digest",
            Model = "digest-planner",
            Reason = "digest"
        });

        await db.SaveChangesAsync(cancellationToken);
        if (post.Status == PostStatus.WaitingModeration)
        {
            await moderationNotifier.NotifyAsync(channel, post, cancellationToken);
        }

        return post;
    }

    private static string BuildFooter(Channel channel, PublicationTypeSetting type)
    {
        var links = channel.FooterLinks
            .Where(link => link.IsEnabled)
            .Where(link => string.IsNullOrWhiteSpace(link.PublicationKindsCsv) ||
                           link.PublicationKindsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Any(value => value.Equals(type.Kind.ToString(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(link => link.SortOrder)
            .Take(3)
            .ToList();

        var template = string.IsNullOrWhiteSpace(type.FooterTemplate) ? string.Empty : type.FooterTemplate.Trim();
        var linkLine = string.Join(" | ", links.Select(link => $"[{link.Label}]({link.Url})"));
        return string.Join(Environment.NewLine + Environment.NewLine, new[] { template, linkLine }.Where(part => !string.IsNullOrWhiteSpace(part)));
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

    private static string FirstSentence(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.ReplaceLineEndings(" ").Trim();
        if (normalized.StartsWith(fallback, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[fallback.Length..].TrimStart(' ', '-', '—', ':', '.');
        }

        var end = normalized.IndexOfAny(['.', '!', '?']);
        var sentence = end > 20 ? normalized[..(end + 1)] : normalized;
        return Truncate(sentence, 200);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= max ? normalized : $"{normalized[..max]}…";
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static int? ReadInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string? ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record StoryView(int Index, Story Story, string? LeadUrl, IReadOnlyList<StoryItemView> Items);
    private sealed record StoryItemView(string Source, string Title, string Summary, string? Url);
    private sealed record DigestItem(int Index, string Headline, string Text);
    private sealed record DigestPostPick(int Index, PublicationKind? Kind);
    private sealed record DigestPlan(List<DigestItem> Items, List<DigestPostPick> Posts);
}
