using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class TelegramModerationWorker(
    IServiceScopeFactory scopeFactory,
    TelegramHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> optionsAccessor,
    IRealtimeNotifier realtimeNotifier,
    ILogger<TelegramModerationWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? offset = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsAccessor.Value;
            if (string.IsNullOrWhiteSpace(options.BotToken) || options.GetModeratorChatIds().Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            try
            {
                var callbacks = await GetCallbacksAsync(options, offset, stoppingToken);
                foreach (var callback in callbacks)
                {
                    offset = Math.Max(offset ?? 0, callback.UpdateId + 1);
                    await HandleCallbackAsync(options, callback, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram moderation polling failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<TelegramCallback>> GetCallbacksAsync(
        TelegramOptions options,
        long? offset,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["timeout"] = "25",
            ["allowed_updates"] = JsonSerializer.Serialize(new[] { "callback_query" }, JsonOptions),
            ["offset"] = offset?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        });

        using var response = await client.PostAsync(BuildApiUrl(options, "getUpdates"), content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Telegram getUpdates failed: {Response}", raw);
            return [];
        }

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            logger.LogWarning("Telegram getUpdates returned non-ok response: {Response}", raw);
            return [];
        }

        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var callbacks = new List<TelegramCallback>();
        foreach (var update in result.EnumerateArray())
        {
            if (!update.TryGetProperty("update_id", out var updateIdElement) ||
                !updateIdElement.TryGetInt64(out var updateId))
            {
                continue;
            }

            if (!update.TryGetProperty("callback_query", out var callbackQuery))
            {
                callbacks.Add(new TelegramCallback(updateId, null, null, null, null, null));
                continue;
            }

            var id = TryGetString(callbackQuery, "id");
            var data = TryGetString(callbackQuery, "data");
            long? fromId = null;
            long? messageChatId = null;
            int? messageId = null;

            if (callbackQuery.TryGetProperty("from", out var from) &&
                from.TryGetProperty("id", out var fromIdElement) &&
                fromIdElement.TryGetInt64(out var parsedFromId))
            {
                fromId = parsedFromId;
            }

            if (callbackQuery.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("message_id", out var messageIdElement) &&
                    messageIdElement.TryGetInt32(out var parsedMessageId))
                {
                    messageId = parsedMessageId;
                }

                if (message.TryGetProperty("chat", out var chat) &&
                    chat.TryGetProperty("id", out var chatIdElement) &&
                    chatIdElement.TryGetInt64(out var parsedChatId))
                {
                    messageChatId = parsedChatId;
                }
            }

            callbacks.Add(new TelegramCallback(updateId, id, fromId, messageChatId, messageId, data));
        }

        return callbacks;
    }

    private async Task HandleCallbackAsync(
        TelegramOptions options,
        TelegramCallback callback,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callback.CallbackId))
        {
            return;
        }

        if (!IsAuthorized(options, callback))
        {
            await AnswerCallbackAsync(options, callback.CallbackId, "Нет доступа к модерации.", true, cancellationToken);
            return;
        }

        var command = ParseCommand(callback.Data);
        if (command is null)
        {
            await AnswerCallbackAsync(options, callback.CallbackId, "Неизвестная команда.", true, cancellationToken);
            return;
        }

        var moderationMessageIds = await GetActiveModerationMessageIdsAsync(command.Value.PostId, cancellationToken);
        await AnswerCallbackAsync(options, callback.CallbackId, "Принял.", false, cancellationToken);

        try
        {
            var resultText = command.Value.Action switch
            {
                "publish" => await PublishAsync(command.Value.PostId, cancellationToken),
                "reject" => await RejectAsync(command.Value.PostId, cancellationToken),
                "rewrite" => await RewriteAsync(command.Value.PostId, cancellationToken),
                "image" => await RegenerateImageAsync(command.Value.PostId, cancellationToken),
                _ => "Неизвестная команда."
            };

            if (moderationMessageIds.Count > 0)
            {
                await ResolveModerationMessagesAsync(options, moderationMessageIds, resultText, cancellationToken);
            }
            else
            {
                await EditCallbackMessageAsync(options, callback, resultText, cancellationToken);
            }

            await realtimeNotifier.StateChangedAsync($"telegram-{command.Value.Action}", null, command.Value.PostId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram moderation command {Command} failed for post {PostId}.", command.Value.Action, command.Value.PostId);
            var errorText = $"Ошибка: {ex.Message}";
            if (moderationMessageIds.Count > 0)
            {
                await ResolveModerationMessagesAsync(options, moderationMessageIds, errorText, cancellationToken);
            }
            else
            {
                await EditCallbackMessageAsync(options, callback, errorText, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> GetActiveModerationMessageIdsAsync(Guid postId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ModerationMessages
            .Where(message => message.PostId == postId && message.IsActive)
            .Select(message => message.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<string> PublishAsync(Guid postId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imageGenerator = scope.ServiceProvider.GetRequiredService<IImageGenerator>();
        var publisher = scope.ServiceProvider.GetRequiredService<ITelegramPublisher>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

        var post = await db.Posts
            .Include(item => item.Channel)
            .Include(item => item.SourceCandidate)
            .FirstOrDefaultAsync(item => item.Id == postId, cancellationToken);

        if (post?.Channel is null)
        {
            return "Пост не найден.";
        }

        if (post.Status == PostStatus.Published)
        {
            return $"Уже опубликовано: {post.TelegramPostUrl}";
        }

        if (ShouldGenerateImageOnPublish(post))
        {
            var image = await imageGenerator.GenerateForPostAsync(post.Channel, post, cancellationToken);
            if (!string.IsNullOrWhiteSpace(image.ImageUrl))
            {
                post.ImagePath = image.ImageUrl;
            }

            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = post.ChannelId,
                PostId = post.Id,
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

        var result = await publisher.PublishAsync(post.Channel, post, cancellationToken);
        if (!result.Success)
        {
            post.Status = PostStatus.PublishFailed;
            post.RejectionReason = result.Error;
            await db.SaveChangesAsync(cancellationToken);
            return $"Не опубликовано. Ошибка Telegram: {result.Error}";
        }

        post.Status = PostStatus.Published;
        post.PublishedAtUtc = clock.UtcNow;
        post.TelegramMessageId = result.TelegramMessageId;
        post.TelegramPostUrl = result.PublicUrl;
        await db.SaveChangesAsync(cancellationToken);

        return $"Опубликовано: {result.PublicUrl}";
    }

    private async Task<string> RejectAsync(Guid postId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var post = await db.Posts.FirstOrDefaultAsync(item => item.Id == postId, cancellationToken);
        if (post is null)
        {
            return "Пост не найден.";
        }

        if (post.Status == PostStatus.Published)
        {
            return $"Пост уже опубликован: {post.TelegramPostUrl}";
        }

        post.Status = PostStatus.Rejected;
        post.RejectionReason = "Отклонено через Telegram-модерацию.";
        await db.SaveChangesAsync(cancellationToken);

        return "Не публикуем. Пост отклонён.";
    }

    private async Task<string> RewriteAsync(Guid postId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IPostTextGenerator>();
        var notifier = scope.ServiceProvider.GetRequiredService<IModerationNotifier>();

        var post = await db.Posts
            .Include(item => item.Channel)
            .Include(item => item.PublicationType)
            .Include(item => item.SourceCandidate)
            .FirstOrDefaultAsync(item => item.Id == postId, cancellationToken);

        if (post?.Channel is null || post.PublicationType is null)
        {
            return "Пост не найден.";
        }

        if (post.Status == PostStatus.Published)
        {
            return $"Пост уже опубликован: {post.TelegramPostUrl}";
        }

        var candidate = post.SourceCandidate ?? new SourceCandidate
        {
            ChannelId = post.ChannelId,
            Title = post.SourceTitle,
            Url = post.SourceUrl,
            CanonicalUrl = post.SourceUrl,
            Summary = post.OriginalSummary,
            RawText = post.OriginalSummary,
            ImageUrl = post.ImagePath,
            MediaUrlsJson = post.MediaUrlsJson,
            VideoUrl = post.VideoUrl,
            NormalizedHash = post.Id.ToString("N")
        };

        var generated = await generator.GenerateAsync(post.Channel, post.PublicationType, candidate, cancellationToken);
        post.GeneratedText = generated.Text;
        post.FinalText = generated.Text;
        post.Header = generated.Header;
        post.Footer = generated.Footer;
        post.Prompt = generated.Prompt;
        post.Model = generated.Model;
        post.CostAmount = null;
        post.CostCurrency = generated.CostCurrency;
        post.Status = PostStatus.WaitingModeration;

        var version = await db.PostVersions
            .Where(item => item.PostId == post.Id)
            .Select(item => item.VersionNumber)
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken);

        db.PostVersions.Add(new PostVersion
        {
            PostId = post.Id,
            VersionNumber = version + 1,
            Text = generated.Text,
            Prompt = generated.Prompt,
            Model = generated.Model,
            Reason = "telegram-rewrite"
        });

        db.AiUsageRecords.Add(new AiUsageRecord
        {
            ChannelId = post.ChannelId,
            PostId = post.Id,
            Provider = generated.Provider,
            Model = generated.Model,
            TaskType = AiTaskType.Rewrite,
            PromptTokens = generated.PromptTokens,
            CompletionTokens = generated.CompletionTokens,
            TotalTokens = generated.TotalTokens,
            CostAmount = null,
            CostCurrency = generated.CostCurrency,
            ProviderCostAmount = generated.CostAmount,
            ProviderCostCurrency = generated.CostCurrency,
            RequestMetadataJson = generated.UsageMetadataJson
        });

        await db.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(post.Channel, post, cancellationToken);

        return "Переписал. Новая версия отправлена ниже.";
    }

    private async Task<string> RegenerateImageAsync(Guid postId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var imageGenerator = scope.ServiceProvider.GetRequiredService<IImageGenerator>();
        var notifier = scope.ServiceProvider.GetRequiredService<IModerationNotifier>();

        var post = await db.Posts
            .Include(item => item.Channel)
            .Include(item => item.SourceCandidate)
            .FirstOrDefaultAsync(item => item.Id == postId, cancellationToken);

        if (post?.Channel is null)
        {
            return "Пост не найден.";
        }

        if (post.Status == PostStatus.Published)
        {
            return $"Пост уже опубликован: {post.TelegramPostUrl}";
        }

        var image = await imageGenerator.GenerateForPostAsync(post.Channel, post, cancellationToken);
        if (string.IsNullOrWhiteSpace(image.ImageUrl))
        {
            return "Провайдер не вернул URL картинки.";
        }

        post.ImagePath = image.ImageUrl;
        post.Status = PostStatus.WaitingModeration;

        db.AiUsageRecords.Add(new AiUsageRecord
        {
            ChannelId = post.ChannelId,
            PostId = post.Id,
            Provider = image.Provider,
            Model = image.Model,
            TaskType = AiTaskType.ImageGeneration,
            CostAmount = AiCostDefaults.ImageGenerationRub,
            CostCurrency = AiCostDefaults.Currency,
            ProviderCostAmount = image.CostAmount,
            ProviderCostCurrency = image.CostCurrency,
            RequestMetadataJson = image.UsageMetadataJson
        });

        await db.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(post.Channel, post, cancellationToken);

        return "Картинку переделал. Версия с новой картинкой отправлена ниже.";
    }

    private async Task AnswerCallbackAsync(
        TelegramOptions options,
        string callbackId,
        string text,
        bool showAlert,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["callback_query_id"] = callbackId,
            ["text"] = text,
            ["show_alert"] = showAlert ? "true" : "false"
        });

        using var response = await client.PostAsync(BuildApiUrl(options, "answerCallbackQuery"), content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Telegram answerCallbackQuery failed: {Response}", raw);
        }
    }

    private async Task ResolveModerationMessagesAsync(
        TelegramOptions options,
        IReadOnlyList<Guid> moderationMessageIds,
        string text,
        CancellationToken cancellationToken)
    {
        if (moderationMessageIds.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messages = await db.ModerationMessages
            .Where(message => moderationMessageIds.Contains(message.Id) && message.IsActive)
            .ToListAsync(cancellationToken);

        using var client = httpClientFactory.CreateClient();
        foreach (var message in messages)
        {
            if (message.ImageMessageId is not null)
            {
                await DeleteMessageAsync(client, options, message.ChatId, message.ImageMessageId.Value, cancellationToken);
            }

            await EditMessageAsync(client, options, message.ChatId, message.TextMessageId, text, cancellationToken);

            message.IsActive = false;
            message.Resolution = text.Length <= 120 ? text : text[..120];
            message.ResolvedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteMessageAsync(
        HttpClient client,
        TelegramOptions options,
        string chatId,
        int messageId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId.ToString(CultureInfo.InvariantCulture)
        });

        using var response = await client.PostAsync(BuildApiUrl(options, "deleteMessage"), content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Telegram deleteMessage failed for chat {ChatId} message {MessageId}: {Response}", chatId, messageId, raw);
        }
    }

    private async Task EditMessageAsync(
        HttpClient client,
        TelegramOptions options,
        string chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId.ToString(CultureInfo.InvariantCulture),
            ["text"] = text,
            ["disable_web_page_preview"] = "false",
            ["reply_markup"] = JsonSerializer.Serialize(new { inline_keyboard = Array.Empty<object[]>() }, JsonOptions)
        });

        using var response = await client.PostAsync(BuildApiUrl(options, "editMessageText"), content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Telegram editMessageText failed for chat {ChatId} message {MessageId}: {Response}", chatId, messageId, raw);
        }
    }

    private async Task EditCallbackMessageAsync(
        TelegramOptions options,
        TelegramCallback callback,
        string text,
        CancellationToken cancellationToken)
    {
        var chatId = callback.MessageChatId?.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(chatId) || callback.MessageId is null)
        {
            return;
        }

        using var client = httpClientFactory.CreateClient();
        await EditMessageAsync(client, options, chatId, callback.MessageId.Value, text, cancellationToken);
    }

    private static bool IsAuthorized(TelegramOptions options, TelegramCallback callback)
    {
        var moderatorIds = options.GetModeratorChatIds();
        var fromId = callback.FromId?.ToString(CultureInfo.InvariantCulture);
        var chatId = callback.MessageChatId?.ToString(CultureInfo.InvariantCulture);

        return moderatorIds.Any(id => id == fromId || id == chatId);
    }

    private static ModerationCommand? ParseCommand(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts[0].Equals("moderate", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[2], out var postId))
        {
            return new ModerationCommand(parts[1].ToLowerInvariant(), postId);
        }

        if (parts.Length == 2 && Guid.TryParse(parts[1], out postId))
        {
            return new ModerationCommand(parts[0].ToLowerInvariant(), postId);
        }

        return null;
    }

    private static bool ShouldGenerateImageOnPublish(Post post)
    {
        if (post.PublicationKind == PublicationKind.Trailer && !string.IsNullOrWhiteSpace(post.VideoUrl))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(post.ImagePath))
        {
            return false;
        }

        return post.PublicationKind is PublicationKind.News or PublicationKind.BreakingNews or PublicationKind.Digest or PublicationKind.Deal or PublicationKind.Trailer or PublicationKind.Meme;
    }

    private static string BuildApiUrl(TelegramOptions options, string method)
    {
        return $"https://api.telegram.org/bot{options.BotToken}/{method}";
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private readonly record struct TelegramCallback(
        long UpdateId,
        string? CallbackId,
        long? FromId,
        long? MessageChatId,
        int? MessageId,
        string? Data);

    private readonly record struct ModerationCommand(string Action, Guid PostId);
}
