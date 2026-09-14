using System.Text.Json;
using System.Xml.Linq;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>RSS 2.0 / Atom feeds (and "Web" sources that expose a feed URL).</summary>
public sealed class FeedCollector(HttpClient httpClient, VideoEnricher videoEnricher) : ISourceCollector
{
    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.Rss, SourceKind.Web];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            return [];
        }

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

        var position = 0;
        var rankByPosition = SourceAllowsMeme(source);
        foreach (var entry in entries)
        {
            position++;
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
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, profile, haystack))
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

            var author = GetChildValue(entry, "creator") ?? GetChildValue(entry, "author");
            var externalId = GetChildValue(entry, "guid") ?? GetChildValue(entry, "id") ?? link;

            result.Add(new CollectedCandidate(
                title,
                link,
                BuildSummary(title, text),
                text,
                imageUrl,
                // Meme feeds are requested pre-sorted (top of the week): keep that order as the score.
                rankByPosition ? Math.Max(1, 124 - position * 4) : null,
                null,
                publishedAt,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    sourceUrl = source.Url,
                    author,
                    videoUrl,
                    transport = "feed"
                }),
                VideoUrl: videoUrl,
                ExternalId: externalId,
                Author: author));
        }

        return await videoEnricher.EnrichAsync(source, profile, result, cancellationToken);
    }
}
