using Microsoft.EntityFrameworkCore;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class PostTextGenerator(AppDbContext db, IAiProvider aiProvider) : IPostTextGenerator
{
    public async Task<PostTextResult> GenerateAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken)
    {
        var header = string.IsNullOrWhiteSpace(publicationType.HeaderTemplate)
            ? string.Empty
            : publicationType.HeaderTemplate.Trim();

        var footer = await BuildFooterAsync(channel.Id, publicationType, cancellationToken);
        var prompt = BuildPrompt(channel, publicationType, candidate);

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
                prompt),
            cancellationToken);

        var text = response.Provider == "local-fallback" || string.IsNullOrWhiteSpace(response.Text)
            ? BuildLocalText(publicationType, candidate)
            : response.Text.Trim();

        text = ClampText(TextSanitizer.Clean(text), publicationType.MaxTextLength);

        return new PostTextResult(
            text,
            header,
            footer,
            prompt,
            response.Provider,
            response.Model,
            response.PromptTokens,
            response.CompletionTokens,
            response.TotalTokens,
            response.CostAmount,
            response.CostCurrency,
            response.UsageMetadataJson);
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

    private static string BuildPrompt(Channel channel, PublicationTypeSetting publicationType, SourceCandidate candidate)
    {
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

        Инфоповод:
        Заголовок: {candidate.Title}
        URL: {candidate.Url}
        Видео: {candidate.VideoUrl}
        Резюме: {candidate.Summary}

        Требования:
        - русский язык;
        - коротко и по делу, как живой редактор Telegram-канала, а не пресс-релиз;
        - 2-4 коротких абзаца без маркированного списка, если список не нужен по смыслу;
        - первый абзац сразу сообщает новость, без разгона и без кликбейта;
        - объясняй последствия только если они реально следуют из инфоповода;
        - не используй шаблонные подзаголовки и обороты: "Почему это важно", "Что это значит", "Для игроков", "тревожный звоночек", "финальный босс", "экосистема", "бьёт по рынку", "стоит следить";
        - без искусственной драматизации, мемных концовок и канцелярита;
        - максимум один уместный эмодзи, лучше без эмодзи;
        - без неигровой повестки;
        - без длинных URL в тексте;
        - максимум {publicationType.MaxTextLength} символов;
        - если это слух, явно пометь это в начале.
        """;
    }

    private static string BuildLocalText(PublicationTypeSetting publicationType, SourceCandidate candidate)
    {
        var prefix = publicationType.Kind == PublicationKind.Rumor ? "Пока это слух: " : string.Empty;
        var summary = string.IsNullOrWhiteSpace(candidate.Summary) ? candidate.Title : candidate.Summary;
        summary = summary.ReplaceLineEndings(" ").Trim();

        return $"""
        {prefix}{candidate.Title}

        {summary}

        Коротко: инфоповод стоит проверить и решить, выпускать ли его в канал.
        """.Trim();
    }

    private static string ClampText(string text, int maxLength)
    {
        if (maxLength <= 0 || text.Length <= maxLength)
        {
            return text;
        }

        return $"{text[..Math.Max(0, maxLength - 3)]}...";
    }
}
