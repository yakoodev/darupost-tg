using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>Polza.ai chat completion with the "web" plugin: the model researches fresh items and returns JSON.</summary>
public sealed class AiWebSearchCollector(
    HttpClient httpClient,
    IOptions<PolzaOptions> optionsAccessor,
    VideoEnricher videoEnricher) : ISourceCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string ItemJsonSchema = """
        {
          "items": [
            {
            "title": "краткий английский заголовок",
            "url": "https://...",
            "summary": "2-3 предложения с фактами и контекстом",
            "publishedAt": "ISO-8601 если известна дата, иначе null",
            "imageUrl": null,
            "videoUrl": "https://www.youtube.com/watch?v=... или null"
            }
          ]
        }
        """;

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.AiWebSearch];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return [];
        }

        var brand = string.IsNullOrWhiteSpace(source.Channel?.Name) ? profile.BrandFallbackName : source.Channel!.Name;
        var searchPrompt = string.IsNullOrWhiteSpace(source.Url) ? profile.WebSearch.DefaultQuery : source.Url.Trim();
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var template = SourceOnlyAllowsKind(source, PublicationKind.Trailer) && !string.IsNullOrWhiteSpace(profile.WebSearch.VideoPrompt)
            ? profile.WebSearch.VideoPrompt
            : profile.WebSearch.NewsPrompt;

        var userPrompt = template
            .Replace("{today}", today, StringComparison.Ordinal)
            .Replace("{brand}", brand, StringComparison.Ordinal)
            .Replace("{itemJson}", ItemJsonSchema, StringComparison.Ordinal);

        // Each source narrows the search with its own query; templates without a {query} slot get it appended.
        userPrompt = userPrompt.Contains("{query}", StringComparison.Ordinal)
            ? userPrompt.Replace("{query}", searchPrompt, StringComparison.Ordinal)
            : $"{userPrompt}{Environment.NewLine}{Environment.NewLine}Фокус поиска (главный приоритет): {searchPrompt}";

        var payload = new
        {
            model = options.DefaultModel,
            messages = new[]
            {
                new { role = "system", content = profile.WebSearch.ResearcherPersona },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.2,
            max_tokens = 3000,
            plugins = new[]
            {
                new
                {
                    id = "web",
                    engine = "exa",
                    max_results = 5,
                    search_prompt = searchPrompt
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/{options.ChatCompletionPath.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Polza web search failed: {(int)response.StatusCode} {raw}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var usage = PolzaResponseParser.ExtractUsage(root, options.DefaultModel);
        var text = PolzaResponseParser.ExtractText(root) ?? string.Empty;
        var citations = PolzaResponseParser.ExtractUrlCitations(root);
        var items = ParseItems(source, profile, text, raw, usage, citations);
        return await videoEnricher.EnrichAsync(source, profile, items, cancellationToken);
    }

    private static IReadOnlyCollection<CollectedCandidate> ParseItems(
        Source source,
        NicheProfile profile,
        string text,
        string raw,
        PolzaUsageSnapshot usage,
        IReadOnlyCollection<PolzaUrlCitation> citations)
    {
        var json = ExtractJsonPayload(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return ParseCitations(source, profile, raw, usage, citations);
        }

        using var document = JsonDocument.Parse(json);
        var items = GetItemsArray(document.RootElement);
        if (items is null)
        {
            return ParseCitations(source, profile, raw, usage, citations);
        }

        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        foreach (var item in items.Value.EnumerateArray().Take(10))
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = GetString(item, "title");
            var summary = GetString(item, "summary");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            var haystack = $"{title}\n{summary}";
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, profile, haystack))
            {
                continue;
            }

            // Undated items look fresh and turn into stale "news": require a publish date.
            if (!DateTimeOffset.TryParse(GetString(item, "publishedAt"), out var foundAt))
            {
                continue;
            }

            var url = GetString(item, "url");
            result.Add(new CollectedCandidate(
                Sanitize(title),
                url,
                Sanitize(summary),
                Sanitize(summary),
                GetString(item, "imageUrl"),
                null,
                null,
                foundAt,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    transport = "polza-web",
                    videoUrl = GetString(item, "videoUrl"),
                    polza = usage.MetadataJson,
                    raw
                }),
                usage.CostRub,
                "RUB",
                usage.MetadataJson,
                NormalizeVideoUrl(GetString(item, "videoUrl")),
                ExternalId: url));
        }

        return result.Count > 0 ? result : ParseCitations(source, profile, raw, usage, citations);
    }

    private static IReadOnlyCollection<CollectedCandidate> ParseCitations(
        Source source,
        NicheProfile profile,
        string raw,
        PolzaUsageSnapshot usage,
        IReadOnlyCollection<PolzaUrlCitation> citations)
    {
        var filters = CreateFilters(source);
        var result = new List<CollectedCandidate>();
        foreach (var citation in citations.Take(10))
        {
            if (string.IsNullOrWhiteSpace(citation.Title) || string.IsNullOrWhiteSpace(citation.Url))
            {
                continue;
            }

            var content = NormalizeCitationContent(citation.Content);
            var haystack = $"{citation.Title}\n{content}";
            if (!PassesTextFilters(filters, haystack) || !IsUsefulCandidateForSource(source, profile, haystack) || HasOldDateInUrl(citation.Url))
            {
                continue;
            }

            var summary = string.IsNullOrWhiteSpace(content)
                ? citation.Title.Trim()
                : BuildSummary(citation.Title.Trim(), content);

            result.Add(new CollectedCandidate(
                citation.Title.Trim(),
                citation.Url.Trim(),
                summary,
                content,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                JsonSerializer.Serialize(new
                {
                    source = source.Name,
                    transport = "polza-web-annotation",
                    polza = usage.MetadataJson,
                    raw
                }),
                usage.CostRub,
                "RUB",
                usage.MetadataJson,
                ExternalId: citation.Url.Trim()));
        }

        return result;
    }
}
