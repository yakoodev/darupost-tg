using System.Text.Json;
using System.Xml.Linq;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>Reddit subreddit listing via the public JSON API with an Atom RSS fallback.</summary>
public sealed class RedditCollector(HttpClient httpClient, VideoEnricher videoEnricher) : ISourceCollector
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2.5);

    /// <summary>Reddit rate-limits unauthenticated bursts hard (429); space requests out process-wide.</summary>
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

    private static void ApplyRedditHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) tg-autoposter/0.2 (news aggregator; contact: admin)");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("application/atom+xml");
        request.Headers.Accept.ParseAdd("text/xml");
    }

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.Reddit];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Subreddit))
        {
            return [];
        }

        try
        {
            return await CollectJsonAsync(source, profile, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return await CollectRssAsync(source, profile, cancellationToken);
        }
    }

    private async Task<IReadOnlyCollection<CollectedCandidate>> CollectJsonAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildJsonUrl(source));
        ApplyRedditHeaders(request);

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
                    transport = "json"
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
        ApplyRedditHeaders(request);

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
