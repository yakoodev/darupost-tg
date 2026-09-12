using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed record WspanelPage(string Url, string? FinalUrl, string? Title, string? Text, string? Html, string? Error);

/// <summary>Thin client for wspanel's page reader (<c>POST /api/v1/read</c>).</summary>
public sealed class WspanelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WspanelOptions _options;
    private readonly HttpClient _httpClient;

    public WspanelClient(IOptions<WspanelOptions> optionsAccessor)
    {
        _options = optionsAccessor.Value;
        var handler = new HttpClientHandler();
        if (_options.InsecureTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(30, _options.TimeoutSeconds))
        };
    }

    public bool IsConfigured =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        !string.IsNullOrWhiteSpace(_options.Profile);

    public bool UseForTelegram => IsConfigured && _options.UseForTelegram;

    /// <summary>Reads up to five URLs inside the logged-in browser of the given (or default) desktop.</summary>
    public async Task<IReadOnlyList<WspanelPage>> ReadAsync(
        IReadOnlyList<string> urls,
        bool includeHtml,
        string? profileOverride,
        CancellationToken cancellationToken,
        int? waitMs = null)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("wspanel не настроен (Wspanel:Enabled/BaseUrl/ApiKey/Profile).");
        }

        if (urls.Count == 0)
        {
            return [];
        }

        var payload = new ReadRequest(
            string.IsNullOrWhiteSpace(profileOverride) ? _options.Profile! : profileOverride,
            urls.Take(5).ToList(),
            string.IsNullOrWhiteSpace(_options.Program) ? null : _options.Program,
            includeHtml,
            Math.Max(0, waitMs ?? _options.WaitMs),
            _options.Force);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/api/v1/read");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        // The panel does not accept chunked request bodies (JsonContent streams → Transfer-Encoding: chunked → empty body → bad_id).
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), System.Text.Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
                error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            }
            catch (JsonException)
            {
                // not JSON — keep raw body
            }

            throw new HttpRequestException($"wspanel read failed: {(int)response.StatusCode} {code ?? string.Empty} {error ?? body}".Trim());
        }

        var result = JsonSerializer.Deserialize<ReadResponse>(body, JsonOptions);
        return result?.Pages?
            .Select(page => new WspanelPage(page.Url ?? string.Empty, page.FinalUrl, page.Title, page.Text, page.Html, page.Error))
            .ToList() ?? [];
    }

    private sealed record ReadRequest(
        [property: JsonPropertyName("profile")] string Profile,
        [property: JsonPropertyName("urls")] List<string> Urls,
        [property: JsonPropertyName("program")] string? Program,
        [property: JsonPropertyName("html")] bool Html,
        [property: JsonPropertyName("wait")] int Wait,
        [property: JsonPropertyName("force")] bool Force);

    private sealed class ReadResponse
    {
        [JsonPropertyName("session")] public string? Session { get; set; }
        [JsonPropertyName("program")] public string? Program { get; set; }
        [JsonPropertyName("attached")] public bool Attached { get; set; }
        [JsonPropertyName("pages")] public List<ReadPage>? Pages { get; set; }
    }

    private sealed class ReadPage
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("final_url")] public string? FinalUrl { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("html")] public string? Html { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
