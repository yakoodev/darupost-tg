using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>Polza/OpenAI-compatible embeddings (POST {BaseUrl}/embeddings).</summary>
public sealed class PolzaEmbeddingClient(
    HttpClient httpClient,
    IOptions<PolzaOptions> optionsAccessor,
    ILogger<PolzaEmbeddingClient> logger) : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<float[]?> EmbedAsync(Guid channelId, string text, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds));

        var payload = new
        {
            model = options.EmbeddingModel,
            input = text.Length > 8000 ? text[..8000] : text,
            user = channelId.ToString("N")
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/embeddings");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            request.Content = content;

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Polza embeddings failed: {Status} {Body}", (int)response.StatusCode, raw);
                return null;
            }

            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0 ||
                !data[0].TryGetProperty("embedding", out var embedding) ||
                embedding.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var vector = new float[embedding.GetArrayLength()];
            var i = 0;
            foreach (var element in embedding.EnumerateArray())
            {
                vector[i++] = element.GetSingle();
            }

            return vector;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Polza embeddings call failed for channel {ChannelId}.", channelId);
            return null;
        }
    }
}
