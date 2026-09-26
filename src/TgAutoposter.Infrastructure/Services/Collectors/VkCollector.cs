using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>
/// Public VK community walls via the VK API (wall.get). Reads open groups with the service token — no user login.
/// Built for RU VTuber aggregators like VtuN (vk.com/vtun_vtubernews): debuts, covers, new models, events, with images.
/// <see cref="Source.Url"/> accepts a screen name ("vtun_vtubernews"), a vk.com link, "club123"/"public123", or a numeric "-123".
/// </summary>
public sealed partial class VkCollector(HttpClient httpClient, IOptions<VkOptions> optionsAccessor) : ISourceCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.Vk];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Коллектор VK не настроен (Vk:Enabled/Vk:ServiceToken).");
        }

        var reference = ExtractCommunityRef(source.Url);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return [];
        }

        var ownerId = await ResolveOwnerIdAsync(options, reference, cancellationToken);
        if (ownerId == 0)
        {
            throw new InvalidOperationException($"VK: не удалось разрешить сообщество '{reference}'.");
        }

        var root = await CallAsync(options, "wall.get", new Dictionary<string, string>
        {
            ["owner_id"] = ownerId.ToString(),
            ["count"] = Math.Clamp(options.PostsPerCommunity, 1, 100).ToString(),
            ["extended"] = "0"
        }, cancellationToken);

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        var absOwner = Math.Abs(ownerId);

        foreach (var post in items.EnumerateArray())
        {
            // Ads and reposts of other communities are noise for a curated feed.
            if (GetInt(post, "marked_as_ads") == 1)
            {
                continue;
            }

            var rawText = CleanVkText(GetString(post, "text"));
            if (string.IsNullOrWhiteSpace(rawText))
            {
                continue;
            }

            if (!PassesTextFilters(filters, rawText) || !IsUsefulCandidateForSource(source, profile, rawText))
            {
                continue;
            }

            var postId = GetInt(post, "id");
            var date = post.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(d.GetInt64())
                : DateTimeOffset.UtcNow;

            var (imageUrl, mediaUrls) = ExtractPhotos(post);
            var videoUrl = ExtractYouTubeLink(post);

            // Covers usually carry only a YouTube link and no VK photo — use the video thumbnail so the post still has an image.
            if (string.IsNullOrWhiteSpace(imageUrl) && !string.IsNullOrWhiteSpace(videoUrl))
            {
                imageUrl = YouTubeThumbnail(videoUrl);
            }

            // VtuN cover posts are just "° КАВЕР от X ! Видео: <url>" — enrich with the YouTube title so the writer has the song.
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                var ytTitle = await FetchYouTubeTitleAsync(videoUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ytTitle))
                {
                    rawText = $"{rawText}\nНазвание видео на YouTube: {ytTitle}";
                }
            }

            var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var title = lines.Length > 0 ? lines[0] : rawText;
            if (title.Length > 160)
            {
                title = $"{title[..157]}...";
            }

            var url = $"https://vk.com/wall{ownerId}_{postId}";
            result.Add(new CollectedCandidate(
                title,
                url,
                BuildSummary(title, rawText.Length > 800 ? $"{rawText[..800]}..." : rawText),
                rawText,
                imageUrl,
                null,
                null,
                date,
                JsonSerializer.Serialize(new { source = source.Name, community = reference, ownerId, post = postId, transport = "vk-api" }),
                VideoUrl: videoUrl,
                MediaUrls: mediaUrls.Count > 0 ? mediaUrls : null,
                ExternalId: $"{absOwner}_{postId}",
                Author: source.Name));
        }

        return result;
    }

    private async Task<long> ResolveOwnerIdAsync(VkOptions options, string reference, CancellationToken cancellationToken)
    {
        // Numeric forms: "-123" (already an owner id), "123", "club123", "public123".
        var numeric = Regex.Match(reference, @"^(?:-|club|public|event)?(\d+)$", RegexOptions.IgnoreCase);
        if (numeric.Success)
        {
            return -long.Parse(numeric.Groups[1].Value);
        }

        var root = await CallAsync(options, "groups.getById", new Dictionary<string, string>
        {
            ["group_ids"] = reference
        }, cancellationToken);

        // v5.199 returns { "groups": [...] }; older shapes return a bare array in "response".
        var groups = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("groups", out var g) ? g : default;
        if (groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() > 0)
        {
            var id = GetInt(groups[0], "id");
            if (id != 0)
            {
                return -id;
            }
        }

        return 0;
    }

    private async Task<JsonElement> CallAsync(VkOptions options, string method, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        parameters["access_token"] = options.ServiceToken!;
        parameters["v"] = options.ApiVersion;
        var query = string.Join('&', parameters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var requestUrl = $"{options.ApiBaseUrl.TrimEnd('/')}/{method}?{query}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(body);
        var rootElement = document.RootElement;
        if (rootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("error_msg", out var m) ? m.GetString() : "unknown";
            var code = error.TryGetProperty("error_code", out var c) ? c.GetInt32() : -1;
            throw new InvalidOperationException($"VK API {method} ошибка {code}: {message}");
        }

        return rootElement.TryGetProperty("response", out var resp) ? resp.Clone() : default;
    }

    private static (string? ImageUrl, List<string> MediaUrls) ExtractPhotos(JsonElement post)
    {
        var media = new List<string>();
        if (post.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (GetString(attachment, "type") != "photo" || !attachment.TryGetProperty("photo", out var photo))
                {
                    continue;
                }

                if (!photo.TryGetProperty("sizes", out var sizes) || sizes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string? best = null;
                var bestWidth = -1;
                foreach (var size in sizes.EnumerateArray())
                {
                    var width = GetInt(size, "width");
                    var link = GetString(size, "url");
                    if (!string.IsNullOrWhiteSpace(link) && width > bestWidth)
                    {
                        bestWidth = width;
                        best = link;
                    }
                }

                if (!string.IsNullOrWhiteSpace(best))
                {
                    media.Add(best!);
                }
            }
        }

        return (media.FirstOrDefault(), media);
    }

    private async Task<string?> FetchYouTubeTitleAsync(string videoUrl, CancellationToken cancellationToken)
    {
        try
        {
            var oembed = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(videoUrl)}&format=json";
            using var response = await httpClient.GetAsync(oembed, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? YouTubeThumbnail(string videoUrl)
    {
        var m = Regex.Match(videoUrl, @"(?:v=|youtu\.be/|shorts/|live/)(?<id>[A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase);
        return m.Success ? $"https://img.youtube.com/vi/{m.Groups["id"].Value}/hqdefault.jpg" : null;
    }

    private static string? ExtractYouTubeLink(JsonElement post)
    {
        if (post.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (GetString(attachment, "type") == "link" && attachment.TryGetProperty("link", out var link))
                {
                    var url = GetString(link, "url");
                    if (!string.IsNullOrWhiteSpace(url) && YouTubeLinkRegex().IsMatch(url))
                    {
                        return WebUtility.HtmlDecode(url);
                    }
                }
            }
        }

        var youTube = YouTubeLinkRegex().Match(GetString(post, "text"));
        return youTube.Success ? youTube.Value : null;
    }

    /// <summary>VK inline markup [club123|Name] / [id45|Name] → "Name"; strip zero-width and collapse blank lines.</summary>
    private static string CleanVkText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = MentionRegex().Replace(text, "${name}");
        cleaned = cleaned.Replace("​", string.Empty, StringComparison.Ordinal);
        return cleaned.Trim();
    }

    private static string ExtractCommunityRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var match = Regex.Match(trimmed, @"vk\.(?:com|ru)/(?<name>[A-Za-z0-9_.]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["name"].Value;
        }

        return trimmed.TrimStart('@');
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    // VK inline mention markup is always [ref|Visible Name] — ref may be club123, id45, or a vk.com/vk.ru link.
    [GeneratedRegex(@"\[[^\[\]|]+\|(?<name>[^\[\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"https?://(?:www\.|m\.)?(?:youtube\.com/(?:watch\?v=|shorts/|live/)|youtu\.be/)[A-Za-z0-9_-]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeLinkRegex();
}
