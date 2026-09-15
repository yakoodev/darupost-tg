using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// Writes the post body and a short card headline in one call (JSON). Story context (other sources on the same
/// event) is passed in so the text can combine facts instead of retelling a single thin source.
/// </summary>
public sealed class PostTextGenerator(AppDbContext db, IAiProvider aiProvider, INicheProfileProvider profiles) : IPostTextGenerator
{
    private static readonly string[] BannedPhrases =
    [
        "агентство не указано", "агентство в исходной новости не указано", "подробностей нет", "деталей пока нет",
        "официальных деталей", "в доступном резюме", "в доступном анонсе", "дальше ждём", "дальше стоит ждать",
        "стоит следить", "если следите за", "подробностей в источнике нет", "других подробностей", "в источнике нет",
        "больше подробностей нет", "деталей не сообщается", "подробности не раскрываются", "пока неизвестно"
    ];

    public async Task<PostTextResult> GenerateAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken,
        string? storyContext = null,
        string? talentFacts = null)
    {
        var header = string.IsNullOrWhiteSpace(publicationType.HeaderTemplate)
            ? string.Empty
            : publicationType.HeaderTemplate.Trim();

        var footer = await BuildFooterAsync(channel.Id, publicationType, cancellationToken);
        var prompt = BuildPrompt(channel, profiles.Get(channel.ProfileKey), publicationType, candidate, storyContext, talentFacts);

        if (publicationType.Kind == PublicationKind.Meme)
        {
            return new PostTextResult(
                string.Empty,
                header,
                footer,
                "meme-image-only",
                "local",
                "none",
                CostAmount: 0,
                CostCurrency: "RUB",
                UsageMetadataJson: """{"reason":"Meme posts publish image only; text generation skipped."}""");
        }

        var response = await aiProvider.CompleteAsync(
            new AiRequest(
                channel.Id,
                AiTaskType.PostGeneration,
                channel.SystemPrompt,
                prompt,
                RequireJson: true,
                MaxTokens: 2000,
                Temperature: 0.7),
            cancellationToken);

        string text;
        string? headline = null;
        AiResponse? critic = null;
        if (response.Provider == "local-fallback" || string.IsNullOrWhiteSpace(response.Text))
        {
            text = BuildLocalText(publicationType, candidate);
        }
        else
        {
            (headline, text) = ParseResponse(response.Text);

            // Second pass: a sub-editor checks the draft against the sources and removes invented facts, translationese and clichés.
            critic = await CheckDraftAsync(channel, headline, text, candidate, storyContext, talentFacts, cancellationToken);
            if (critic is not null && !string.IsNullOrWhiteSpace(critic.Text))
            {
                var (checkedHeadline, checkedText) = ParseResponse(critic.Text);
                if (!string.IsNullOrWhiteSpace(checkedText) && checkedText.Length >= 80)
                {
                    text = checkedText;
                    headline = string.IsNullOrWhiteSpace(checkedHeadline) ? headline : checkedHeadline;
                }
            }
        }

        text = ClampText(TextSanitizer.Clean(text), publicationType.MaxTextLength);
        headline = CleanHeadline(headline);

        return new PostTextResult(
            text,
            header,
            footer,
            prompt,
            response.Provider,
            response.Model,
            Sum(response.PromptTokens, critic?.PromptTokens),
            Sum(response.CompletionTokens, critic?.CompletionTokens),
            Sum(response.TotalTokens, critic?.TotalTokens),
            response.CostAmount is null && critic?.CostAmount is null ? null : (response.CostAmount ?? 0) + (critic?.CostAmount ?? 0),
            response.CostCurrency,
            response.UsageMetadataJson,
            headline);
    }

    private async Task<string> BuildFooterAsync(Guid channelId, PublicationTypeSetting publicationType, CancellationToken cancellationToken)
    {
        var links = await db.FooterLinks
            .Where(link => link.ChannelId == channelId && link.IsEnabled)
            .OrderBy(link => link.SortOrder)
            .Take(3)
            .ToListAsync(cancellationToken);

        var filtered = links
            .Where(link => string.IsNullOrWhiteSpace(link.PublicationKindsCsv) ||
                           link.PublicationKindsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Any(value => value.Equals(publicationType.Kind.ToString(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var template = string.IsNullOrWhiteSpace(publicationType.FooterTemplate)
            ? string.Empty
            : publicationType.FooterTemplate.Trim();
        var linkLine = string.Join(" | ", filtered.Select(link => $"[{link.Label}]({link.Url})"));

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            new[] { template, linkLine }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatNow(string? timeZone)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? "Europe/Moscow" : timeZone);
            return $"{TimeZoneInfo.ConvertTime(now, zone):dd.MM.yyyy HH:mm} ({zone.Id})";
        }
        catch (Exception)
        {
            return $"{now:dd.MM.yyyy HH:mm} UTC";
        }
    }

    private static string FormatAgo(DateTimeOffset foundAtUtc)
    {
        var age = DateTimeOffset.UtcNow - foundAtUtc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalHours < 1 ? $"{Math.Max(1, (int)age.TotalMinutes)} мин назад" : age.TotalHours < 48 ? $"{(int)age.TotalHours} ч назад" : $"{(int)age.TotalDays} дн назад";
    }

    private static string BuildPrompt(
        Channel channel,
        NicheProfile profile,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        string? storyContext,
        string? talentFacts = null)
    {
        var rules = new List<string> { "- русский язык;" };
        rules.AddRange(profile.Prompts.TextRules
            .Select(rule => rule.TrimStart('-', ' ').Trim())
            .Where(rule => rule.Length > 0)
            .Select(rule => $"- {rule}"));
        rules.Add($"- текст не длиннее {publicationType.MaxTextLength} символов;");
        rules.Add("- пиши только то, что есть в источниках; не сообщай, чего в них нет (никаких «агентство не указано», «подробностей пока нет», «официальных деталей нет»);");
        rules.Add("- никаких концовок-ожиданий и призывов («дальше ждём», «стоит следить», «если вы фанат — заходите»); закончи последним фактом;");
        rules.Add("- если это слух, явно пометь это в первом предложении.");
        rules.Add("- не называй пользователей Reddit, фанатов и ники из комментариев источником новости; если сведения только фанатские, пиши «по данным фанатов» или не публикуй факт как подтверждённый;");
        rules.Add("- сверяй даты с текущим временем: если событие (стрим, дебют, премьера) уже прошло, пиши о нём в прошедшем времени и не подавай как анонс;");

        var context = string.IsNullOrWhiteSpace(storyContext)
            ? string.Empty
            : $"""

        Другие источники об этом же событии (используй факты, не противоречь им):
        {storyContext}
        """;

        return $"""
        Сформируй Telegram-пост для канала "{channel.Name}".

        Позиционирование:
        {channel.Positioning}

        Стиль:
        {channel.StyleGuide}

        Тип публикации:
        {publicationType.Name}

        Дополнительные правила типа:
        {publicationType.SystemPrompt}

        Сейчас: {FormatNow(channel.TimeZone)}

        Инфоповод:
        Заголовок: {candidate.Title}
        URL: {candidate.Url}
        Опубликовано в источнике: {FormatLocal(candidate.FoundAtUtc, channel.TimeZone)} ({FormatAgo(candidate.FoundAtUtc)})
        Видео: {candidate.VideoUrl}
        Текст источника:
        {SourceText(candidate)}

        {DescribeTalentFacts(talentFacts)}
        {context}

        Требования к тексту:
        {string.Join(Environment.NewLine, rules)}

        Требования к заголовку карточки (headline):
        - 4–9 слов, не длиннее 70 символов;
        - по-русски, имена талантов и агентств латиницей;
        - кто + что произошло, конкретно; без кавычек, эмодзи, двоеточий-кликбейта и точки в конце;
        - не копируй первое предложение текста дословно.

        Верни СТРОГО один JSON-объект без markdown:
        {"{"}"headline": "...", "text": "..."{"}"}
        """;
    }

    private async Task<AiResponse?> CheckDraftAsync(
        Channel channel,
        string? headline,
        string text,
        SourceCandidate candidate,
        string? storyContext,
        string? talentFacts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const string system = """
            Ты выпускающий редактор русскоязычного Telegram-канала о витуберах. Проверь черновик поста по источникам и исправь его.
            - Убери любой факт, которого нет в источниках или в фактах о героях: придуманные биографии, цифры, даты, причины, оценки.
            - Перепиши кальки с английского и японского на естественный русский; иероглифы не оставляй.
            - Убери штампы и воду: «стоит отметить», «в мире VTuber-индустрии», «не просто X, а Y», «невероятно», «друзья», восклицания, риторические вопросы, призывы в конце.
            - Первое предложение — факт: кто, что и когда. Итог — 3–5 предложений, 1–2 абзаца.
            - Убери фразы о том, чего нет в источнике («других подробностей нет», «пока неизвестно»), и не подавай пользователя Reddit или фаната как источник новости.
            - Не добавляй ничего нового от себя. Если черновик уже хороший, верни его почти без изменений.
            Верни СТРОГО один JSON-объект без markdown: {"headline": "...", "text": "...", "issues": ["что исправил, коротко"]}
            """;

        var user = new StringBuilder()
            .AppendLine("Источник:")
            .AppendLine($"Заголовок: {candidate.Title}")
            .AppendLine($"Опубликовано: {FormatLocal(candidate.FoundAtUtc, channel.TimeZone)}")
            .AppendLine(SourceText(candidate))
            .AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(storyContext) ? "Других источников нет." : "Другие источники:" + Environment.NewLine + storyContext)
            .AppendLine()
            .AppendLine(DescribeTalentFacts(talentFacts))
            .AppendLine()
            .AppendLine("Черновик:")
            .AppendLine($"headline: {headline}")
            .AppendLine($"text: {text}")
            .ToString();

        try
        {
            return await aiProvider.CompleteAsync(
                new AiRequest(channel.Id, AiTaskType.Rewrite, system, user, RequireJson: true, MaxTokens: 2000, Temperature: 0.2),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string SourceText(SourceCandidate candidate)
    {
        var value = string.IsNullOrWhiteSpace(candidate.RawText) ? candidate.Summary : candidate.RawText;
        value = (value ?? string.Empty).Trim();
        return value.Length <= 2500 ? value : value[..2500] + "…";
    }

    private static string DescribeTalentFacts(string? talentFacts)
    {
        return string.IsNullOrWhiteSpace(talentFacts)
            ? "Фактов о героях из реестра нет: не пиши биографию и не угадывай агентство."
            : "Факты о героях из реестра канала (используй только их для контекста):" + Environment.NewLine + talentFacts;
    }

    private static string FormatLocal(DateTimeOffset utc, string? timeZone)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZone) ? "Europe/Moscow" : timeZone);
            return $"{TimeZoneInfo.ConvertTime(utc, zone):dd.MM.yyyy HH:mm}";
        }
        catch (Exception)
        {
            return $"{utc:dd.MM.yyyy HH:mm} UTC";
        }
    }

    private static int? Sum(int? first, int? second) => first is null && second is null ? null : (first ?? 0) + (second ?? 0);

    private static (string? Headline, string Text) ParseResponse(string raw)
    {
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed[start..(end + 1)]);
                var root = doc.RootElement;
                var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String ? textEl.GetString() : null;
                var headline = root.TryGetProperty("headline", out var headEl) && headEl.ValueKind == JsonValueKind.String ? headEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return (headline, text.Trim());
                }
            }
            catch (JsonException)
            {
                // fall through: treat the whole response as text
            }
        }

        return (null, trimmed);
    }

    private static string? CleanHeadline(string? headline)
    {
        if (string.IsNullOrWhiteSpace(headline))
        {
            return null;
        }

        var value = TextSanitizer.Clean(headline).ReplaceLineEndings(" ").Trim().Trim('"', '.', ' ');
        const int limit = 70;
        if (value.Length > limit)
        {
            // Shorten at a word boundary so the card never shows a phrase cut in the middle.
            var cut = value.LastIndexOf(' ', limit - 1);
            value = (cut > 20 ? value[..cut] : value[..limit]).TrimEnd(' ', ',', ':', ';', '—', '-', '«') + "…";
        }

        // Balance Russian quotes: drop a dangling opening one or close it.
        var opens = value.Count(ch => ch == '«');
        var closes = value.Count(ch => ch == '»');
        if (opens > closes)
        {
            value = value.EndsWith('…') ? value[..^1] + "»…" : value + "»";
        }
        else if (closes > opens)
        {
            value = value.Replace("»", string.Empty);
        }

        return value.Trim();
    }

    private static string BuildLocalText(PublicationTypeSetting publicationType, SourceCandidate candidate)
    {
        var prefix = publicationType.Kind == PublicationKind.Rumor ? "Пока это слух: " : string.Empty;
        var summary = string.IsNullOrWhiteSpace(candidate.Summary) ? candidate.Title : candidate.Summary;
        summary = summary.ReplaceLineEndings(" ").Trim();

        return $"""
        {prefix}{candidate.Title}

        {summary}
        """.Trim();
    }

    /// <summary>Drops trailing "waiting/next" filler sentences the model sometimes still adds, then clamps at a sentence boundary.</summary>
    private static string ClampText(string text, int maxLength)
    {
        // Drop filler sentences ("no further details", "stay tuned") wherever they are, keeping the rest of the paragraph.
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(paragraph => string.Join(" ", System.Text.RegularExpressions.Regex.Split(paragraph, "(?<=[.!?…])\\s+")
                .Where(sentence => !BannedPhrases.Any(phrase => sentence.Contains(phrase, StringComparison.OrdinalIgnoreCase)))))
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
            .ToList();

        text = paragraphs.Count == 0 ? text : string.Join("\n\n", paragraphs);
        if (maxLength <= 0 || text.Length <= maxLength)
        {
            return text;
        }

        var cut = text[..maxLength];
        var lastStop = cut.LastIndexOfAny(['.', '!', '?']);
        return lastStop > maxLength / 2 ? cut[..(lastStop + 1)] : $"{cut[..Math.Max(0, maxLength - 1)]}…";
    }
}
