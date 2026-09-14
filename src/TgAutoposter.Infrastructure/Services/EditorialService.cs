using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Stories;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed record EditorialRunResult(
    Guid ChannelId,
    int StoriesReviewed,
    int StoriesRejected,
    Guid? StoryId,
    Guid? PostId,
    string Note);

/// <summary>
/// The "editor-in-chief": rates open stories for newsworthiness with one model call, dismisses the noise
/// (stream announcements, donations, fan chatter) and drafts a post from the single best story — respecting the
/// daily limit, the interval between posts and the channel's schedule windows.
/// </summary>
public sealed class EditorialService(
    AppDbContext db,
    StoryClusteringService clustering,
    IAiProvider aiProvider,
    IAutopostingPipeline pipeline,
    IDateTimeProvider clock,
    IOptions<EditorialOptions> optionsAccessor,
    ILogger<EditorialService> logger)
{
    private const string JudgeSystemPrompt = """
        Ты главный редактор русскоязычного Telegram-канала, который объединяет витуберов: новости RU-сцены и мировой VTuber-индустрии для самих витуберов и их зрителей.
        Оцени каждый сюжет по шкале 0–10 — насколько он стоит отдельного поста в канале.

        10–9: крупное событие: закрытие/запуск агентства, graduation или дебют известного таланта, скандал с официальными заявлениями, большой концерт, коллаб с крупным брендом или игрой, важное обновление VTube Studio / Live2D / стриминговых платформ.
        8–7: заметная новость: MV или оригинальная песня известного таланта, крупный майлстоун, новый ген крупного агентства, заметное событие RU-сцены (дебют, уход, новая модель у известного RU-витубера), крупный мерч или ивент.
        6–5: мелочь для узкого круга: небольшой коллаб, обычный кавер, мелкое агентство.
        4–0: не новость: анонс или старт обычного стрима, смена игры или статуса на стриме, просьбы о донатах и бусти, личные посты и болтовня, фан-арт, мемы, клипы и нарезки, обсуждения и мнения фанатов, розыгрыши, реклама.

        СНГ-сцена в приоритете: канал в первую очередь для русскоязычных витуберов. Реальные события RU/СНГ-витуберов (дебют, новая модель, уход или перерыв, запуск или набор агентства, премия, фестиваль, крупный коллаб, релиз песни, заметный майлстоун) оценивай на 2 балла выше, чем такое же событие у зарубежного инди-таланта, и ставь им rubric "RU-сцена". Рутина RU-витуберов (старт стрима, благодарности, личные посты) остаётся 0–3.
        Будь строг: в день набирается 3–5 новостей уровня 7+. Сомневаешься — ставь ниже.
        kind: News, BreakingNews (только для 9–10 срочных), Rumor (неподтверждённое), Trailer (главное — видео: MV, дебют-стрим, 3D-лайв), Deal (мерч, билеты, ивенты).
        rubric: одно-два слова для плашки на карточке: Дебют, Graduation, Музыка, Коллаб, Агентства, Индустрия, RU-сцена, Мерч, Ивент, Слух, Скандал, Софт, Майлстоун.

        Ответь СТРОГО одним JSON-объектом без markdown:
        {"items":[{"index":0,"score":7,"kind":"News","rubric":"Музыка","reason":"до 12 слов"}]}
        Оцени все сюжеты из списка.
        """;

    /// <summary>CIS-scene stories are the channel's core audience: a softer bar and a tie-break bonus.</summary>
    private static bool IsCisScene(Story story) =>
        string.Equals(story.EditorRubric, "RU-сцена", StringComparison.OrdinalIgnoreCase);

    private static int RequiredScore(Story story, EditorialOptions options) =>
        IsCisScene(story) ? Math.Max(options.RejectBelow, options.MinScore - options.CisScoreDiscount) : options.MinScore;

    public async Task<EditorialRunResult> RunAsync(Guid channelId, bool force, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        var channel = await db.Channels
            .Include(item => item.ScheduleWindows)
            .FirstOrDefaultAsync(item => item.Id == channelId && item.IsEnabled, cancellationToken);
        if (channel is null)
        {
            return new EditorialRunResult(channelId, 0, 0, null, null, "Канал не найден или выключен.");
        }

        var now = clock.UtcNow;
        var zone = ResolveTimeZone(channel.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);

        if (!force && options.RespectScheduleWindows && channel.ScheduleWindows.Count > 0 && !IsInsideWindow(channel, localNow))
        {
            return new EditorialRunResult(channel.Id, 0, 0, null, null, "Вне окон публикации.");
        }

        var localDayStart = localNow.Date;
        var dayStartUtc = new DateTimeOffset(localDayStart, zone.GetUtcOffset(localDayStart)).ToUniversalTime();
        var postsToday = await db.Posts
            .Where(post => post.ChannelId == channel.Id &&
                           post.CreatedAtUtc >= dayStartUtc &&
                           post.PublicationKind != PublicationKind.Digest &&
                           post.Status != PostStatus.Duplicate &&
                           post.Status != PostStatus.FactCheckFailed &&
                           post.Status != PostStatus.Rejected)
            .Select(post => post.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (!force && postsToday.Count >= channel.DailyPostLimit)
        {
            return new EditorialRunResult(channel.Id, 0, 0, null, null, $"Дневной лимит {channel.DailyPostLimit} исчерпан.");
        }

        var lastPostAt = postsToday.Count == 0 ? (DateTimeOffset?)null : postsToday.Max();
        var tooSoon = !force && lastPostAt is not null && lastPostAt.Value > now.AddMinutes(-options.MinMinutesBetweenPosts);

        await clustering.ClusterPendingAsync(channel, cancellationToken);

        var since = now.AddHours(-Math.Max(1, options.LookbackHours));
        var stories = await db.Stories
            .Where(story => story.ChannelId == channel.Id && story.Status == StoryStatus.Open && story.LastSeenAtUtc >= since)
            .OrderByDescending(story => story.Score)
            .ThenByDescending(story => story.LastSeenAtUtc)
            .Take(Math.Max(5, options.MaxStoriesPerReview) * 3)
            .ToListAsync(cancellationToken);

        var toJudge = stories
            .Where(story => story.EditorCheckedAtUtc is null || story.CandidatesCount > story.EditorCandidatesCount)
            .Take(Math.Max(5, options.MaxStoriesPerReview))
            .ToList();
        if (tooSoon)
        {
            // Between posts only look out for something breaking — no point paying to rate ordinary stories yet.
            toJudge = toJudge.Where(story => story.IsBreaking).ToList();
        }

        var rejected = 0;
        if (toJudge.Count > 0)
        {
            var verdicts = await JudgeAsync(channel, toJudge, cancellationToken);
            if (verdicts is null)
            {
                return new EditorialRunResult(channel.Id, 0, 0, null, null, "Редактор не ответил (AI недоступен или ответ не разобран).");
            }

            for (var i = 0; i < toJudge.Count; i++)
            {
                var story = toJudge[i];
                story.EditorCheckedAtUtc = now;
                story.EditorCandidatesCount = story.CandidatesCount;
                if (!verdicts.TryGetValue(i, out var verdict))
                {
                    story.EditorScore = 4;
                    story.EditorNote = "редактор не оценил";
                    story.Status = StoryStatus.Dismissed;
                    rejected++;
                    continue;
                }

                story.EditorScore = verdict.Score;
                story.EditorNote = Truncate(verdict.Reason, 480);
                story.EditorRubric = Truncate(verdict.Rubric, 38);
                if (verdict.Kind is not null)
                {
                    story.KindHint = verdict.Kind;
                }

                if (verdict.Score < options.RejectBelow)
                {
                    story.Status = StoryStatus.Dismissed;
                    rejected++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var pick = stories
            .Where(story => story.Status == StoryStatus.Open && story.LeadCandidateId is not null && story.EditorScore >= RequiredScore(story, options))
            .OrderByDescending(story => story.EditorScore + (IsCisScene(story) ? 1 : 0))
            .ThenByDescending(story => story.Score)
            .FirstOrDefault();

        if (pick is null)
        {
            return new EditorialRunResult(channel.Id, toJudge.Count, rejected, null, null, "Нет сюжетов уровня публикации.");
        }

        if (tooSoon && pick.EditorScore < options.BreakingScore)
        {
            return new EditorialRunResult(channel.Id, toJudge.Count, rejected, null, null, "Рано: выдерживаем интервал между постами.");
        }

        var kind = await ResolveKindAsync(pick, cancellationToken);
        var result = await pipeline.RunForChannelAsync(
            channel.Id,
            new PipelineRunOptions(
                MaxPostsToCreate: 1,
                BypassDailyLimit: true,
                PublicationKind: kind,
                CollectSources: false,
                CandidateId: pick.LeadCandidateId,
                StoryId: pick.Id),
            cancellationToken);

        if (result.PostsCreated == 0)
        {
            var reason = result.DuplicatesSkipped > 0 ? "дубль" : result.FactCheckFailed > 0 ? "не прошёл фактчек" : result.Warnings.FirstOrDefault() ?? "без причины";
            pick.Status = StoryStatus.Dismissed;
            pick.EditorNote = Truncate($"{pick.EditorNote} | пост не создан: {reason}", 480);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Editorial: story {StoryId} not drafted: {Reason}.", pick.Id, reason);
            return new EditorialRunResult(channel.Id, toJudge.Count, rejected, pick.Id, null, $"Сюжет «{Truncate(pick.Title, 80)}» не стал постом: {reason}.");
        }

        logger.LogInformation("Editorial: drafted story {StoryId} (score {Score}) as post {PostId}.", pick.Id, pick.EditorScore, pick.PostId);
        return new EditorialRunResult(channel.Id, toJudge.Count, rejected, pick.Id, pick.PostId, $"Выбран сюжет «{Truncate(pick.Title, 80)}» (оценка {pick.EditorScore}).");
    }

    private sealed record Verdict(int Score, PublicationKind? Kind, string? Rubric, string? Reason);

    private async Task<Dictionary<int, Verdict>?> JudgeAsync(Channel channel, List<Story> stories, CancellationToken cancellationToken)
    {
        var ids = stories.Select(story => story.Id).ToList();
        var candidates = await db.SourceCandidates
            .AsNoTracking()
            .Where(candidate => candidate.StoryId != null && ids.Contains(candidate.StoryId.Value))
            .Select(candidate => new { StoryId = candidate.StoryId!.Value, candidate.Title, candidate.Summary, candidate.SourceId, candidate.Score, candidate.FoundAtUtc })
            .ToListAsync(cancellationToken);
        var sourceNames = await db.Sources
            .AsNoTracking()
            .Where(source => source.ChannelId == channel.Id)
            .ToDictionaryAsync(source => source.Id, source => source.Name, cancellationToken);

        var user = new StringBuilder();
        user.AppendLine($"Сюжеты ({stories.Count}):");
        for (var i = 0; i < stories.Count; i++)
        {
            var story = stories[i];
            user.AppendLine();
            user.AppendLine($"[{i}] {Truncate(story.Title, 200)}");
            user.AppendLine($"    источников: {story.SourcesCount}, упоминаний: {story.CandidatesCount}{(string.IsNullOrWhiteSpace(story.TalentsCsv) ? string.Empty : $", таланты: {story.TalentsCsv}")}");
            foreach (var item in candidates.Where(candidate => candidate.StoryId == story.Id).OrderByDescending(candidate => candidate.Score ?? 0).Take(3))
            {
                var name = sourceNames.TryGetValue(item.SourceId, out var sourceName) ? sourceName : "источник";
                var summary = Truncate(item.Summary.ReplaceLineEndings(" "), 260);
                user.AppendLine($"    - ({name}) {Truncate(item.Title, 160)}{(summary.Length > 0 && !summary.StartsWith(item.Title, StringComparison.OrdinalIgnoreCase) ? $": {summary}" : string.Empty)}");
            }
        }

        AiResponse response;
        try
        {
            response = await aiProvider.CompleteAsync(
                new AiRequest(channel.Id, AiTaskType.Classification, JudgeSystemPrompt, user.ToString(), RequireJson: true, MaxTokens: 2500),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Editorial judge call failed for channel {ChannelId}.", channel.Id);
            return null;
        }

        db.AiUsageRecords.Add(new AiUsageRecord
        {
            ChannelId = channel.Id,
            Provider = response.Provider,
            Model = response.Model,
            TaskType = AiTaskType.Classification,
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

        var text = response.Text;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<int, Verdict>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var indexEl) || !indexEl.TryGetInt32(out var index) || index < 0 || index >= stories.Count)
                {
                    continue;
                }

                var score = item.TryGetProperty("score", out var scoreEl) && scoreEl.TryGetInt32(out var parsedScore) ? Math.Clamp(parsedScore, 0, 10) : 0;
                PublicationKind? kind = item.TryGetProperty("kind", out var kindEl) &&
                                        Enum.TryParse<PublicationKind>(kindEl.GetString(), true, out var parsedKind) &&
                                        parsedKind is not (PublicationKind.Digest or PublicationKind.Meme)
                    ? parsedKind
                    : null;
                var rubric = item.TryGetProperty("rubric", out var rubricEl) && rubricEl.ValueKind == JsonValueKind.String ? rubricEl.GetString() : null;
                var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String ? reasonEl.GetString() : null;
                result[index] = new Verdict(score, kind, rubric, reason);
            }

            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Editorial judge returned invalid JSON for channel {ChannelId}.", channel.Id);
            return null;
        }
    }

    /// <summary>Keep the editor's kind only if the lead candidate's source allows it; otherwise let the pipeline classify.</summary>
    private async Task<PublicationKind?> ResolveKindAsync(Story story, CancellationToken cancellationToken)
    {
        if (story.KindHint is null || story.LeadCandidateId is null)
        {
            return null;
        }

        var allowed = await db.SourceCandidates
            .Where(candidate => candidate.Id == story.LeadCandidateId)
            .Select(candidate => candidate.Source!.AllowedPublicationKindsCsv)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(allowed))
        {
            return story.KindHint;
        }

        return allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Equals(story.KindHint.Value.ToString(), StringComparison.OrdinalIgnoreCase))
            ? story.KindHint
            : null;
    }

    private static bool IsInsideWindow(Channel channel, DateTimeOffset localNow)
    {
        var time = TimeOnly.FromDateTime(localNow.DateTime);
        return channel.ScheduleWindows.Any(window =>
            (window.DayOfWeek is null || window.DayOfWeek == localNow.DayOfWeek) &&
            time >= window.StartTime && time <= window.EndTime);
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

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
