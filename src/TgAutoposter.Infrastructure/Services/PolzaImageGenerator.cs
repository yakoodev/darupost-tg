using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public sealed class PolzaImageGenerator(HttpClient httpClient, IOptions<PolzaOptions> optionsAccessor) : IImageGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ImageGenerationResult> GenerateForPostAsync(
        Channel channel,
        Post post,
        CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        var model = options.ImageModel;
        var prompt = BuildPrompt(channel, post);
        var referenceImages = ResolveReferenceImages(post);

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new ImageGenerationResult(
                null,
                prompt,
                "local-fallback",
                model,
                UsageMetadataJson: JsonSerializer.Serialize(new { reason = "Polza provider is disabled or API key is missing." }),
                RawResponse: "Polza provider is disabled or API key is missing.");
        }

        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(120, options.TimeoutSeconds));

        var aspectRatios = options.ImageAspectRatio.Equals("4:5", StringComparison.OrdinalIgnoreCase)
            ? new[] { options.ImageAspectRatio, "3:4" }
            : new[] { options.ImageAspectRatio };

        HttpRequestException? aspectError = null;
        foreach (var aspectRatio in aspectRatios)
        {
            try
            {
                return await GenerateViaMediaApiAsync(options, channel.Id, model, prompt, aspectRatio, referenceImages, cancellationToken);
            }
            catch (HttpRequestException ex) when (aspectRatios.Length > 1 && ex.Message.Contains("aspect_ratio", StringComparison.OrdinalIgnoreCase))
            {
                aspectError = ex;
            }
        }

        if (aspectError is not null)
        {
            throw aspectError;
        }

        throw new InvalidOperationException("Polza image generation did not return a result.");
    }

    private async Task<ImageGenerationResult> GenerateViaMediaApiAsync(
        PolzaOptions options,
        Guid channelId,
        string model,
        string prompt,
        string aspectRatio,
        IReadOnlyCollection<string> referenceImages,
        CancellationToken cancellationToken)
    {
        var input = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["aspect_ratio"] = aspectRatio,
            ["image_resolution"] = options.ImageResolution,
            ["n"] = 1,
            ["max_images"] = 1,
            ["quality"] = "medium",
            ["output_format"] = "png"
        };

        if (referenceImages.Count > 0)
        {
            input["images"] = referenceImages;
        }

        var payload = new
        {
            model,
            input,
            @async = true,
            user = channelId.ToString("N")
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BuildApiRoot(options)}/api/v1/media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        request.Content = content;

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Polza media generation failed: {(int)response.StatusCode} {raw}");
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var directResult = TryParseCompleted(root, prompt, model, raw);
        if (directResult is not null)
        {
            return directResult;
        }

        var requestId = TryGetString(root, "requestId") ?? TryGetString(root, "id");
        if (string.IsNullOrWhiteSpace(requestId))
        {
            var usage = PolzaResponseParser.ExtractUsage(root, model);
            return new ImageGenerationResult(null, prompt, "polza", model, usage.CostRub, "RUB", usage.MetadataJson, raw);
        }

        return await PollAsync(options, requestId, prompt, model, raw, cancellationToken);
    }

    private static string BuildPrompt(Channel channel, Post post)
    {
        var text = post.FinalText ?? post.GeneratedText ?? post.OriginalSummary;
        text = text.ReplaceLineEndings(" ").Trim();
        if (text.Length > 900)
        {
            text = $"{text[..900]}...";
        }

        var rubric = ResolveRubric(post);
        var mainThesis = ExtractMainThesis(post, text);
        var visualSubject = ExtractVisualSubject(post, text);
        var brandName = string.IsNullOrWhiteSpace(channel.Name) ? "Только игры" : channel.Name.Trim();

        if (post.PublicationKind == PublicationKind.Meme)
        {
            var sourceImage = ResolveReferenceImages(post).FirstOrDefault() ?? post.ImagePath;
            return $"""
            Локализуй исходный игровой мем для русскоязычного Telegram-канала "{brandName}".

            Это не новостная карточка и не постер. Используй исходное изображение как главный референс:
            {sourceImage}

            Задача:
            - сохранить узнаваемую композицию, кадрирование и мемный формат исходной картинки;
            - перевести весь видимый английский текст на естественный русский;
            - если дословный перевод слабый, адаптировать шутку под русскоязычных игроков;
            - оставить юмор коротким, разговорным и понятным без поясняющих подписей;
            - не добавлять логотипы, водяные знаки, бренд канала, новостные рамки, UI-панели и инфографику;
            - не превращать мем в AI-art, 3D-render, фэнтези-постер или рекламный баннер;
            - текст должен быть крупным, читаемым и без ошибок.

            Контекст для адаптации:
            Заголовок Reddit: {post.SourceTitle}
            Ссылка: {post.SourceUrl}
            Текст поста: {text}
            """;
        }

        return $"""
        Создай вертикальную новостную картинку 4:5 для Telegram-канала об играх по одному фиксированному шаблону.

        Стиль: тёмная editorial-карточка игрового медиа, Figma-style, минимализм. Не AI-art, не 3D-render, не фэнтези-постер, не инфографика.
        Важно: все картинки канала должны выглядеть 1 в 1 по сетке, отступам, расположению текста, рамке, синим линиям и размеру брендовой подписи. Меняется только тезис, рубрика и визуальный объект справа.

        Фиксированная композиция:
        - холст 4:5;
        - внешний отступ 56 px;
        - тонкая рамка 1 px по периметру;
        - сверху слева маленькая рубрика: {rubric};
        - слева по центру крупный основной тезис, максимум 2-3 строки;
        - справа один визуальный объект: {visualSubject};
        - снизу слева только бренд: {brandName};
        - лёгкие UI-линии и один синий акцент.

        Цвета:
        чёрный/графитовый фон, белый текст, синий акцент.

        Текст:
        {rubric}

        {mainThesis}

        {brandName}

        Требования:
        крупный читаемый текст, без ошибок, минимум элементов, без лишних фраз, без официальных логотипов, без водяных знаков, без мелкой инфографики, без домена, без имени источника, без автора и без строки source.

        Контекст новости:
        Заголовок источника: {post.SourceTitle}
        URL: {post.SourceUrl}
        Текст поста: {text}
        """;
    }

    private static string ResolveRubric(Post post)
    {
        return post.PublicationKind switch
        {
            PublicationKind.BreakingNews => "СРОЧНО",
            PublicationKind.Rumor => "СЛУХ",
            PublicationKind.Digest => "ДАЙДЖЕСТ",
            PublicationKind.Deal => "РАЗДАЧА",
            PublicationKind.Trailer => "ТРЕЙЛЕР",
            PublicationKind.Meme => "МЕМ",
            _ => "НОВОСТИ"
        };
    }

    private static string ExtractMainThesis(Post post, string text)
    {
        var candidates = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim(' ', '-', '—', '*'))
            .Where(line => line.Length > 12)
            .ToList();

        var thesis = candidates.FirstOrDefault() ?? post.SourceTitle;
        thesis = RemoveMarkdown(thesis);

        return ClampWords(thesis, 9, 68);
    }

    private static string ExtractVisualSubject(Post post, string text)
    {
        var subject = string.IsNullOrWhiteSpace(post.SourceTitle)
            ? text
            : post.SourceTitle;

        return $"""
        предмет новости «{ClampWords(RemoveMarkdown(subject), 12, 120)}» в виде безопасной метафоры без официальных логотипов и без узнаваемых скриншотов; если упомянута компания, игра, сервис или событие, показать не логотип, а контекстный объект: силуэт персонажа без сходства, игровое окно без UI-брендинга, витрину магазина, серверную стойку, студийный стол, календарь события или экран обновления.
        """.ReplaceLineEndings(" ").Trim();
    }

    private static string RemoveMarkdown(string value)
    {
        return value
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string ClampWords(string value, int maxWords, int maxChars)
    {
        var compact = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length > maxChars)
        {
            compact = $"{compact[..Math.Max(0, maxChars - 1)].TrimEnd()}…";
        }

        var words = compact.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length <= maxWords
            ? compact
            : $"{string.Join(' ', words.Take(maxWords))}…";
    }

    private async Task<ImageGenerationResult> PollAsync(
        PolzaOptions options,
        string requestId,
        string prompt,
        string model,
        string initialRaw,
        CancellationToken cancellationToken)
    {
        var waitLimit = TimeSpan.FromSeconds(Math.Max(180, options.TimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow.Add(waitLimit);
        var mediaUrl = $"{BuildApiRoot(options)}/api/v1/media/{Uri.EscapeDataString(requestId)}";
        var raw = initialRaw;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Polza media status failed: {(int)response.StatusCode} {raw}");
            }

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var status = TryGetString(root, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var completed = TryParseCompleted(root, prompt, model, raw);
                if (completed is not null)
                {
                    return completed;
                }
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Polza image generation failed: {raw}");
            }
        }

        throw new TimeoutException($"Polza image generation timed out for request {requestId}. Last response: {raw}");
    }

    private static ImageGenerationResult? TryParseCompleted(JsonElement root, string prompt, string model, string raw)
    {
        if (root.TryGetProperty("status", out var status) &&
            status.ValueKind == JsonValueKind.String &&
            !status.GetString()!.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var imageUrl = PolzaResponseParser.ExtractMediaUrl(root);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var usage = PolzaResponseParser.ExtractUsage(root, model);
        return new ImageGenerationResult(
            imageUrl,
            prompt,
            PolzaResponseParser.ExtractProvider(root) ?? "polza",
            PolzaResponseParser.ExtractModel(root, model),
            usage.CostRub,
            "RUB",
            usage.MetadataJson,
            raw);
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyCollection<string> ResolveReferenceImages(Post post)
    {
        if (post.PublicationKind != PublicationKind.Meme)
        {
            return [];
        }

        var sourceImage = NormalizeReferenceImageUrl(post.SourceCandidate?.ImageUrl) ??
                          NormalizeReferenceImageUrl(post.ImagePath);

        var urls = DeserializeMediaUrls(post.SourceCandidate?.MediaUrlsJson)
            .Concat(DeserializeMediaUrls(post.MediaUrlsJson))
            .Append(sourceImage)
            .Select(NormalizeReferenceImageUrl)
            .OfType<string>()
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return urls.Length == 0 ? [] : urls;
    }

    private static IReadOnlyCollection<string> DeserializeMediaUrls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NormalizeReferenceImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.Equals("preview.redd.it", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Host = "i.redd.it",
                Query = string.Empty
            };

            uri = builder.Uri;
        }

        var path = uri.AbsolutePath;
        var isImage = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

        return isImage ? uri.ToString() : null;
    }

    private static string BuildApiRoot(PolzaOptions options)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl[..^"/api/v1".Length]
            : baseUrl;
    }
}
