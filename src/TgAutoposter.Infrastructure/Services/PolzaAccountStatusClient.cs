using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed class PolzaAccountStatusClient(HttpClient httpClient, IOptions<PolzaOptions> optionsAccessor) : IAiAccountStatusClient
{
    public async Task<AiAccountStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        var hasApiKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        if (!options.Enabled || !hasApiKey)
        {
            return new AiAccountStatus(
                options.Enabled,
                hasApiKey,
                options.BaseUrl,
                options.DefaultModel,
                options.ImageModel,
                null,
                hasApiKey ? null : "API key не задан.");
        }

        try
        {
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BuildApiRoot(options)}/api/v1/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Create(options, hasApiKey, null, $"Balance failed: {(int)response.StatusCode} {raw}");
            }

            using var document = JsonDocument.Parse(raw);
            var amount = document.RootElement.TryGetProperty("amount", out var amountElement)
                ? ParseAmount(amountElement)
                : null;

            return Create(options, hasApiKey, amount, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Create(options, hasApiKey, null, ex.Message);
        }
    }

    private static AiAccountStatus Create(PolzaOptions options, bool hasApiKey, decimal? balance, string? error)
    {
        return new AiAccountStatus(
            options.Enabled,
            hasApiKey,
            options.BaseUrl,
            options.DefaultModel,
            options.ImageModel,
            balance,
            error);
    }

    private static decimal? ParseAmount(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String &&
               decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static string BuildApiRoot(PolzaOptions options)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl[..^"/api/v1".Length]
            : baseUrl;
    }
}
