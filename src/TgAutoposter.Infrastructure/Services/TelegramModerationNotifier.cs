using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class TelegramModerationNotifier(
    TelegramHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> optionsAccessor,
    AppDbContext db,
    ILogger<TelegramModerationNotifier> logger) : IModerationNotifier
{
    public async Task NotifyAsync(Channel channel, Post post, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        var moderatorChatIds = options.GetModeratorChatIds();
        if (string.IsNullOrWhiteSpace(options.BotToken) || moderatorChatIds.Count == 0)
        {
            logger.LogInformation("Moderation notification skipped for post {PostId}: Telegram moderation chat is not configured.", post.Id);
            return;
        }

        var text = $"""
        Пост ожидает модерации

        Канал: {channel.Name}
        Тип: {post.PublicationKind}
        Статус фактчека: {post.FactCheckStatus}
        Дедупликация: {post.DeduplicationStatus}
        План: {post.ScheduledForUtc:yyyy-MM-dd HH:mm} UTC

        {post.Header}

        {post.FinalText ?? post.GeneratedText}

        {BuildVideoLine(post)}

        {post.Footer}
        """;

        var replyMarkup = JsonSerializer.Serialize(new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "✅ Публикуем", callback_data = $"moderate:publish:{post.Id}" },
                    new { text = "🛑 Не публикуем", callback_data = $"moderate:reject:{post.Id}" }
                },
                new[]
                {
                    new { text = "🖼 Переделать картинку", callback_data = $"moderate:image:{post.Id}" },
                    new { text = "✍️ Переписать", callback_data = $"moderate:rewrite:{post.Id}" }
                }
            }
        });

        using var client = httpClientFactory.CreateClient();
        foreach (var chatId in moderatorChatIds)
        {
        var photoUrl = NormalizeTelegramPhotoUrl(post.ImagePath);
        var imageMessageId = string.IsNullOrWhiteSpace(photoUrl)
                ? null
                : await TrySendImagePreviewAsync(client, options, chatId, post, photoUrl, cancellationToken);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = ClampText(text),
                ["disable_web_page_preview"] = "false",
                ["reply_markup"] = replyMarkup
            });

            using var response = await client.PostAsync($"https://api.telegram.org/bot{options.BotToken}/sendMessage", content, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Telegram moderation notification failed for post {PostId} and chat {ChatId}: {Response}", post.Id, chatId, raw);
                continue;
            }

            var textMessageId = ExtractMessageId(raw);
            if (textMessageId is null)
            {
                logger.LogWarning("Telegram moderation notification for post {PostId} returned no message id: {Response}", post.Id, raw);
                continue;
            }

            db.ModerationMessages.Add(new ModerationMessage
            {
                PostId = post.Id,
                ChatId = chatId,
                TextMessageId = textMessageId.Value,
                ImageMessageId = imageMessageId,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int?> TrySendImagePreviewAsync(
        HttpClient client,
        TelegramOptions options,
        string chatId,
        Post post,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["photo"] = photoUrl,
            ["caption"] = ClampCaption($"Картинка к посту: {post.SourceTitle}")
        });

        using var response = await client.PostAsync($"https://api.telegram.org/bot{options.BotToken}/sendPhoto", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Telegram moderation image preview failed for post {PostId}: {Response}", post.Id, raw);
            return null;
        }

        return ExtractMessageId(raw);
    }

    private static string? NormalizeTelegramPhotoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
            uri.Host.Equals("preview.redd.it", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Host = "i.redd.it",
                Query = string.Empty
            };

            return builder.Uri.ToString();
        }

        return value.Trim();
    }

    private static string ClampText(string text)
    {
        const int limit = 3900;
        var value = text.Trim();
        return value.Length <= limit ? value : $"{value[..(limit - 3)].TrimEnd()}...";
    }

    private static string? BuildVideoLine(Post post)
    {
        return string.IsNullOrWhiteSpace(post.VideoUrl)
            ? null
            : $"Видео/трейлер: {post.VideoUrl}";
    }

    private static string ClampCaption(string text)
    {
        const int limit = 1024;
        var value = text.Trim();
        return value.Length <= limit ? value : $"{value[..(limit - 3)].TrimEnd()}...";
    }

    private static int? ExtractMessageId(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.TryGetProperty("result", out var result) &&
               result.TryGetProperty("message_id", out var messageId) &&
               messageId.TryGetInt32(out var value)
            ? value
            : null;
    }
}
