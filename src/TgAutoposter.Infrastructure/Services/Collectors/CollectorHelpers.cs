using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>Shared, niche-agnostic helpers for all source collectors.</summary>
internal static class CollectorHelpers
{
    public sealed record TextFilters(List<string> Whitelist, List<string> Blacklist);

    public static void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("tg-autoposter/0.2 by local-admin");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("application/atom+xml");
        request.Headers.Accept.ParseAdd("application/rss+xml");
        request.Headers.Accept.ParseAdd("text/xml");
        request.Headers.Accept.ParseAdd("text/html");
    }

    public static TextFilters CreateFilters(Source source)
    {
        return new TextFilters(SplitCsv(source.WhitelistKeywordsCsv), SplitCsv(source.BlacklistKeywordsCsv));
    }

    public static bool PassesTextFilters(TextFilters filters, string haystack)
    {
        if (filters.Blacklist.Count > 0 && filters.Blacklist.Any(keyword => Contains(haystack, keyword)))
        {
            return false;
        }

        return filters.Whitelist.Count == 0 || filters.Whitelist.Any(keyword => Contains(haystack, keyword));
    }

    /// <summary>
    /// Niche-aware "is this news?" gate: memes always pass; everything else must avoid low-value markers and
    /// contain at least one news-signal marker from the profile. An empty news-signal list means "accept all".
    /// </summary>
    public static bool IsUsefulCandidateForSource(Source source, NicheProfile profile, string haystack)
    {
        if (SourceAllowsMeme(source))
        {
            return true;
        }

        if (ContainsAny(haystack, profile.Markers.LowValue))
        {
            return false;
        }

        return profile.Markers.NewsSignal.Count == 0 || ContainsAny(haystack, profile.Markers.NewsSignal);
    }

    public static bool SourceAllowsMeme(Source source) => SourceAllowsKindExplicit(source, PublicationKind.Meme);

    public static bool SourceAllowsKind(Source source, PublicationKind kind)
    {
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        return allowed.Count == 0 || allowed.Any(value => value.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool SourceAllowsKindExplicit(Source source, PublicationKind kind)
    {
        return SplitCsv(source.AllowedPublicationKindsCsv)
            .Any(value => value.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool SourceOnlyAllowsKind(Source source, PublicationKind kind)
    {
        var allowed = SplitCsv(source.AllowedPublicationKindsCsv);
        return allowed.Count == 1 && allowed[0].Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public static bool Contains(string text, string keyword) => text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    public static bool ContainsAny(string text, IEnumerable<string> markers) => markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static string BuildSummary(string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return title;
        }

        var normalized = Sanitize(text.ReplaceLineEndings(" "));
        var cleanTitle = Sanitize(title);
        return normalized.Length <= 500 ? $"{cleanTitle}\n{normalized}" : $"{cleanTitle}\n{normalized[..500]}...";
    }

    public static string Sanitize(string? value) => TextSanitizer.Clean(value);

    public static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withoutTags = Regex.Replace(html, "<.*?>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return Sanitize(Regex.Replace(decoded, "\\s+", " ").Trim());
    }

    public static string? NormalizeCitationContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var normalized = Regex.Replace(content.Replace("[...]", " "), "\\s+", " ").Trim();
        return normalized.Length <= 900 ? normalized : $"{normalized[..900]}...";
    }

    public static bool HasOldDateInUrl(string url, int maxAgeHours = 72)
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
            return date < DateTimeOffset.UtcNow.AddHours(-maxAgeHours);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // ---- JSON payload helpers (AI responses) ----

    public static string? ExtractJsonPayload(string text)
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

    public static JsonElement? GetItemsArray(JsonElement root)
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

    public static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public static int GetInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;
    }

    public static long GetLong(JsonElement element, string name)
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

    public static bool GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    }

    public static string? TryGetNestedString(JsonElement element, IReadOnlyList<string> path)
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

    // ---- XML feed helpers ----

    public static string? GetChildValue(XElement element, string localName)
    {
        var child = element.Elements().FirstOrDefault(item => item.Name.LocalName == localName);
        var value = child?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? GetFeedLink(XElement entry)
    {
        var atomLink = entry.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "link" && element.Attribute("href") is not null);

        if (atomLink is not null)
        {
            return atomLink.Attribute("href")?.Value.Trim();
        }

        return GetChildValue(entry, "link");
    }

    public static DateTimeOffset ParseFeedDate(XElement entry)
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

    public static string? ExtractFeedImageUrl(XElement entry, string? html)
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

    public static string? ExtractFeedVideoUrl(XElement entry, string? html)
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

    // ---- media URL helpers ----

    public static string? ExtractImageUrl(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = Regex.Match(html, "https?://[^\\s\"']+\\.(?:jpg|jpeg|png|webp)", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeImageUrl(match.Value) : null;
    }

    public static string? ExtractVideoUrl(string? html)
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

    public static bool LooksLikeImage(string? value)
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

    public static bool LooksLikeVideoUrl(string? value)
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

    public static string? NormalizeImageUrl(string? value)
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

    public static string? NormalizeVideoUrl(string? value)
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
}
