using System.Globalization;
using System.Text.Json;

namespace TgAutoposter.Infrastructure.Services;

internal static class PolzaResponseParser
{
    public static PolzaUsageSnapshot ExtractUsage(JsonElement root, string? fallbackModel = null)
    {
        JsonElement? usage = root.TryGetProperty("usage", out var usageElement)
            ? usageElement
            : null;

        var promptTokens = usage is null ? null : GetOptionalInt(usage.Value, "prompt_tokens") ?? GetOptionalInt(usage.Value, "input_tokens");
        var completionTokens = usage is null ? null : GetOptionalInt(usage.Value, "completion_tokens") ?? GetOptionalInt(usage.Value, "output_tokens");
        var totalTokens = usage is null ? null : GetOptionalInt(usage.Value, "total_tokens");
        var costRub = usage is null ? null : GetOptionalDecimal(usage.Value, "cost_rub") ?? GetOptionalDecimal(usage.Value, "cost");

        var metadata = JsonSerializer.Serialize(new
        {
            requestId = TryGetString(root, "id"),
            provider = TryGetString(root, "provider"),
            model = TryGetString(root, "model") ?? fallbackModel,
            status = TryGetString(root, "status"),
            usage = usage?.GetRawText(),
            annotations = TryGetAnnotationsRaw(root),
            error = TryGetErrorRaw(root)
        });

        return new PolzaUsageSnapshot(promptTokens, completionTokens, totalTokens, costRub, metadata);
    }

    public static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        return first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    public static string? ExtractProvider(JsonElement root)
    {
        return TryGetString(root, "provider") ?? "polza";
    }

    public static string ExtractModel(JsonElement root, string fallback)
    {
        return TryGetString(root, "model") ?? fallback;
    }

    public static string? ExtractMediaUrl(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            var dataUrl = FindUrl(data);
            if (!string.IsNullOrWhiteSpace(dataUrl))
            {
                return dataUrl;
            }
        }

        foreach (var name in new[] { "output", "result", "content", "url" })
        {
            if (root.TryGetProperty(name, out var property))
            {
                var url = FindUrl(property);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        return null;
    }

    public static IReadOnlyCollection<PolzaUrlCitation> ExtractUrlCitations(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return [];
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("annotations", out var annotations) ||
            annotations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PolzaUrlCitation>();
        foreach (var annotation in annotations.EnumerateArray())
        {
            if (!annotation.TryGetProperty("url_citation", out var citation) ||
                citation.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = TryGetString(citation, "url");
            var title = TryGetString(citation, "title");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            result.Add(new PolzaUrlCitation(
                url,
                title,
                TryGetString(citation, "content")));
        }

        return result;
    }

    private static string? FindUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            return IsUrl(value) ? value : null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var url = FindUrl(item);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var preferred in new[] { "url", "image_url", "output_url", "file_url", "public_url", "download_url", "b64_json" })
        {
            if (element.TryGetProperty(preferred, out var property))
            {
                var url = FindUrl(property);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var url = FindUrl(property.Value);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? TryGetAnnotationsRaw(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var first = choices[0];
        return first.TryGetProperty("message", out var message) &&
               message.TryGetProperty("annotations", out var annotations)
            ? annotations.GetRawText()
            : null;
    }

    private static string? TryGetErrorRaw(JsonElement root)
    {
        return root.TryGetProperty("error", out var error) ? error.GetRawText() : null;
    }

    private static int? GetOptionalInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
    }

    private static decimal? GetOptionalDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            ? value
            : null;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record PolzaUsageSnapshot(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    decimal? CostRub,
    string? MetadataJson);

internal sealed record PolzaUrlCitation(
    string Url,
    string Title,
    string? Content);
