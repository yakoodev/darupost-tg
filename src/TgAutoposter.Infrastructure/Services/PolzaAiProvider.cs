using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed class PolzaAiProvider(HttpClient httpClient, IOptions<PolzaOptions> optionsAccessor) : IAiProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.DefaultModel : request.Model;

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new AiResponse(
                BuildLocalDraft(request),
                "local-fallback",
                model ?? "local",
                UsageMetadataJson: JsonSerializer.Serialize(new { reason = "Polza provider is disabled or API key is missing." }),
                RawResponse: "Polza provider is disabled or API key is missing.");
        }

        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds));

        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            temperature = options.Temperature,
            max_tokens = options.MaxTokens,
            response_format = request.RequireJson ? new { type = "json_object" } : null,
            user = request.ChannelId.ToString("N")
        };

        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}/{options.ChatCompletionPath.TrimStart('/')}");
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        requestMessage.Content = content;

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Polza chat completion failed: {(int)response.StatusCode} {raw}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var text = PolzaResponseParser.ExtractText(root) ?? string.Empty;
        var usage = PolzaResponseParser.ExtractUsage(root, model);

        return new AiResponse(
            text,
            PolzaResponseParser.ExtractProvider(root) ?? "polza",
            PolzaResponseParser.ExtractModel(root, model ?? options.DefaultModel),
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            usage.CostRub,
            "RUB",
            usage.MetadataJson,
            RawResponse: raw);
    }

    private static string BuildLocalDraft(AiRequest request)
    {
        var body = request.UserPrompt.Trim();
        return body.Length <= 900 ? body : body[..900];
    }

}
