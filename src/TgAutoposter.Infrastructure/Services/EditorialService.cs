using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Pipeline;
using TgAutoposter.Application.Profiles;
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
    INicheProfileProvider profiles,
    IDateTimeProvider clock,
    IOptions<EditorialOptions> optionsAccessor,
    ILogger<EditorialService> logger)
{
    private const string JudgeSystemPrompt = """
        Ты главный редактор русскоязычного Telegram-канала для витуберов и их зрителей. Приоритет — RU/СНГ-сцена; мировая VTuber-индустрия — только крупные события.
        Оцени каждый сюжет по шкале 0–10: насколько вероятно, что владелец канала опубликует его отдельным постом.

        Как решает владелец канала (по его реальным решениям).
        ПУБЛИКУЕТ:
        - официальные новости крупных агентств с понятным фактом: обновления, новый ген, концерты, закрытия, уходы известных талантов;
        - события, где пересекаются агентства или большие аудитории;
        - настоящие события известных RU-витуберов (помечены [RU★]): новая модель, сбор или аукцион, уход или перерыв, крупный коллаб, премия, резонансное высказывание.
        ОТКЛОНЯЕТ:
        - дебюты, каверы, MV и анонсы стримов малоизвестных талантов, в том числе русскоязычных;
        - пересказы японских пресс-релизов мелких агентств, мерч и голосовые пакеты;
        - самопромо, комиссии, расписания, благодарности, личный быт, фан-посты и обсуждения;
        - повторы уже опубликованного и анонсы событий, которые уже прошли.

        Примеры его решений:
        - «hololive обновляет FANCLUB ко второму году» (официальный аккаунт агентства) → 7, опубликовал
        - «Mori Calliope позвала на 3D-концерт гостей из других агентств» → 7, опубликовал
        - «Planya пошутила над исследованием ТАСС» [RU★], обсуждали широко → 6, опубликовал
        - «Снежа просит помочь со сбором на домик» [RU★] → 6, опубликовал
        - «Lyutomir проведёт дебютный стрим» (малоизвестный RU-витубер) → 3, отклонил
        - «У Aki вышел кавер на Blood» (малоизвестный) → 3, отклонил
        - «Moona Hoshinova выпустила MV с KARDI» (обычный релиз без события) → 5, отклонил
        - «Агентство くえにこ! дебютировало двух талантов» (пресс-релиз мелкого агентства) → 2, отклонил
        - «AuroraRisalia открыли комиссии на монтаж» (самопромо) → 0, отклонил
        - «Kaibutsu: расписание стримов на неделю» → 0

        У каждого упоминания указана роль источника: агентство (официальный аккаунт), новостник, личный канал, сообщество (Reddit, фанаты), поиск (ИИ-поиск по вебу).
        Подтверждение: 7+ только если среди упоминаний есть агентство или новостник, либо 2+ независимых источника. Сюжет только из сообщества, поиска или личного канала малоизвестного таланта — максимум 5. Личный канал известного RU-витубера [RU★] сам считается первоисточником.
        Время: указаны текущее время, когда сюжет впервые появился и возраст каждого упоминания. Анонс события, которое уже прошло, — 0–2. Сюжет старше 48 часов без нового факта — максимум 4.
        Повторы: в запросе список недавно опубликованного. Тот же сюжет — 0–2; продолжение — только при существенном новом факте, иначе максимум 4.
        Шкала: 9–10 — главное событие дня; 7–8 — уверенная публикация; 6 — публикуем только для RU-сцены; 5 и ниже — не публикуем. Сомневаешься — ставь ниже.
        kind: News, BreakingNews (только для 9–10 срочных), Rumor (неподтверждённое), Trailer (главное — видео: MV, дебют, 3D-лайв), Deal (мерч, билеты, ивенты).
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

    private static string FormatLocal(DateTimeOffset utc, string? timeZone)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? "Europe/Moscow" : timeZone);
            return $"{TimeZoneInfo.ConvertTime(utc, zone):dd.MM.yyyy HH:mm} ({zone.Id})";
        }
        catch (Exception)
        {
            return $"{utc:dd.MM.yyyy HH:mm} UTC";
        }
    }

    /// <summary>Editorial role of a source from the niche profile (matched by URL, subreddit or name), with a kind-based fallback.</summary>
    private static string ResolveRole(NicheProfile profile, string name, string? url, string? subreddit, SourceKind kind)
    {
        var match = profile.Sources.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(url) && string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(subreddit) && string.Equals(item.Subreddit, subreddit, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match?.Role))
        {
            return match.Role!;
        }

        return kind switch
        {
            SourceKind.AiWebSearch => "search",
            SourceKind.Reddit => "community",
            SourceKind.Rss or SourceKind.Web => "newsline",
            SourceKind.Telegram or SourceKind.YouTube => "personal",
            _ => "community"
        };
    }

    private static string RoleLabel(string role) => role switch
    {
        "agency" => "агентство",
        "newsline" => "новостник",
        "personal" => "личный канал",
        "community" => "сообщество",
        "search" => "поиск",
        "meme" => "мемы",
        _ => "источник"
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalHours < 1 ? $"{Math.Max(1, (int)age.TotalMinutes)} мин назад" : age.TotalHours < 48 ? $"{(int)age.TotalHours} ч назад" : $"{(int)age.TotalDays} дн назад";
    }

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
                           post.PublicationKind != PublicationKind.Meme &&
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
            .OrderByDescending(story => story.EditorScore)
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

        var profile = profiles.Get(channel.ProfileKey);
        var sourceRoles = (await db.Sources
                .AsNoTracking()
                .Where(source => source.ChannelId == channel.Id)
                .Select(source => new { source.Id, source.Name, source.Url, source.Subreddit, source.Kind })
                .ToListAsync(cancellationToken))
            .ToDictionary(source => source.Id, source => RoleLabel(ResolveRole(profile, source.Name, source.Url, source.Subreddit, source.Kind)));
        var talentInfo = (await db.Talents
                .AsNoTracking()
                .Where(talent => talent.ChannelId == channel.Id && talent.IsActive)
                .Select(talent => new { talent.Name, talent.Agency, talent.Group, talent.Priority })
                .ToListAsync(cancellationToken))
            .GroupBy(talent => talent.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var recentPublished = await db.Posts
            .AsNoTracking()
            .Where(post => post.ChannelId == channel.Id && post.Status == PostStatus.Published && post.PublishedAtUtc >= clock.UtcNow.AddDays(-30))
            .OrderByDescending(post => post.PublishedAtUtc)
            .Take(20)
            .Select(post => post.Headline ?? post.SourceTitle)
            .ToListAsync(cancellationToken);

        string DescribeTalents(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
            {
                return string.Empty;
            }

            var described = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(name =>
            {
                if (!talentInfo.TryGetValue(name, out var talent))
                {
                    return name;
                }

                var isRu = (talent.Agency ?? string.Empty).Contains("RU", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(talent.Group, "RU", StringComparison.OrdinalIgnoreCase);
                if (isRu && talent.Priority <= 2)
                {
                    return $"{talent.Name} [RU★]";
                }

                return string.IsNullOrWhiteSpace(talent.Agency) ? talent.Name : $"{talent.Name} ({talent.Agency})";
            });
            return $", таланты: {string.Join(", ", described)}";
        }

        var user = new StringBuilder();
        var nowUtc = clock.UtcNow;
        user.AppendLine($"Сейчас: {FormatLocal(nowUtc, channel.TimeZone)}.");
        user.AppendLine(recentPublished.Count == 0
            ? "Недавно опубликовано: ничего."
            : "Недавно опубликовано (30 дней):" + Environment.NewLine + string.Join(Environment.NewLine, recentPublished.Select(title => $"  • {Truncate(title, 120)}")));
        user.AppendLine($"Сюжеты ({stories.Count}):");
        for (var i = 0; i < stories.Count; i++)
        {
            var story = stories[i];
            user.AppendLine();
            user.AppendLine($"[{i}] {Truncate(story.Title, 200)}");
            user.AppendLine($"    впервые: {FormatAge(nowUtc - story.FirstSeenAtUtc)}, источников: {story.SourcesCount}, упоминаний: {story.CandidatesCount}{DescribeTalents(story.TalentsCsv)}");
            foreach (var item in candidates.Where(candidate => candidate.StoryId == story.Id).OrderByDescending(candidate => candidate.Score ?? 0).Take(3))
            {
                var name = sourceNames.TryGetValue(item.SourceId, out var sourceName) ? sourceName : "источник";
                // Summaries are built as "title\nbody": strip the title prefix so the judge actually sees what the post says.
                var body = item.Summary ?? string.Empty;
                if (body.StartsWith(item.Title, StringComparison.OrdinalIgnoreCase))
                {
                    body = body[item.Title.Length..];
                }

                body = Truncate(body.ReplaceLineEndings(" ").Trim(), 500);
                var role = sourceRoles.TryGetValue(item.SourceId, out var sourceRole) ? sourceRole : "источник";
                user.AppendLine($"    - ({name}, {role}, {FormatAge(nowUtc - item.FoundAtUtc)}) {Truncate(item.Title, 160)}{(body.Length > 0 ? $": {body}" : string.Empty)}");
            }
        }

        AiResponse response;
        try
        {
            response = await aiProvider.CompleteAsync(
                new AiRequest(channel.Id, AiTaskType.Classification, JudgeSystemPrompt, user.ToString(), RequireJson: true, MaxTokens: 2500, Temperature: 0.2),
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
            CostAmount = response.CostAmount,
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

    internal static bool IsInsideWindow(Channel channel, DateTimeOffset localNow)
    {
        var time = TimeOnly.FromDateTime(localNow.DateTime);
        return channel.ScheduleWindows.Any(window =>
            (window.DayOfWeek is null || window.DayOfWeek == localNow.DayOfWeek) &&
            time >= window.StartTime && time <= window.EndTime);
    }

    internal static TimeZoneInfo ResolveTimeZone(string id)
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
