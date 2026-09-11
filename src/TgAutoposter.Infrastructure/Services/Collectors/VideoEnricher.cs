using System.Text.RegularExpressions;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>
/// For sources that may produce video posts: scrapes the candidate page for an og:video / YouTube URL and,
/// for video-only sources, drops candidates whose video is older than 72h.
/// </summary>
public sealed class VideoEnricher(HttpClient httpClient)
{
    public async Task<IReadOnlyCollection<CollectedCandidate>> EnrichAsync(
        Source source,
        NicheProfile profile,
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
            if (!string.IsNullOrWhiteSpace(candidate.VideoUrl) || !LooksLikeVideoCandidate(profile, candidate))
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

    private static bool LooksLikeVideoCandidate(NicheProfile profile, CollectedCandidate candidate)
    {
        return profile.Markers.VideoSignal.Count > 0 &&
               ContainsAny($"{candidate.Title}\n{candidate.Summary}\n{candidate.RawText}", profile.Markers.VideoSignal);
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
}
