using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
/// The panel returns the page's rendered text (innerText), not the live DOM, so tweets are parsed from text:
/// each tweet starts with "display name / @handle / date" and ends before the engagement counters.
/// <see cref="Source.Url"/> is "@handle", "handle" or an x.com / twitter.com profile link.
/// </summary>
public sealed partial class TwitterCollector(WspanelClient wspanel) : ISourceCollector
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["янв"] = 1, ["февр"] = 2, ["фев"] = 2, ["мар"] = 3, ["март"] = 3, ["апр"] = 4, ["мая"] = 5, ["май"] = 5, ["июн"] = 6,
        ["июл"] = 7, ["авг"] = 8, ["сент"] = 9, ["сен"] = 9, ["окт"] = 10, ["нояб"] = 11, ["ноя"] = 11, ["дек"] = 12,
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6, ["jul"] = 7, ["aug"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12
    };

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

        var text = page.Text ?? string.Empty;
        if (page.FinalUrl?.Contains("/i/flow/login", StringComparison.OrdinalIgnoreCase) == true ||
            page.FinalUrl?.Contains("/login", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("Chrome в wspanel-столе не залогинен в X: страница ушла на форму входа.");
        }

        if (text.Length < 200)
        {
            throw new InvalidOperationException($"X отдал почти пустую страницу для @{handle} ({text.Length} символов) — похоже на ограничение или капчу; повторим позже.");
        }

        // Rendered HTML carries status ids and tweet photos; the plain-text parser is only a fallback.
        var tweets = ParseArticles(page.Html ?? string.Empty, handle, DateTimeOffset.UtcNow);
        if (tweets.Count == 0)
        {
            tweets = ParseTweets(text, handle, DateTimeOffset.UtcNow);
        }
        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        foreach (var tweet in tweets)
        {
            if (!PassesTextFilters(filters, tweet.Body) || !IsUsefulCandidateForSource(source, profile, tweet.Body))
            {
                continue;
            }

            var title = FirstLine(tweet.Body, 160);
            var externalId = tweet.StatusId ?? Sha1($"{tweet.Author}|{tweet.DateLine}|{tweet.Body[..Math.Min(tweet.Body.Length, 120)]}");
            var images = tweet.Images ?? [];
            result.Add(new CollectedCandidate(
                title,
                tweet.StatusId is null ? $"https://x.com/{tweet.Author}" : $"https://x.com/{tweet.Author}/status/{tweet.StatusId}",
                BuildSummary(title, tweet.Body.Length > 800 ? $"{tweet.Body[..800]}..." : tweet.Body),
                tweet.Body,
                images.FirstOrDefault(),
                null,
                null,
                tweet.PostedAt,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    handle,
                    author = tweet.Author,
                    dateLine = tweet.DateLine,
                    isRepost = tweet.IsRepost,
                    isPinned = tweet.IsPinned,
                    statusId = tweet.StatusId,
                    transport = tweet.StatusId is null ? "wspanel-x-text" : "wspanel-x-html"
                }),
                MediaUrls: images.Count > 0 ? images : null,
                ExternalId: externalId,
                Author: tweet.Author));
        }

        return result;
    }

    internal sealed record ParsedTweet(
        string Author,
        string DateLine,
        DateTimeOffset PostedAt,
        string Body,
        bool IsRepost,
        bool IsPinned,
        string? StatusId = null,
        IReadOnlyList<string>? Images = null);

    /// <summary>
    /// Parses the profile timeline HTML returned by wspanel: one &lt;article&gt; per tweet with the status link
    /// (whose text is the relative time), the tweet text in div[dir=auto] and photos on pbs.twimg.com.
    /// </summary>
    internal static List<ParsedTweet> ParseArticles(string html, string handle, DateTimeOffset now)
    {
        var result = new List<ParsedTweet>();
        if (string.IsNullOrEmpty(html))
        {
            return result;
        }

        foreach (Match article in ArticleBlockRegex().Matches(html))
        {
            var body = article.Groups["body"].Value;
            var status = StatusTimeRegex().Match(body);
            if (!status.Success)
            {
                continue;
            }

            var author = status.Groups["user"].Value;
            var statusId = status.Groups["id"].Value;
            var dateLine = WebUtility.HtmlDecode(status.Groups["time"].Value).Trim();
            var postedAt = TryParseDate(dateLine, now) ?? now;

            var textMatch = TweetBodyRegex().Match(body);
            var text = textMatch.Success ? HtmlToText(textMatch.Groups["body"].Value) : string.Empty;
            var images = MediaUrlRegex().Matches(body)
                .Select(match => NormalizeTwitterMedia(match.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            if (text.Length < 3 && images.Count == 0)
            {
                continue;
            }

            var isPinned = body.Contains("Закреплено", StringComparison.OrdinalIgnoreCase) || body.Contains(">Pinned<", StringComparison.OrdinalIgnoreCase);
            var isRepost = !author.Equals(handle, StringComparison.OrdinalIgnoreCase);
            result.Add(new ParsedTweet(author, dateLine, postedAt, text.Length < 3 ? "Медиа без текста" : text, isRepost, isPinned, statusId, images));
        }

        return result
            .GroupBy(tweet => tweet.StatusId)
            .Select(group => group.First())
            .ToList();
    }

    private static string HtmlToText(string html)
    {
        var value = ButtonRegex().Replace(html, string.Empty);
        value = BreakRegex().Replace(value, "\n");
        value = TagRegex().Replace(value, string.Empty);
        value = WebUtility.HtmlDecode(value).Replace('\u00a0', ' ');
        var lines = value.Split('\n').Select(line => line.Trim());
        return MultiBlankRegex().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    private static string NormalizeTwitterMedia(string url)
    {
        var decoded = WebUtility.HtmlDecode(url);
        var question = decoded.IndexOf('?');
        var basePath = question >= 0 ? decoded[..question] : decoded;
        return $"{basePath}?format=jpg&name=large";
    }

    /// <summary>Parses X's rendered profile text into tweets.</summary>
    internal static List<ParsedTweet> ParseTweets(string text, string handle, DateTimeOffset now)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n').Select(line => line.Trim()).ToList();
        var headers = new List<int>();
        for (var i = 0; i + 2 < lines.Count; i++)
        {
            if (lines[i].Length > 0 && HandleRegex().IsMatch(lines[i + 1]) && TryParseDate(lines[i + 2], now) is not null)
            {
                headers.Add(i);
            }
        }

        var result = new List<ParsedTweet>();
        for (var h = 0; h < headers.Count; h++)
        {
            var start = headers[h];
            var end = h + 1 < headers.Count ? headers[h + 1] : lines.Count;
            var author = lines[start + 1].TrimStart('@');
            var dateLine = lines[start + 2];
            var postedAt = TryParseDate(dateLine, now) ?? now;
            var previous = start > 0 ? lines[start - 1] : string.Empty;
            var isPinned = previous.Contains("Закреплено", StringComparison.OrdinalIgnoreCase) || previous.Contains("Pinned", StringComparison.OrdinalIgnoreCase);
            var isRepost = previous.Contains("репост", StringComparison.OrdinalIgnoreCase) || previous.Contains("reposted", StringComparison.OrdinalIgnoreCase);

            var body = new List<string>();
            var counters = 0;
            for (var i = start + 3; i < end; i++)
            {
                var line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }

                if (CounterRegex().IsMatch(line))
                {
                    counters++;
                    if (counters >= 2)
                    {
                        break; // engagement block (replies / reposts / likes / views)
                    }

                    body.Add(line);
                    continue;
                }

                counters = 0;
                if (!IsNoise(line))
                {
                    body.Add(line);
                }
            }

            while (body.Count > 0 && CounterRegex().IsMatch(body[^1]))
            {
                body.RemoveAt(body.Count - 1);
            }

            var bodyText = string.Join("\n", body).Trim();
            if (bodyText.Length < 3)
            {
                continue;
            }

            // Only the account's own posts (and reposts it made); replies to others are skipped.
            if (!author.Equals(handle, StringComparison.OrdinalIgnoreCase) && !isRepost)
            {
                continue;
            }

            result.Add(new ParsedTweet(author, dateLine, postedAt, bodyText, isRepost, isPinned));
        }

        return result;
    }

    private static bool IsNoise(string line)
    {
        return line is "·" or "/" or "／" or "＼" ||
               line.StartsWith("В ответ", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Replying to", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Показать ещё", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Show more", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Реклама", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Ad", StringComparison.OrdinalIgnoreCase);
    }

    internal static DateTimeOffset? TryParseDate(string line, DateTimeOffset now)
    {
        var value = line.Trim().TrimEnd('.');
        if (value.Length == 0 || value.Length > 24)
        {
            return null;
        }

        var relative = RelativeRegex().Match(value);
        if (relative.Success)
        {
            var amount = int.Parse(relative.Groups["n"].Value, CultureInfo.InvariantCulture);
            return relative.Groups["u"].Value.ToLowerInvariant() switch
            {
                "с" or "сек" or "s" => now.AddSeconds(-amount),
                "мин" or "м" or "m" or "min" => now.AddMinutes(-amount),
                "ч" or "h" => now.AddHours(-amount),
                "д" or "d" => now.AddDays(-amount),
                _ => null
            };
        }

        // "7 сент", "7 сент. 2025", "7 Sep", "7 Sep 2025"
        var dayFirst = DayFirstRegex().Match(value);
        if (dayFirst.Success && Months.TryGetValue(dayFirst.Groups["mon"].Value, out var month))
        {
            return BuildDate(dayFirst.Groups["year"], month, dayFirst.Groups["day"].Value, now);
        }

        // "Sep 7", "Sep 7, 2025"
        var monthFirst = MonthFirstRegex().Match(value);
        if (monthFirst.Success && Months.TryGetValue(monthFirst.Groups["mon"].Value, out month))
        {
            return BuildDate(monthFirst.Groups["year"], month, monthFirst.Groups["day"].Value, now);
        }

        return null;
    }

    private static DateTimeOffset? BuildDate(Group yearGroup, int month, string dayValue, DateTimeOffset now)
    {
        var day = int.Parse(dayValue, CultureInfo.InvariantCulture);
        var year = yearGroup.Success ? int.Parse(yearGroup.Value, CultureInfo.InvariantCulture) : now.Year;
        try
        {
            var date = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);
            if (!yearGroup.Success && date > now.AddDays(1))
            {
                date = date.AddYears(-1);
            }

            return date;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Skip decorative openers ("📢 News 📢", "／") and take the first line with real words.
        var line = lines.FirstOrDefault(candidate => candidate.Count(char.IsLetterOrDigit) >= 12)
                   ?? lines.FirstOrDefault()
                   ?? text;
        return line.Length <= max ? line : $"{line[..(max - 3)]}...";
    }

    private static string Sha1(string value)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    [GeneratedRegex("<article\\b[^>]*>(?<body>.*?)</article>", RegexOptions.Singleline)]
    private static partial Regex ArticleBlockRegex();

    [GeneratedRegex("href=\"/(?<user>[A-Za-z0-9_]{1,15})/status/(?<id>\\d{6,})\"[^>]*>(?<time>[^<]{1,24})</a>", RegexOptions.Singleline)]
    private static partial Regex StatusTimeRegex();

    [GeneratedRegex("<div dir=\"auto\"[^>]*>(?<body>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex TweetBodyRegex();

    [GeneratedRegex("https://pbs\\.twimg\\.com/(?:media|amplify_video_thumb|ext_tw_video_thumb|tweet_video_thumb)/[^\"'\\s?<&]+")]
    private static partial Regex MediaUrlRegex();

    [GeneratedRegex("<button\\b.*?</button>", RegexOptions.Singleline)]
    private static partial Regex ButtonRegex();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex MultiBlankRegex();

    [GeneratedRegex("^@[A-Za-z0-9_]{1,15}$")]
    private static partial Regex HandleRegex();

    // "12", "1,2 тыс.", "100 тыс.", "1.2K", "3M", "12 345"
    [GeneratedRegex("^\\d{1,3}(?:[ \\u00a0,.]\\d{1,3})*(?:\\s?(?:тыс\\.?|млн|K|M))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CounterRegex();

    [GeneratedRegex("^(?<n>\\d{1,3})\\s?(?<u>с|сек|мин|м|ч|д|s|m|min|h|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeRegex();

    [GeneratedRegex("^(?<day>\\d{1,2})\\s(?<mon>[а-яА-Яa-zA-Z]{3,5})\\.?(?:\\s(?<year>\\d{4}))?(?:\\s?г\\.?)?$")]
    private static partial Regex DayFirstRegex();

    [GeneratedRegex("^(?<mon>[A-Za-z]{3,4})\\s(?<day>\\d{1,2})(?:,\\s(?<year>\\d{4}))?$")]
    private static partial Regex MonthFirstRegex();
}
