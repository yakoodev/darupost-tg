using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>
/// Reddit subreddit listing. Transport order: OAuth (if a script app is configured) → anonymous JSON →
/// wspanel remote browser (if configured; anonymous access is blocked from many IPs) → Atom RSS.
/// </summary>
public sealed class RedditCollector(
    HttpClient httpClient,
    VideoEnricher videoEnricher,
    WspanelClient wspanel,
    IOptions<RedditOptions> optionsAccessor,
    ILogger<RedditCollector> logger) : ISourceCollector
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2.5);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private static string? _oauthToken;
    private static DateTimeOffset _oauthExpiresAt = DateTimeOffset.MinValue;

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.Reddit];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Subreddit))
        {
            return [];
        }

        var options = optionsAccessor.Value;
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            try
            {
                var json = await FetchOAuthListingAsync(source, options, cancellationToken);
                return await ParseListingAsync(source, profile, json, "oauth", cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                errors.Add($"oauth: {ex.Message}");
                logger.LogWarning(ex, "Reddit OAuth listing failed for r/{Subreddit}.", source.Subreddit);
            }
        }

        try
        {
            var json = await FetchAnonymousListingAsync(source, options, cancellationToken);
            return await ParseListingAsync(source, profile, json, "json", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            errors.Add($"json: {ex.Message}");
        }

        if (options.UseWspanelFallback && wspanel.IsConfigured)
        {
            try
            {
                var pages = await wspanel.ReadAsync([BuildJsonUrl(source)], includeHtml: false, null, cancellationToken, waitMs: 800);
                var page = pages.FirstOrDefault();
                var text = page?.Text?.Trim();
                if (page is not null && string.IsNullOrWhiteSpace(page.Error) && !string.IsNullOrWhiteSpace(text))
                {
                    var start = text.IndexOf('{');
                    var json = start >= 0 ? text[start..] : text;
                    return await ParseListingAsync(source, profile, json, "wspanel", cancellationToken);
                }

                errors.Add($"wspanel: {page?.Error ?? "пустой ответ"}");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                errors.Add($"wspanel: {ex.Message}");
            }
        }

        try
        {
            return await CollectRssAsync(source, profile, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            errors.Add($"rss: {ex.Message}");
            throw new HttpRequestException($"Reddit r/{source.Subreddit} недоступен: {string.Join("; ", errors)}");
        }
    }

    // ---- transports ----

    private async Task<string> FetchOAuthListingAsync(Source source, RedditOptions options, CancellationToken cancellationToken)
    {
        var token = await GetOAuthTokenAsync(options, cancellationToken);
        await ThrottleAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildJsonUrl(source, "https://oauth.reddit.com", withJsonSuffix: false));
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _oauthToken = null; // force refresh next time
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string> GetOAuthTokenAsync(RedditOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_oauthToken) && _oauthExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return _oauthToken;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token");
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.ClientId}:{options.ClientSecret}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Reddit OAuth token failed: {(int)response.StatusCode} {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var token = GetString(doc.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Reddit OAuth response has no access_token.");
        }

        var expires = doc.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 3600;
        _oauthToken = token;
        _oauthExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires);
        return token;
    }

    private async Task<string> FetchAnonymousListingAsync(Source source, RedditOptions options, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildJsonUrl(source));
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Reddit rate-limits anonymous bursts hard (429); space requests out process-wide.</summary>
    private static async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastRequestAt + MinGap - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---- parsing ----

    private async Task<IReadOnlyCollection<CollectedCandidate>> ParseListingAsync(
        Source source,
        NicheProfile profile,
        string json,
        string transport,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
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
                !IsUsefulCandidateForSource(source, profile, haystack) ||
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
            var redditId = GetString(item, "id");
            var author = GetString(item, "author");

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
                    redditId,
                    author,
                    sourceUrl = url,
                    videoUrl,
                    mediaUrls,
                    transport
                }),
                VideoUrl: videoUrl,
                MediaUrls: mediaUrls.Count > 0 ? mediaUrls : null,
                ExternalId: redditId,
                Author: author));
        }

        return await videoEnricher.EnrichAsync(source, profile, result, cancellationToken);
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> CollectRssAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRssUrl(source));
        request.Headers.UserAgent.ParseAdd(optionsAccessor.Value.UserAgent);
        request.Headers.Accept.ParseAdd("application/atom+xml");
        request.Headers.Accept.ParseAdd("text/xml");

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

            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, profile, haystack))
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

            var redditId = entry.Element(Atom + "id")?.Value;
            var author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value;

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
                    redditId,
                    author,
                    videoUrl,
                    transport = "rss"
                }),
                VideoUrl: videoUrl,
                ExternalId: redditId,
                Author: author));
        }

        return await videoEnricher.EnrichAsync(source, profile, result, cancellationToken);
    }

    // ---- urls ----

    private static string BuildJsonUrl(Source source, string host = "https://www.reddit.com", bool withJsonSuffix = true)
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

        var query = listing == "top" ? $"?limit=25&t={period}&raw_json=1" : "?limit=25&raw_json=1";
        var suffix = withJsonSuffix ? "/.json" : string.Empty;
        return $"{host}/r/{Uri.EscapeDataString(source.Subreddit!)}/{listing}{suffix}{query}";
    }

    private static string BuildRssUrl(Source source)
    {
        return $"https://www.reddit.com/r/{Uri.EscapeDataString(source.Subreddit!)}/.rss";
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
}
