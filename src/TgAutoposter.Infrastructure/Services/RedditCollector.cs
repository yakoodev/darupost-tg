using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed class RedditCollector(HttpClient httpClient, IOptions<PolzaOptions> optionsAccessor) : IContentCollector
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] LowNewsValueMarkers =
    [
        "review",
        "preview",
        "hands-on",
        "roundup",
        "guide",
        "walkthrough",
        "tips",
        "best ",
        "what are you playing",
        "suggestions thread",
        "lives up to the hype",
        "if you haven't played",
        "wordle",
        "connections",
        "quiz",
        "hardware review"
    ];

    private static readonly string[] NewsSignalMarkers =
    [
        "announc",
        "reveal",
        "release",
        "launch",
        "trailer",
        "patch",
        "update",
        "delay",
        "confirm",
        "lawsuit",
        "sues",
        "leak",
        "rumor",
        "rumour",
        "insider",
        "acquisition",
        "beta",
        "dlc",
        "expansion",
        "showcase",
        "coming to",
        "game pass",
        "free",
        "sale",
        "анонс",
        "анонсиров",
        "раскрыл",
        "трейлер",
        "дата выхода",
        "релиз",
        "выйдет",
        "вышла",
        "вышел",
        "патч",
        "обновлен",
        "перенес",
        "подтверд",
        "суд",
        "иск",
        "слух",
        "утеч",
        "бета",
        "дополнени",
        "распродаж"
    ];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, CancellationToken cancellationToken)
    {
        if (source.Kind == SourceKind.AiWebSearch)
        {
            return await CollectAiWebSearchAsync(source, cancellationToken);
        }

        if (source.Kind is SourceKind.Rss or SourceKind.Web)
        {
            return string.IsNullOrWhiteSpace(source.Url)
                ? []
                : await CollectFeedAsync(source, cancellationToken);
        }

        if (source.Kind != SourceKind.Reddit || string.IsNullOrWhiteSpace(source.Subreddit))
        {
            return [];
        }

        try
        {
            return await CollectJsonAsync(source, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return await CollectRssAsync(source, cancellationToken);
        }
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> CollectAiWebSearchAsync(Source source, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return [];
        }

        var searchPrompt = string.IsNullOrWhiteSpace(source.Url)
            ? "fresh video game news today PC console gaming releases trailers updates official announcements"
            : source.Url.Trim();

        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var userPrompt = SourceOnlyAllowsKind(source, PublicationKind.Trailer)
            ? $$"""
        Сегодня {{today}}. Найди 5 свежих официальных трейлеров или видео-анонсов по видеоиграм за последние 24-36 часов.
        Нужны только ролики, которые можно показать русскоязычной аудитории Telegram-канала "Только игры": официальный трейлер, gameplay trailer, launch trailer, reveal trailer, showcase, дата выхода с роликом.
        Не бери обзоры, реакции, фанатские трейлеры, подборки, гайды, старые ролики, неигровую повестку и материалы без проверяемого видео.
        Не бери мелкий инфоповод, если это просто очередной ролик без даты выхода, релиза, крупного анонса, известной игры/студии или заметного обновления.
        В каждом пункте videoUrl обязателен: официальный YouTube watch/embed URL, страница официального трейлера или прямой URL видео. Если videoUrl не найден, не включай пункт.

        Верни только JSON-объект без markdown:
        {
          "items": [
            {
            "title": "краткий английский заголовок",
            "url": "https://страница-источника-или-трейлера",
            "summary": "2-3 предложения: что за игра, что показали, платформа/дата если известны",
            "publishedAt": "ISO-8601 если известна дата, иначе null",
            "imageUrl": null,
            "videoUrl": "https://www.youtube.com/watch?v=..."
            }
          ]
        }
        """
            : $$"""
        Сегодня {{today}}. Найди 5 свежих новостей строго про видеоигры за последние 24-36 часов.
        Нужны инфоповоды для русскоязычного Telegram-канала "Только игры": релизы, патчи, трейлеры, игровые студии, платформы, раздачи, события индустрии.
        Не бери обзоры, превью, рекомендации "поиграть", гайды, Wordle, подборки, старые новости и неигровую повестку.
        Каждый пункт должен быть событием: анонс, релиз, трейлер, патч, перенос, суд, покупка студии, дата выхода, DLC, бета или крупное платформенное изменение.
        Если пункт про трейлер, в videoUrl верни прямую ссылку на официальный ролик, YouTube watch/embed URL или другой проверяемый URL видео. Если видео найти нельзя, такой пункт не бери как трейлер.

        Верни только JSON-объект без markdown:
        {
          "items": [
            {
            "title": "краткий английский заголовок",
            "url": "https://...",
            "summary": "2-3 предложения с фактами и контекстом",
            "publishedAt": "ISO-8601 если известна дата, иначе null",
            "imageUrl": null,
            "videoUrl": null
            }
          ]
        }
        """;

        var payload = new
        {
            model = options.DefaultModel,
            messages = new[]
            {
                new { role = "system", content = "Ты аккуратный новостной ресерчер по видеоиграм. Возвращай только проверяемые свежие инфоповоды со ссылками на источники." },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.2,
            max_tokens = 1400,
            plugins = new[]
            {
                new
                {
                    id = "web",
                    engine = "exa",
                    max_results = 5,
                    search_prompt = searchPrompt
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/{options.ChatCompletionPath.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Polza web search failed: {(int)response.StatusCode} {raw}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var usage = PolzaResponseParser.ExtractUsage(root, options.DefaultModel);
        var text = PolzaResponseParser.ExtractText(root) ?? string.Empty;
        var citations = PolzaResponseParser.ExtractUrlCitations(root);
        var items = ParseAiWebSearchItems(source, text, raw, usage, citations);
        return await EnrichVideoUrlsAsync(source, items, cancellationToken);
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> CollectFeedAsync(Source source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        ApplyHeaders(request);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        var entries = document
            .Descendants()
            .Where(element => element.Name.LocalName is "item" or "entry")
            .Take(30);

        foreach (var entry in entries)
        {
            var title = GetChildValue(entry, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var html = GetChildValue(entry, "description") ??
                       GetChildValue(entry, "summary") ??
                       GetChildValue(entry, "encoded") ??
                       GetChildValue(entry, "content");

            var text = StripHtml(html);
            var haystack = $"{title}\n{text}";
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, haystack))
            {
                continue;
            }

            var link = GetFeedLink(entry);
            var publishedAt = ParseFeedDate(entry);
            var imageUrl = ExtractFeedImageUrl(entry, html);
            var videoUrl = ExtractFeedVideoUrl(entry, html);
            if (SourceAllowsMeme(source) && !LooksLikeImage(imageUrl))
            {
                continue;
            }

            result.Add(new CollectedCandidate(
                title,
                link,
                BuildSummary(title, text),
                text,
                imageUrl,
                null,
                null,
                publishedAt,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    sourceUrl = source.Url,
                    author = GetChildValue(entry, "creator") ?? GetChildValue(entry, "author"),
                    videoUrl,
                    transport = "feed"
                }),
                VideoUrl: videoUrl));
        }

        return await EnrichVideoUrlsAsync(source, result, cancellationToken);
    }

    private static IReadOnlyCollection<CollectedCandidate> ParseAiWebSearchItems(
        Source source,
        string text,
        string raw,
        PolzaUsageSnapshot usage,
        IReadOnlyCollection<PolzaUrlCitation> citations)
    {
        var json = ExtractJsonPayload(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return ParseAiWebSearchCitations(source, raw, usage, citations);
        }

        using var document = JsonDocument.Parse(json);
        var items = GetItemsArray(document.RootElement);
        if (items is null)
        {
            return ParseAiWebSearchCitations(source, raw, usage, citations);
        }

        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        foreach (var item in items.Value.EnumerateArray().Take(10))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = GetString(item, "title");
            var summary = GetString(item, "summary");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            var haystack = $"{title}\n{summary}";
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, haystack))
            {
                continue;
            }

            var foundAt = DateTimeOffset.TryParse(GetString(item, "publishedAt"), out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            result.Add(new CollectedCandidate(
                Sanitize(title),
                GetString(item, "url"),
                Sanitize(summary),
                Sanitize(summary),
                GetString(item, "imageUrl"),
                null,
                null,
                foundAt,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    transport = "polza-web",
                    videoUrl = GetString(item, "videoUrl"),
                    polza = usage.MetadataJson,
                    raw
                }),
                usage.CostRub,
                "RUB",
                usage.MetadataJson,
                NormalizeVideoUrl(GetString(item, "videoUrl"))));
        }

        return result.Count > 0 ? result : ParseAiWebSearchCitations(source, raw, usage, citations);
    }

    private static IReadOnlyCollection<CollectedCandidate> ParseAiWebSearchCitations(
        Source source,
        string raw,
        PolzaUsageSnapshot usage,
        IReadOnlyCollection<PolzaUrlCitation> citations)
    {
        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        foreach (var citation in citations.Take(10))
        {
            if (string.IsNullOrWhiteSpace(citation.Title) || string.IsNullOrWhiteSpace(citation.Url))
            {
                continue;
            }

            var content = NormalizeCitationContent(citation.Content);
            var haystack = $"{citation.Title}\n{content}";
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, haystack) || HasOldDateInUrl(citation.Url))
            {
                continue;
            }

            var summary = string.IsNullOrWhiteSpace(content)
                ? citation.Title.Trim()
                : BuildSummary(citation.Title.Trim(), content);

            result.Add(new CollectedCandidate(
                citation.Title.Trim(),
                citation.Url.Trim(),
                summary,
                content,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    transport = "polza-web-annotation",
                    polza = usage.MetadataJson,
                    raw
                }),
                usage.CostRub,
                "RUB",
                usage.MetadataJson));
        }

        return result;
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> EnrichVideoUrlsAsync(
        Source source,
        IReadOnlyCollection<CollectedCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || !SourceAllowsKind(source, PublicationKind.Trailer))
        {
            return candidates;
        }

        var result = new List<CollectedCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.VideoUrl) || !LooksLikeTrailerCandidate(candidate))
            {
                result.Add(candidate);
                continue;
            }

            var videoUrl = await TryExtractVideoFromPageAsync(candidate.Url, cancellationToken);
            result.Add(string.IsNullOrWhiteSpace(videoUrl) ? candidate : candidate with { VideoUrl = videoUrl });
        }

        if (!SourceOnlyAllowsKind(source, PublicationKind.Trailer))
        {
            return result;
        }

        var fresh = new List<CollectedCandidate>();
        foreach (var candidate in result.Where(candidate => !string.IsNullOrWhiteSpace(candidate.VideoUrl)))
        {
            var publishedAt = await TryExtractVideoPublishedAtAsync(candidate.VideoUrl, cancellationToken);
            if (publishedAt is not null && publishedAt.Value < DateTimeOffset.UtcNow.AddHours(-72))
            {
                continue;
            }

            fresh.Add(publishedAt is null ? candidate : candidate with { FoundAtUtc = publishedAt.Value });
        }

        return fresh;
    }

    private async Task<string?> TryExtractVideoFromPageAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (LooksLikeVideoUrl(url))
        {
            return NormalizeVideoUrl(url);
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyHeaders(request);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return ExtractVideoUrl(html);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<DateTimeOffset?> TryExtractVideoPublishedAtAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyHeaders(request);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = Regex.Match(html, "\"(?:uploadDate|datePublished)\"\\s*:\\s*\"(?<date>\\d{4}-\\d{2}-\\d{2})", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(html, "<meta[^>]+itemprop=[\"'](?:uploadDate|datePublished)[\"'][^>]+content=[\"'](?<date>\\d{4}-\\d{2}-\\d{2})", RegexOptions.IgnoreCase);
            }

            return match.Success && DateTimeOffset.TryParse(match.Groups["date"].Value, out var parsed)
                ? parsed
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string? ExtractJsonPayload(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = Regex.Replace(trimmed, "^```(?:json)?\\s*", string.Empty, RegexOptions.IgnoreCase);
            trimmed = Regex.Replace(trimmed, "\\s*```$", string.Empty);
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var objectStart = trimmed.IndexOf('{', StringComparison.Ordinal);
        var objectEnd = trimmed.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            return trimmed[objectStart..(objectEnd + 1)];
        }

        var arrayStart = trimmed.IndexOf('[', StringComparison.Ordinal);
        var arrayEnd = trimmed.LastIndexOf(']');
        return arrayStart >= 0 && arrayEnd > arrayStart ? trimmed[arrayStart..(arrayEnd + 1)] : trimmed;
    }

    private static JsonElement? GetItemsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            return items;
        }

        return null;
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> CollectJsonAsync(Source source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildJsonUrl(source));
        ApplyHeaders(request);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("children", out var children))
        {
            return [];
        }

        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();

        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("data", out var item))
            {
                continue;
            }

            var title = GetString(item, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var text = GetString(item, "selftext");
            var score = GetInt(item, "score");
            var comments = GetInt(item, "num_comments");
            var isNsfw = GetBool(item, "over_18");
            var haystack = $"{title}\n{text}";

            if (!PassesTextFilters(filters, haystack) ||
                !IsUsefulCandidateForSource(source, haystack) ||
                (isNsfw && !source.AllowNsfw) ||
                score < source.MinimumScore ||
                comments < source.MinimumComments)
            {
                continue;
            }

            var permalink = GetString(item, "permalink");
            var url = GetString(item, "url");
            var mediaUrls = ExtractRedditImageUrls(item);
            var imageUrl = mediaUrls.FirstOrDefault() ?? NormalizeImageUrl(LooksLikeImage(url) ? url : GetString(item, "thumbnail"));
            var videoUrl = ExtractRedditVideoUrl(item);
            if (SourceAllowsMeme(source) && mediaUrls.Count == 0 && !LooksLikeImage(imageUrl))
            {
                continue;
            }

            var redditUrl = string.IsNullOrWhiteSpace(permalink) ? url : $"https://www.reddit.com{permalink}";

            result.Add(new CollectedCandidate(
                title,
                redditUrl,
                BuildSummary(title, text),
                text,
                LooksLikeImage(imageUrl) ? imageUrl : null,
                score,
                comments,
                DateTimeOffset.FromUnixTimeSeconds(GetLong(item, "created_utc")),
                JsonSerializer.Serialize(new
                {
                    subreddit = source.Subreddit,
                    redditId = GetString(item, "id"),
                    author = GetString(item, "author"),
                    sourceUrl = url,
                    videoUrl,
                    mediaUrls,
                    transport = "json"
                }),
                VideoUrl: videoUrl,
                MediaUrls: mediaUrls.Count > 0 ? mediaUrls : null));
        }

        return result;
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> CollectRssAsync(Source source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRssUrl(source));
        ApplyHeaders(request);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();

        foreach (var entry in document.Descendants(Atom + "entry").Take(25))
        {
            var title = entry.Element(Atom + "title")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var link = entry.Elements(Atom + "link")
                .FirstOrDefault(element => element.Attribute("href") is not null)
                ?.Attribute("href")
                ?.Value;

            var html = entry.Element(Atom + "content")?.Value ?? entry.Element(Atom + "summary")?.Value;
            var text = StripHtml(html);
            var haystack = $"{title}\n{text}";

            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, haystack))
            {
                continue;
            }

            var updated = DateTimeOffset.TryParse(entry.Element(Atom + "updated")?.Value, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            var imageUrl = ExtractImageUrl(html);
            var videoUrl = ExtractVideoUrl(html);
            if (SourceAllowsMeme(source) && !LooksLikeImage(imageUrl))
            {
                continue;
            }

            result.Add(new CollectedCandidate(
                title,
                link,
                BuildSummary(title, text),
                text,
                imageUrl,
                null,
                null,
                updated,
                JsonSerializer.Serialize(new
                {
                    subreddit = source.Subreddit,
                    redditId = entry.Element(Atom + "id")?.Value,
                    author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value,
                    videoUrl,
                    transport = "rss"
                }),
                VideoUrl: videoUrl));
        }

        return await EnrichVideoUrlsAsync(source, result, cancellationToken);
    }

    private static string? GetFeedLink(XElement entry)
    {
        var atomLink = entry.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "link" && element.Attribute("href") is not null);

        if (atomLink is not null)
        {
            return atomLink.Attribute("href")?.Value.Trim();
        }

        return GetChildValue(entry, "link");
    }

    private static DateTimeOffset ParseFeedDate(XElement entry)
    {
        foreach (var name in new[] { "pubDate", "published", "updated", "date" })
        {
            var value = GetChildValue(entry, name);
            if (DateTimeOffset.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private static string? ExtractFeedImageUrl(XElement entry, string? html)
    {
        var enclosure = entry.Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "enclosure" &&
                element.Attribute("url") is not null &&
                (element.Attribute("type")?.Value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
                 LooksLikeImage(element.Attribute("url")?.Value)));

        if (enclosure is not null)
        {
            return enclosure.Attribute("url")?.Value.Trim();
        }

        var media = entry.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName is "content" or "thumbnail" &&
                element.Attribute("url") is not null &&
                (element.Attribute("medium")?.Value.Equals("image", StringComparison.OrdinalIgnoreCase) == true ||
                 element.Attribute("type")?.Value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
                 LooksLikeImage(element.Attribute("url")?.Value)));

        return NormalizeImageUrl(media?.Attribute("url")?.Value.Trim() ?? ExtractImageUrl(html));
    }

    private static string? ExtractFeedVideoUrl(XElement entry, string? html)
    {
        var enclosure = entry.Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "enclosure" &&
                element.Attribute("url") is not null &&
                (element.Attribute("type")?.Value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
                 LooksLikeVideoUrl(element.Attribute("url")?.Value)));

        if (enclosure is not null)
        {
            return NormalizeVideoUrl(enclosure.Attribute("url")?.Value);
        }

        var media = entry.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName is "content" or "player" &&
                element.Attribute("url") is not null &&
                (element.Attribute("medium")?.Value.Equals("video", StringComparison.OrdinalIgnoreCase) == true ||
                 element.Attribute("type")?.Value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
                 LooksLikeVideoUrl(element.Attribute("url")?.Value)));

        return NormalizeVideoUrl(media?.Attribute("url")?.Value ?? ExtractVideoUrl(html));
    }

    private static string? GetChildValue(XElement element, string localName)
    {
        var child = element.Elements().FirstOrDefault(item => item.Name.LocalName == localName);
        var value = child?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("tg-autoposter/0.1 by local-admin");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("application/atom+xml");
        request.Headers.Accept.ParseAdd("text/xml");
        request.Headers.Accept.ParseAdd("text/html");
    }

    private static string BuildJsonUrl(Source source)
    {
        var listing = source.RedditListing switch
        {
            RedditListingKind.New => "new",
            RedditListingKind.Rising => "rising",
            RedditListingKind.Top => "top",
            _ => "hot"
        };

        var period = string.IsNullOrWhiteSpace(source.RedditTopPeriod)
            ? "day"
            : Uri.EscapeDataString(source.RedditTopPeriod);

        var query = listing == "top" ? $"?limit=25&t={period}" : "?limit=25";
        return $"https://www.reddit.com/r/{Uri.EscapeDataString(source.Subreddit!)}/{listing}/.json{query}";
    }

    private static string BuildRssUrl(Source source)
    {
        return $"https://www.reddit.com/r/{Uri.EscapeDataString(source.Subreddit!)}/.rss";
    }

    private static RedditFilters CreateFilters(Source source)
    {
        return new RedditFilters(
            SplitCsv(source.WhitelistKeywordsCsv),
            SplitCsv(source.BlacklistKeywordsCsv));
    }

    private static bool PassesTextFilters(RedditFilters filters, string haystack)
    {
        if (filters.Blacklist.Count > 0 && filters.Blacklist.Any(keyword => Contains(haystack, keyword)))
        {
            return false;
        }

        return filters.Whitelist.Count == 0 || filters.Whitelist.Any(keyword => Contains(haystack, keyword));
    }

    private static bool IsUsefulNewsCandidate(string haystack)
    {
        return !ContainsAny(haystack, LowNewsValueMarkers) && ContainsAny(haystack, NewsSignalMarkers);
    }

    private static bool IsUsefulCandidateForSource(Source source, string haystack)
    {
        if (SourceAllowsMeme(source))
        {
            return true;
        }

        return IsUsefulNewsCandidate(haystack);
    }

    private static bool SourceAllowsMeme(Source source)
    {
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        return allowed.Any(kind => kind.Equals(PublicationKind.Meme.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeCitationContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var normalized = Regex.Replace(content.Replace("[...]", " "), "\\s+", " ").Trim();
        return normalized.Length <= 900 ? normalized : $"{normalized[..900]}...";
    }

    private static bool HasOldDateInUrl(string url)
    {
        var match = Regex.Match(url, "(?<year>20\\d{2})[/-](?<month>\\d{2})[/-](?<day>\\d{2})");
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["year"].Value, out var year) ||
            !int.TryParse(match.Groups["month"].Value, out var month) ||
            !int.TryParse(match.Groups["day"].Value, out var day))
        {
            return false;
        }

        try
        {
            var date = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
            return date < DateTimeOffset.UtcNow.AddHours(-72);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string BuildSummary(string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return title;
        }

        var normalized = Sanitize(text.ReplaceLineEndings(" "));
        var cleanTitle = Sanitize(title);
        return normalized.Length <= 500 ? $"{cleanTitle}\n{normalized}" : $"{cleanTitle}\n{normalized[..500]}...";
    }

    private static List<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static bool Contains(string text, string keyword)
    {
        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers)
    {
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractImageUrl(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = Regex.Match(html, "https?://[^\\s\"']+\\.(?:jpg|jpeg|png|webp)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeImageUrl(match.Value) : null;
    }

    private static string? ExtractVideoUrl(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var ogVideo = Regex.Match(html, "<meta[^>]+(?:property|name)=[\"'](?:og:video(?::url)?|twitter:player)[\"'][^>]+content=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase);
        if (ogVideo.Success)
        {
            var normalized = NormalizeVideoUrl(ogVideo.Groups["url"].Value);
            if (LooksLikeVideoUrl(normalized))
            {
                return normalized;
            }
        }

        var iframe = Regex.Match(html, "<iframe[^>]+src=[\"'](?<url>https?://[^\"']+)[\"']", RegexOptions.IgnoreCase);
        if (iframe.Success)
        {
            var normalized = NormalizeVideoUrl(iframe.Groups["url"].Value);
            if (LooksLikeVideoUrl(normalized))
            {
                return normalized;
            }
        }

        var embedUrl = Regex.Match(html, "\"(?:embedUrl|contentUrl)\"\\s*:\\s*\"(?<url>https?:\\\\/\\\\/[^\"\\\\]+(?:\\\\/[^\"\\\\]*)?)\"", RegexOptions.IgnoreCase);
        if (embedUrl.Success)
        {
            var normalized = NormalizeVideoUrl(embedUrl.Groups["url"].Value.Replace("\\/", "/", StringComparison.Ordinal));
            if (LooksLikeVideoUrl(normalized))
            {
                return normalized;
            }
        }

        var direct = Regex.Match(html, "https?://[^\\s\"']+\\.(?:mp4|mov|webm)(?:\\?[^\\s\"']*)?", RegexOptions.IgnoreCase);
        if (direct.Success)
        {
            return NormalizeVideoUrl(direct.Value);
        }

        var youtube = Regex.Match(html, "https?://(?:www\\.)?(?:youtube\\.com/watch\\?v=|youtu\\.be/)[^\\s\"'<]+", RegexOptions.IgnoreCase);
        return youtube.Success ? NormalizeVideoUrl(youtube.Value) : null;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withoutTags = Regex.Replace(html, "<.*?>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return Sanitize(Regex.Replace(decoded, "\\s+", " ").Trim());
    }

    /// <summary>
    /// Drops the Unicode replacement char (U+FFFD) and stray control characters that leak in from
    /// mis-decoded feeds or scraped pages, so corrupted glyphs never reach posts.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '�')
            {
                continue;
            }

            if (char.IsControl(ch) && ch is not ('\n' or '\t'))
            {
                continue;
            }

            builder.Append(ch);
        }

        return Regex.Replace(builder.ToString(), " {2,}", " ").Trim();
    }

    private static bool LooksLikeImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = System.Net.WebUtility.HtmlDecode(value).Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            normalized = uri.AbsolutePath;
        }

        return normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVideoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeVideoUrl(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        return host is "youtu.be" or "www.youtube.com" or "youtube.com" or "m.youtube.com"
            || host.EndsWith("v.redd.it", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decoded = System.Net.WebUtility.HtmlDecode(value).Trim();
        if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("preview.redd.it", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Host = "i.redd.it",
                Query = string.Empty
            };

            return builder.Uri.ToString();
        }

        return decoded;
    }

    private static IReadOnlyList<string> ExtractRedditImageUrls(JsonElement item)
    {
        var urls = new List<string>();
        var url = GetString(item, "url");
        var direct = NormalizeImageUrl(LooksLikeImage(url) ? url : null);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            urls.Add(direct);
        }

        if (item.TryGetProperty("gallery_data", out var gallery) &&
            gallery.TryGetProperty("items", out var galleryItems) &&
            item.TryGetProperty("media_metadata", out var metadata) &&
            galleryItems.ValueKind == JsonValueKind.Array &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var galleryItem in galleryItems.EnumerateArray())
            {
                var mediaId = GetString(galleryItem, "media_id");
                if (string.IsNullOrWhiteSpace(mediaId) ||
                    !metadata.TryGetProperty(mediaId, out var media))
                {
                    continue;
                }

                var sourceUrl = TryGetNestedString(media, ["s", "u"]) ??
                                TryGetNestedString(media, ["s", "gif"]);
                var normalized = NormalizeImageUrl(sourceUrl);
                if (LooksLikeImage(normalized))
                {
                    urls.Add(normalized!);
                }
            }
        }

        var previewUrl = TryGetNestedString(item, ["preview", "images", "0", "source", "url"]);
        var preview = NormalizeImageUrl(previewUrl);
        if (LooksLikeImage(preview))
        {
            urls.Add(preview!);
        }

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static string? ExtractRedditVideoUrl(JsonElement item)
    {
        var direct =
            TryGetNestedString(item, ["media", "reddit_video", "fallback_url"]) ??
            TryGetNestedString(item, ["secure_media", "reddit_video", "fallback_url"]) ??
            TryGetNestedString(item, ["preview", "reddit_video_preview", "fallback_url"]);

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return NormalizeVideoUrl(direct);
        }

        var url = GetString(item, "url");
        return LooksLikeVideoUrl(url) ? NormalizeVideoUrl(url) : null;
    }

    private static string? NormalizeVideoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decoded = System.Net.WebUtility.HtmlDecode(value).Trim().Replace("\\/", "/", StringComparison.Ordinal);
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
        {
            return decoded;
        }

        var embedMatch = Regex.Match(uri.ToString(), "youtube(?:-nocookie)?\\.com/embed/(?<id>[A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase);
        if (embedMatch.Success)
        {
            return $"https://www.youtube.com/watch?v={embedMatch.Groups["id"].Value}";
        }

        return uri.ToString();
    }

    private static string? TryGetNestedString(JsonElement element, IReadOnlyList<string> path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < current.GetArrayLength())
            {
                current = current.EnumerateArray().ElementAt(index);
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool LooksLikeTrailerCandidate(CollectedCandidate candidate)
    {
        return ContainsAny($"{candidate.Title}\n{candidate.Summary}\n{candidate.RawText}", [
            "trailer",
            "teaser",
            "showcase",
            "reveal",
            "анонс",
            "трейлер",
            "тизер",
            "показали"
        ]);
    }

    private static bool SourceAllowsKind(Source source, PublicationKind kind)
    {
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        return allowed.Count == 0 || allowed.Any(value => value.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool SourceOnlyAllowsKind(Source source, PublicationKind kind)
    {
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        return allowed.Count == 1 && allowed[0].Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int GetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.Number when property.TryGetDouble(out var value) => (long)value,
            _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;
    }

    private sealed record RedditFilters(List<string> Whitelist, List<string> Blacklist);
}
