using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>
/// Public Telegram channels via the web preview (https://t.me/s/&lt;channel&gt;). No login, no bot rights needed;
/// works for public channels only. <see cref="Source.Url"/> accepts "@name", "name" or any t.me link.
/// Requests go through the configured Telegram proxy (same as the bot), since t.me is often unreachable directly.
/// </summary>
public sealed partial class TelegramCollector : ISourceCollector
{
    private readonly HttpClient _httpClient;
    private readonly WspanelClient _wspanel;

    public TelegramCollector(TelegramHttpClientFactory httpClientFactory, WspanelClient wspanel)
    {
        _httpClient = httpClientFactory.CreateClient();
        _wspanel = wspanel;
    }

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.Telegram];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        var username = ExtractUsername(source.Url);
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        var previewUrl = $"https://t.me/s/{Uri.EscapeDataString(username)}";
        var html = _wspanel.UseForTelegram
            ? await FetchViaWspanelAsync(previewUrl, cancellationToken)
            : await FetchDirectAsync(previewUrl, cancellationToken);
        if (!html.Contains("tgme_widget_message", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Канал @{username} недоступен через веб-превью (приватный или не существует).");
        }

        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();

        foreach (Match block in MessageBlockRegex().Matches(html))
        {
            var postId = block.Groups["post"].Value; // e.g. "channel/123"
            var body = block.Groups["body"].Value;

            var textHtml = MessageTextRegex().Match(body).Groups["text"].Value;
            var text = StripHtml(textHtml.Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var haystack = text;
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, profile, haystack))
            {
                continue;
            }

            var date = DateTimeOffset.TryParse(TimeRegex().Match(body).Groups["date"].Value, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            var photo = PhotoRegex().Match(body).Groups["url"].Value;
            var video = VideoRegex().Match(body).Groups["url"].Value;
            var preview = LinkPreviewRegex().Match(body).Groups["url"].Value;
            var author = AuthorRegex().Match(body).Groups["name"].Value;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var title = lines.Length > 0 ? lines[0] : text;
            if (title.Length > 160)
            {
                title = $"{title[..157]}...";
            }

            var messageUrl = $"https://t.me/{postId}";
            result.Add(new CollectedCandidate(
                title,
                messageUrl,
                BuildSummary(title, text.Length > 800 ? $"{text[..800]}..." : text),
                text,
                string.IsNullOrWhiteSpace(photo) ? null : WebUtility.HtmlDecode(photo),
                null,
                null,
                date,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    channel = username,
                    post = postId,
                    linkPreview = string.IsNullOrWhiteSpace(preview) ? null : WebUtility.HtmlDecode(preview),
                    transport = "tme-preview"
                }),
                VideoUrl: string.IsNullOrWhiteSpace(video) ? null : NormalizeVideoUrl(video),
                ExternalId: postId,
                Author: string.IsNullOrWhiteSpace(author) ? username : WebUtility.HtmlDecode(author).Trim()));
        }

        return result;
    }

    private async Task<string> FetchDirectAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request);
        request.Headers.AcceptLanguage.ParseAdd("ru,en;q=0.8");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string> FetchViaWspanelAsync(string url, CancellationToken cancellationToken)
    {
        // The preview is server-rendered, so no extra wait is needed.
        var pages = await _wspanel.ReadAsync([url], includeHtml: true, null, cancellationToken, waitMs: 500);
        var page = pages.FirstOrDefault();
        if (page is null || !string.IsNullOrWhiteSpace(page.Error))
        {
            throw new InvalidOperationException($"wspanel не открыл {url}: {page?.Error ?? "пустой ответ"}");
        }

        return page.Html ?? string.Empty;
    }

    private static string? ExtractUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, "t\\.me/(?:s/)?(?<name>[A-Za-z0-9_]{4,})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["name"].Value;
        }

        trimmed = trimmed.TrimStart('@');
        return Regex.IsMatch(trimmed, "^[A-Za-z0-9_]{4,}$") ? trimmed : null;
    }

    // One message block: from the data-post marker to the next message (or end of the section).
    [GeneratedRegex("class=\"tgme_widget_message[^\"]*\"[^>]*data-post=\"(?<post>[A-Za-z0-9_]+/\\d+)\"(?<body>.*?)(?=class=\"tgme_widget_message[^\"]*\"[^>]*data-post=|</section>)", RegexOptions.Singleline)]
    private static partial Regex MessageBlockRegex();

    [GeneratedRegex("class=\"tgme_widget_message_text[^\"]*\"[^>]*>(?<text>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex MessageTextRegex();

    [GeneratedRegex("<time[^>]+datetime=\"(?<date>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex TimeRegex();

    [GeneratedRegex("tgme_widget_message_photo_wrap[^>]*style=\"[^\"]*background-image:url\\('(?<url>[^']+)'\\)", RegexOptions.Singleline)]
    private static partial Regex PhotoRegex();

    [GeneratedRegex("<video[^>]+src=\"(?<url>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex VideoRegex();

    [GeneratedRegex("class=\"tgme_widget_message_link_preview\"[^>]*href=\"(?<url>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex LinkPreviewRegex();

    [GeneratedRegex("class=\"tgme_widget_message_owner_name\"[^>]*>\\s*<span[^>]*>(?<name>[^<]+)</span>", RegexOptions.Singleline)]
    private static partial Regex AuthorRegex();
}
