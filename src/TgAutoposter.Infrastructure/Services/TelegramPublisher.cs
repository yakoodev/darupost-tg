using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed class TelegramPublisher(TelegramHttpClientFactory httpClientFactory, IOptions<TelegramOptions> optionsAccessor) : ITelegramPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<PublishResult> PublishAsync(Channel channel, Post post, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            return new PublishResult(false, null, null, "Telegram: BotToken is not configured.");
        }

        var chatId = channel.TelegramChatId ?? channel.TelegramUsername;
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return new PublishResult(false, null, null, "Telegram: channel chat id or username is not configured.");
        }

        var text = BuildTelegramText(post);
        using var client = httpClientFactory.CreateClient();

        var photoUrls = ResolvePhotoUrls(post);
        var videoUrl = NormalizeTelegramVideoUrl(post.VideoUrl);

        if (!string.IsNullOrWhiteSpace(videoUrl))
        {
            return await PublishVideoAsync(options, channel, chatId, post, videoUrl, text, cancellationToken);
        }

        if (photoUrls.Count > 1)
        {
            return await PublishMediaGroupAsync(options, channel, chatId, photoUrls, text, cancellationToken);
        }

        if (photoUrls.Count == 1)
        {
            using var photoContent = BuildPhotoContent(chatId, photoUrls[0], text);
            return await SendAsync(client, options, channel, "sendPhoto", photoContent, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new PublishResult(false, null, null, "Telegram: post has no text or media to publish.");
        }

        using var content = BuildTextContent(chatId, text);
        return await SendAsync(client, options, channel, "sendMessage", content, cancellationToken);
    }

    private async Task<PublishResult> PublishVideoAsync(
        TelegramOptions options,
        Channel channel,
        string chatId,
        Post post,
        string videoUrl,
        string caption,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();

        if (LooksLikeDirectVideo(videoUrl))
        {
            using var content = BuildRemoteVideoContent(chatId, videoUrl, caption);
            return await SendAsync(client, options, channel, "sendVideo", content, cancellationToken);
        }

        var download = await TryDownloadVideoAsync(videoUrl, post.Id, cancellationToken);
        if (download.FilePath is null)
        {
            return new PublishResult(false, null, null, download.Error ?? "Telegram: video could not be downloaded for upload.");
        }

        try
        {
            using var content = BuildUploadedVideoContent(chatId, download.FilePath, caption);
            return await SendAsync(client, options, channel, "sendVideo", content, cancellationToken);
        }
        finally
        {
            TryDeleteFile(download.FilePath);
        }
    }

    private static async Task<PublishResult> SendAsync(
        HttpClient client,
        TelegramOptions options,
        Channel channel,
        string method,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(BuildApiUrl(options.BotToken!, method), content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PublishResult(false, null, null, raw);
        }

        using var document = JsonDocument.Parse(raw);
        var messageId = ExtractMessageId(document.RootElement);
        return new PublishResult(true, messageId, BuildPublicUrl(channel.TelegramUsername, messageId), null);
    }

    private async Task<PublishResult> PublishMediaGroupAsync(
        TelegramOptions options,
        Channel channel,
        string chatId,
        IReadOnlyList<string> photoUrls,
        string caption,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        using var content = BuildMediaGroupContent(chatId, photoUrls, caption);
        return await SendAsync(client, options, channel, "sendMediaGroup", content, cancellationToken);
    }

    private static FormUrlEncodedContent BuildTextContent(string chatId, string text)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["parse_mode"] = "Markdown",
            ["disable_web_page_preview"] = "false"
        });
    }

    private static FormUrlEncodedContent BuildPhotoContent(string chatId, string photoUrl, string caption)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["photo"] = photoUrl,
            ["caption"] = ClampCaption(caption),
            ["parse_mode"] = "Markdown"
        });
    }

    private static FormUrlEncodedContent BuildRemoteVideoContent(string chatId, string videoUrl, string caption)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["video"] = videoUrl,
            ["caption"] = ClampCaption(caption),
            ["parse_mode"] = "Markdown",
            ["supports_streaming"] = "true"
        });
    }

    private static MultipartFormDataContent BuildUploadedVideoContent(string chatId, string filePath, string caption)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(chatId), "chat_id" },
            { new StringContent(ClampCaption(caption)), "caption" },
            { new StringContent("Markdown"), "parse_mode" },
            { new StringContent("true"), "supports_streaming" }
        };

        var stream = File.OpenRead(filePath);
        content.Add(new StreamContent(stream), "video", Path.GetFileName(filePath));
        return content;
    }

    private static FormUrlEncodedContent BuildMediaGroupContent(string chatId, IReadOnlyList<string> photoUrls, string caption)
    {
        var media = photoUrls
            .Take(10)
            .Select((url, index) => new InputMediaPhoto(
                "photo",
                url,
                index == 0 && !string.IsNullOrWhiteSpace(caption) ? ClampCaption(caption) : null,
                index == 0 && !string.IsNullOrWhiteSpace(caption) ? "Markdown" : null))
            .ToArray();

        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["media"] = JsonSerializer.Serialize(media, JsonOptions)
        });
    }

    private static string BuildTelegramText(Post post)
    {
        if (post.PublicationKind == PublicationKind.Meme)
        {
            return string.IsNullOrWhiteSpace(post.Footer) ? string.Empty : post.Footer.Trim();
        }

        return string.Join("\n\n", new[]
        {
            post.Header,
            post.FinalText ?? post.GeneratedText,
            post.Footer
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildApiUrl(string token, string method)
    {
        return $"https://api.telegram.org/bot{token}/{method}";
    }

    private static string ClampCaption(string caption)
    {
        const int telegramCaptionLimit = 1024;
        if (caption.Length <= telegramCaptionLimit)
        {
            return caption;
        }

        return $"{caption[..1010].TrimEnd()}...";
    }

    private static string? ExtractMessageId(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result))
        {
            return null;
        }

        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("message_id", out var messageId) &&
            messageId.TryGetInt64(out var value))
        {
            return value.ToString();
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                if (item.TryGetProperty("message_id", out messageId) &&
                    messageId.TryGetInt64(out value))
                {
                    return value.ToString();
                }
            }
        }

        return null;
    }

    private static string? BuildPublicUrl(string? username, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        return $"https://t.me/{username.TrimStart('@')}/{messageId}";
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

    private static string? NormalizeTelegramVideoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static IReadOnlyList<string> ResolvePhotoUrls(Post post)
    {
        var result = new List<string>();
        var mainPhoto = NormalizeTelegramPhotoUrl(post.ImagePath);
        if (!string.IsNullOrWhiteSpace(mainPhoto))
        {
            result.Add(mainPhoto);
        }
        else if (!string.IsNullOrWhiteSpace(post.MediaUrlsJson))
        {
            try
            {
                var urls = JsonSerializer.Deserialize<string[]>(post.MediaUrlsJson, JsonOptions) ?? [];
                result.AddRange(urls.Select(NormalizeTelegramPhotoUrl).Where(url => !string.IsNullOrWhiteSpace(url))!);
            }
            catch (JsonException)
            {
                // Invalid stored media JSON should not block single-image or text posts.
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static bool LooksLikeDirectVideo(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<VideoDownloadResult> TryDownloadVideoAsync(
        string videoUrl,
        Guid postId,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "tg-autoposter-videos");
        Directory.CreateDirectory(tempDir);

        var outputTemplate = Path.Combine(tempDir, $"{postId:N}-%(id)s.%(ext)s");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add("--no-playlist");
        process.StartInfo.ArgumentList.Add("--max-filesize");
        process.StartInfo.ArgumentList.Add("45M");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("bv*[ext=mp4][height<=720]+ba[ext=m4a]/b[ext=mp4][height<=720]/best[height<=720]");
        process.StartInfo.ArgumentList.Add("--merge-output-format");
        process.StartInfo.ArgumentList.Add("mp4");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(outputTemplate);
        process.StartInfo.ArgumentList.Add(videoUrl);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new VideoDownloadResult(null, $"yt-dlp is not available: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            return new VideoDownloadResult(null, $"yt-dlp failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        var file = Directory
            .EnumerateFiles(tempDir, $"{postId:N}-*")
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(file)
            ? new VideoDownloadResult(null, $"yt-dlp completed but no output file was found. Output: {stdout.Trim()}")
            : new VideoDownloadResult(file, null);
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record InputMediaPhoto(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("media")] string Media,
        [property: JsonPropertyName("caption")] string? Caption,
        [property: JsonPropertyName("parse_mode")] string? ParseMode);

    private sealed record VideoDownloadResult(string? FilePath, string? Error);
}
