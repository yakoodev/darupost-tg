using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services.Cards;

/// <summary>
/// Image generator front door. Publication types with <see cref="MediaGenerationMode.BrandCard"/> get a branded card
/// built from the real source image (no AI art); everything else (memes, legacy AI posters) goes to <see cref="PolzaImageGenerator"/>.
/// </summary>
public sealed partial class BrandCardImageGenerator(
    PolzaImageGenerator aiGenerator,
    AppDbContext db,
    BrandCardRenderer renderer,
    IHttpClientFactory httpClientFactory,
    TalentMatcher talents,
    INicheProfileProvider profiles,
    IOptions<MediaOptions> mediaOptionsAccessor,
    ILogger<BrandCardImageGenerator> logger) : IImageGenerator
{
    public const string HttpClientName = "brand-card";
    private const int MaxImageBytes = 15 * 1024 * 1024;
    private const int MaxPageBytes = 1_500_000;

    private static readonly string[] NoScrapeHosts = ["reddit.com", "redd.it", "x.com", "twitter.com", "t.me", "youtube.com", "youtu.be"];

    public async Task<ImageGenerationResult> GenerateForPostAsync(Channel channel, Post post, CancellationToken cancellationToken)
    {
        var mode = post.PublicationType?.MediaMode;
        if (mode is null && post.PublicationTypeId is Guid typeId)
        {
            mode = await db.PublicationTypes
                .Where(type => type.Id == typeId)
                .Select(type => (MediaGenerationMode?)type.MediaMode)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (mode != MediaGenerationMode.BrandCard)
        {
            return await aiGenerator.GenerateForPostAsync(channel, post, cancellationToken);
        }

        return await RenderBrandCardAsync(channel, post, cancellationToken);
    }

    private async Task<ImageGenerationResult> RenderBrandCardAsync(Channel channel, Post post, CancellationToken cancellationToken)
    {
        var profile = profiles.Get(channel.ProfileKey);

        var candidate = post.SourceCandidate;
        if (candidate is null && post.SourceCandidateId is Guid candidateId)
        {
            candidate = await db.SourceCandidates.FirstOrDefaultAsync(item => item.Id == candidateId, cancellationToken);
        }

        Source? source = null;
        if (candidate is not null)
        {
            source = candidate.Source ?? await db.Sources.AsNoTracking().FirstOrDefaultAsync(item => item.Id == candidate.SourceId, cancellationToken);
        }

        string? storyTalents = null;
        if (candidate?.StoryId is Guid storyId)
        {
            storyTalents = await db.Stories.Where(story => story.Id == storyId).Select(story => story.TalentsCsv).FirstOrDefaultAsync(cancellationToken);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        SKBitmap? visual = null;
        string? usedImage = null;
        foreach (var url in CollectImageUrls(post, candidate))
        {
            visual = await DownloadBitmapAsync(client, url, cancellationToken);
            if (visual is not null)
            {
                usedImage = url;
                break;
            }
        }

        if (visual is null && candidate?.Url is { } pageUrl && CanScrapePage(pageUrl))
        {
            var ogImage = await FindOpenGraphImageAsync(client, pageUrl, cancellationToken);
            if (ogImage is not null)
            {
                visual = await DownloadBitmapAsync(client, ogImage, cancellationToken);
                usedImage = visual is null ? null : ogImage;
            }
        }

        var kicker = await BuildKickerAsync(channel, post, storyTalents, cancellationToken);
        var content = new BrandCardContent(
            Rubric: ResolveRubric(profile, post),
            Headline: string.IsNullOrWhiteSpace(post.Headline) ? FallbackHeadline(post) : post.Headline!,
            Kicker: kicker,
            Visual: visual,
            SourceLabel: BuildSourceLabel(source, candidate),
            Urgent: post.PublicationKind == PublicationKind.BreakingNews);

        byte[] bytes;
        try
        {
            bytes = renderer.Render(content);
        }
        finally
        {
            visual?.Dispose();
        }

        var publicPath = LocalMediaPaths.BuildPublicPath("generated", $"{post.Id:N}.jpg");
        var fullPath = LocalMediaPaths.BuildFullPath(mediaOptionsAccessor.Value, publicPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        logger.LogInformation("Brand card rendered for post {PostId}: image={Image}, kicker={Kicker}.", post.Id, usedImage ?? "none", kicker ?? "-");

        return new ImageGenerationResult(
            publicPath,
            "brand-card",
            "brand-card",
            "skia",
            CostAmount: 0m,
            CostCurrency: "RUB",
            UsageMetadataJson: JsonSerializer.Serialize(new { renderer = "brand-card", sourceImage = usedImage, kicker, rubric = content.Rubric }));
    }

    // ---- content ----

    private static IEnumerable<string> CollectImageUrls(Post post, SourceCandidate? candidate)
    {
        var urls = new List<string>();

        // Video thumbnails: maxres first (hqdefault is letterboxed 4:3).
        foreach (var value in new[] { post.VideoUrl, candidate?.VideoUrl, candidate?.Url, post.SourceUrl })
        {
            var id = TryGetYouTubeId(value);
            if (id is not null)
            {
                urls.Add($"https://i.ytimg.com/vi/{id}/maxresdefault.jpg");
                urls.Add($"https://i.ytimg.com/vi/{id}/hqdefault.jpg");
                break;
            }
        }

        urls.AddRange(ReadMediaUrls(post.MediaUrlsJson));
        urls.AddRange(ReadMediaUrls(candidate?.MediaUrlsJson));
        foreach (var value in new[] { candidate?.ImageUrl, post.ImagePath })
        {
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(value.Trim());
            }
        }

        return urls
            .Select(url => WebUtility.HtmlDecode(url))
            .Where(url => !url.Contains("/thumbs/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8);
    }

    private async Task<string?> BuildKickerAsync(Channel channel, Post post, string? storyTalents, CancellationToken cancellationToken)
    {
        var text = string.Join('\n', post.Headline, post.SourceTitle, post.OriginalSummary);
        var matches = await talents.MatchAsync(channel.Id, text, cancellationToken);
        var top = matches
            .OrderBy(match => match.Priority)
            .ThenByDescending(match => !string.Equals(match.Name, match.Agency, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (top is not null)
        {
            var agency = AgencyLabel(top.Agency);
            return agency is null || string.Equals(agency, top.Name, StringComparison.OrdinalIgnoreCase)
                ? top.Name
                : $"{agency} · {top.Name}";
        }

        var fromStory = storyTalents?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(fromStory) ? null : fromStory;
    }

    private static string? AgencyLabel(string? agency)
    {
        if (string.IsNullOrWhiteSpace(agency))
        {
            return null;
        }

        return agency.Trim().ToLowerInvariant() switch
        {
            "indie (ru)" => "RU-сцена",
            "indie" => "Инди",
            _ => agency.Trim()
        };
    }

    private static string ResolveRubric(NicheProfile profile, Post post)
    {
        if (!string.IsNullOrWhiteSpace(post.Rubric))
        {
            return post.Rubric!;
        }

        if (profile.Prompts.Rubrics.TryGetValue(post.PublicationKind.ToString(), out var rubric) && !string.IsNullOrWhiteSpace(rubric))
        {
            return rubric;
        }

        return post.PublicationKind switch
        {
            PublicationKind.BreakingNews => "Срочно",
            PublicationKind.Rumor => "Слух",
            PublicationKind.Digest => "Дайджест",
            PublicationKind.Trailer => "Видео",
            _ => "Новости"
        };
    }

    private static string? BuildSourceLabel(Source? source, SourceCandidate? candidate)
    {
        if (source is null)
        {
            return null;
        }

        switch (source.Kind)
        {
            case SourceKind.Twitter:
            {
                var handle = candidate?.Author ?? source.Url?.Trim().TrimStart('@');
                return string.IsNullOrWhiteSpace(handle) ? "X" : $"X · @{handle.TrimStart('@')}";
            }
            case SourceKind.Reddit:
                return string.IsNullOrWhiteSpace(source.Subreddit) ? "Reddit" : $"Reddit · r/{source.Subreddit}";
            case SourceKind.YouTube:
                return "YouTube";
            case SourceKind.Telegram:
            {
                var match = Regex.Match(source.Url ?? string.Empty, "(?:t\\.me/(?:s/)?)?@?(?<name>[A-Za-z0-9_]{4,})");
                return match.Success ? $"Telegram · @{match.Groups["name"].Value}" : "Telegram";
            }
            default:
                return Uri.TryCreate(candidate?.Url, UriKind.Absolute, out var uri)
                    ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
                    : null;
        }
    }

    private static string FallbackHeadline(Post post)
    {
        var text = (post.FinalText ?? post.GeneratedText ?? post.SourceTitle).ReplaceLineEndings(" ").Replace("**", string.Empty).Trim();
        var end = text.IndexOfAny(['.', '!', '?']);
        var sentence = end is > 20 and < 110 ? text[..end] : text;
        return sentence.Length <= 90 ? sentence : sentence[..sentence.LastIndexOf(' ', 89)];
    }

    // ---- fetching ----

    private async Task<SKBitmap?> DownloadBitmapAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length is 0 or > MaxImageBytes)
            {
                return null;
            }

            buffer.Position = 0;
            var bitmap = SKBitmap.Decode(buffer);
            if (bitmap is null)
            {
                return null;
            }

            // Thumbnails and icons make blurry, pixelated cards — treat them as "no image".
            if (bitmap.Width < 320 || bitmap.Height < 200)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogDebug(ex, "Card image download failed: {Url}", url);
            return null;
        }
    }

    private async Task<string?> FindOpenGraphImageAsync(HttpClient client, string pageUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxPageBytes];
            var read = 0;
            int chunk;
            while (read < buffer.Length && (chunk = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += chunk;
            }

            var html = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var match = OgImageRegex().Match(html);
            if (!match.Success)
            {
                match = OgImageReversedRegex().Match(html);
            }

            if (!match.Success)
            {
                return null;
            }

            var value = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            return Uri.TryCreate(new Uri(pageUrl), value, out var absolute) ? absolute.ToString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            logger.LogDebug(ex, "og:image lookup failed: {Url}", pageUrl);
            return null;
        }
    }

    private static bool CanScrapePage(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" &&
               !NoScrapeHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReadMediaUrls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? TryGetYouTubeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = YouTubeIdRegex().Match(value);
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex("(?:youtube\\.com/(?:watch\\?(?:.*&)?v=|live/|shorts/|embed/)|youtu\\.be/)(?<id>[A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeIdRegex();

    [GeneratedRegex("<meta[^>]+(?:property|name)=[\"'](?:og:image(?::secure_url)?|twitter:image(?::src)?)[\"'][^>]*content=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();

    [GeneratedRegex("<meta[^>]+content=[\"'](?<url>[^\"']+)[\"'][^>]*(?:property|name)=[\"'](?:og:image(?::secure_url)?|twitter:image(?::src)?)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageReversedRegex();
}
