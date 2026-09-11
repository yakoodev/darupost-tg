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
/// X (Twitter) account timeline rendered inside wspanel's logged-in Chrome (<see cref="WspanelClient"/>).
/// <see cref="Source.Url"/> is "@handle", "handle" or an x.com / twitter.com profile link.
/// Parses the rendered DOM: one &lt;article data-testid="tweet"&gt; per tweet.
/// </summary>
public sealed partial class TwitterCollector(WspanelClient wspanel) : ISourceCollector
{
    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.Twitter];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        var handle = ExtractHandle(source.Url);
        if (string.IsNullOrWhiteSpace(handle))
        {
            return [];
        }

        if (!wspanel.IsConfigured)
        {
            throw new InvalidOperationException("Коллектор X требует настроенный wspanel (Wspanel:Enabled/BaseUrl/ApiKey/Profile).");
        }

        var pages = await wspanel.ReadAsync([$"https://x.com/{handle}"], includeHtml: true, ReadProfileOverride(source), cancellationToken);
        var page = pages.FirstOrDefault();
        if (page is null)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(page.Error))
        {
            throw new InvalidOperationException($"wspanel не открыл x.com/{handle}: {page.Error}");
        }

        var html = page.Html ?? string.Empty;
        if (page.FinalUrl?.Contains("/i/flow/login", StringComparison.OrdinalIgnoreCase) == true ||
            html.Contains("data-testid=\"loginButton\"", StringComparison.Ordinal) && !html.Contains("data-testid=\"tweet\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Chrome в wspanel-столе не залогинен в X: страница ушла на форму входа.");
        }

        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match article in ArticleRegex().Matches(html))
        {
            var body = article.Groups["body"].Value;

            var statusMatch = StatusLinkRegex().Match(body);
            if (!statusMatch.Success)
            {
                continue;
            }

            var author = statusMatch.Groups["user"].Value;
            var tweetId = statusMatch.Groups["id"].Value;
            if (!seen.Add(tweetId))
            {
                continue;
            }

            // Replies from the account to others show up on the profile too; keep only the account's own posts
            // (and reposts, which are rendered with the original author's status link).
            var isRepost = body.Contains("socialContext", StringComparison.Ordinal);
            if (!author.Equals(handle, StringComparison.OrdinalIgnoreCase) && !isRepost)
            {
                continue;
            }

            var textHtml = TweetTextRegex().Match(body).Groups["text"].Value;
            var text = StripHtml(DecodeEmojiAlt(textHtml)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!PassesTextFilters(filters, text) || !IsUsefulCandidateForSource(source, profile, text))
            {
                continue;
            }

            var date = DateTimeOffset.TryParse(TimeRegex().Match(body).Groups["date"].Value, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            var images = ImageRegex().Matches(body)
                .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
                .Where(url => url.Contains("/media/", StringComparison.Ordinal))
                .Select(NormalizeTwitterImage)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            var hasVideo = body.Contains("data-testid=\"videoPlayer\"", StringComparison.Ordinal) ||
                           body.Contains("data-testid=\"videoComponent\"", StringComparison.Ordinal);

            var tweetUrl = $"https://x.com/{author}/status/{tweetId}";
            var title = FirstLine(text, 160);

            result.Add(new CollectedCandidate(
                title,
                tweetUrl,
                BuildSummary(title, text.Length > 800 ? $"{text[..800]}..." : text),
                text,
                images.FirstOrDefault(),
                null,
                null,
                date,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    handle,
                    author,
                    tweetId,
                    isRepost,
                    hasVideo,
                    transport = "wspanel-x"
                }),
                VideoUrl: hasVideo ? tweetUrl : null,
                MediaUrls: images.Count > 0 ? images : null,
                ExternalId: tweetId,
                Author: author));
        }

        return result;
    }

    private static string? ReadProfileOverride(Source source)
    {
        if (string.IsNullOrWhiteSpace(source.SettingsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(source.SettingsJson);
            return doc.RootElement.TryGetProperty("wspanelProfile", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractHandle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, "(?:x\\.com|twitter\\.com)/(?<name>[A-Za-z0-9_]{1,15})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["name"].Value;
        }

        trimmed = trimmed.TrimStart('@');
        return Regex.IsMatch(trimmed, "^[A-Za-z0-9_]{1,15}$") ? trimmed : null;
    }

    private static string FirstLine(string text, int max)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;
        return line.Length <= max ? line : $"{line[..(max - 3)]}...";
    }

    /// <summary>X renders emoji as &lt;img alt="😀"&gt;; keep the alt so the text stays readable.</summary>
    private static string DecodeEmojiAlt(string html)
    {
        return Regex.Replace(html, "<img[^>]+alt=\"(?<alt>[^\"]*)\"[^>]*>", match => match.Groups["alt"].Value);
    }

    private static string NormalizeTwitterImage(string url)
    {
        // pbs.twimg.com/media/XXXX?format=jpg&name=small → request the large variant.
        return Regex.Replace(url, "([?&])name=[A-Za-z0-9_]+", "$1name=large");
    }

    [GeneratedRegex("<article[^>]*data-testid=\"tweet\"[^>]*>(?<body>.*?)</article>", RegexOptions.Singleline)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex("href=\"/(?<user>[A-Za-z0-9_]{1,15})/status/(?<id>\\d{5,})\"", RegexOptions.Singleline)]
    private static partial Regex StatusLinkRegex();

    [GeneratedRegex("data-testid=\"tweetText\"[^>]*>(?<text>.*?)</div>\\s*(?:</div>|<div)", RegexOptions.Singleline)]
    private static partial Regex TweetTextRegex();

    [GeneratedRegex("<time[^>]+datetime=\"(?<date>[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex TimeRegex();

    [GeneratedRegex("<img[^>]+src=\"(?<url>https://pbs\\.twimg\\.com/[^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex ImageRegex();
}
