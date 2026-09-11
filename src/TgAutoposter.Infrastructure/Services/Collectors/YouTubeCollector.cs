using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>
/// YouTube channel uploads via the public Atom feed (no API key). <see cref="Source.Url"/> may be a channel id
/// (UC…), a channel/handle URL (youtube.com/@handle, /channel/UC…, /c/name) or a ready feed URL.
/// The resolved channel id is cached in <see cref="Source.SettingsJson"/>.
/// </summary>
public sealed partial class YouTubeCollector(HttpClient httpClient) : ISourceCollector
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.YouTube];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            return [];
        }

        var channelId = await ResolveChannelIdAsync(source, cancellationToken)
            ?? throw new InvalidOperationException($"Не удалось определить YouTube channel id для «{source.Url}».");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(channelId)}");
        ApplyHeaders(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var filters = CreateFilters(source);
        var channelName = document.Root?.Element(Atom + "title")?.Value.Trim();
        var result = new List<CollectedCandidate>();

        foreach (var entry in document.Descendants(Atom + "entry").Take(30))
        {
            var videoId = entry.Element(Yt + "videoId")?.Value.Trim();
            var title = entry.Element(Atom + "title")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var group = entry.Element(Media + "group");
            var description = group?.Element(Media + "description")?.Value.Trim();
            var thumbnail = group?.Element(Media + "thumbnail")?.Attribute("url")?.Value;
            var author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value.Trim() ?? channelName;
            var published = DateTimeOffset.TryParse(entry.Element(Atom + "published")?.Value, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            var haystack = $"{title}\n{description}";
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, profile, haystack))
            {
                continue;
            }

            var videoUrl = $"https://www.youtube.com/watch?v={videoId}";
            result.Add(new CollectedCandidate(
                title,
                videoUrl,
                BuildSummary(title, Shorten(description, 600)),
                description,
                thumbnail,
                null,
                null,
                published,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    channelId,
                    channelName,
                    videoId,
                    transport = "youtube-feed"
                }),
                VideoUrl: videoUrl,
                ExternalId: videoId,
                Author: author));
        }

        return result;
    }

    private async Task<string?> ResolveChannelIdAsync(Source source, CancellationToken cancellationToken)
    {
        var input = source.Url!.Trim();

        var direct = ChannelIdRegex().Match(input);
        if (direct.Success)
        {
            return direct.Value;
        }

        var cached = ReadCachedChannelId(source);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var pageUrl = input.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? input
            : input.StartsWith('@')
                ? $"https://www.youtube.com/{input}"
                : $"https://www.youtube.com/@{input}";

        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        ApplyHeaders(request);
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = Regex.Match(html, "\"(?:channelId|externalId)\"\\s*:\\s*\"(?<id>UC[A-Za-z0-9_-]{22})\"")
            .Success
            ? Regex.Match(html, "\"(?:channelId|externalId)\"\\s*:\\s*\"(?<id>UC[A-Za-z0-9_-]{22})\"")
            : Regex.Match(html, "<meta[^>]+itemprop=\"identifier\"[^>]+content=\"(?<id>UC[A-Za-z0-9_-]{22})\"");
        if (!match.Success)
        {
            match = Regex.Match(html, "channel_id=(?<id>UC[A-Za-z0-9_-]{22})");
        }

        if (!match.Success)
        {
            return null;
        }

        var channelId = match.Groups["id"].Value;
        source.SettingsJson = JsonSerializer.Serialize(new { youtubeChannelId = channelId, resolvedAtUtc = DateTimeOffset.UtcNow });
        return channelId;
    }

    private static string? ReadCachedChannelId(Source source)
    {
        if (string.IsNullOrWhiteSpace(source.SettingsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(source.SettingsJson);
            return doc.RootElement.TryGetProperty("youtubeChannelId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Shorten(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= max ? normalized : $"{normalized[..max]}...";
    }

    [GeneratedRegex("UC[A-Za-z0-9_-]{22}")]
    private static partial Regex ChannelIdRegex();
}
