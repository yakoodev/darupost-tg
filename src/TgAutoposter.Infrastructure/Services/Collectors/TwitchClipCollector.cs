using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;
using static TgAutoposter.Infrastructure.Services.Collectors.CollectorHelpers;

namespace TgAutoposter.Infrastructure.Services.Collectors;

/// <summary>
/// Keyless Twitch clip analysis. Lists a channel's recent public clips via the public web GraphQL
/// client-id (no app / no OAuth / no 2FA), downloads the smallest clip file in pure HTTP, and has a
/// Polza multimodal model watch the video (with audio) to decide whether it is post-worthy and write
/// the post. Post-worthy clips are returned as ordinary candidates that flow through the scene (Deal)
/// lane → brand card → moderation. Spend is bounded by a per-channel per-run cap, a global daily cap,
/// a view floor and a per-channel "already analysed" memory kept in <see cref="Source.SettingsJson"/>.
/// </summary>
public sealed partial class TwitchClipCollector(
    HttpClient httpClient,
    IOptions<TwitchOptions> twitchAccessor,
    IOptions<PolzaOptions> polzaAccessor,
    AppDbContext db,
    IDateTimeProvider clock,
    ILogger<TwitchClipCollector> logger) : ISourceCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<SourceKind> SupportedKinds { get; } = [SourceKind.TwitchClip];

    public async Task<IReadOnlyCollection<CollectedCandidate>> CollectAsync(Source source, NicheProfile profile, CancellationToken cancellationToken)
    {
        var options = twitchAccessor.Value;
        if (!options.IsConfigured)
        {
            return [];
        }

        var polza = polzaAccessor.Value;
        if (!polza.Enabled || string.IsNullOrWhiteSpace(polza.ApiKey))
        {
            throw new InvalidOperationException("Twitch-клипы требуют Polza (анализ видео), но провайдер выключен или нет ключа.");
        }

        var login = ExtractLogin(source.Url);
        if (string.IsNullOrWhiteSpace(login))
        {
            return [];
        }

        var now = clock.UtcNow;

        // Global daily spend guard: how many clips were already analysed today (any channel).
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var analysedToday = await db.AiUsageRecords.CountAsync(
            r => r.CreatedAtUtc >= dayStart && r.Model.StartsWith("twitch-clip"),
            cancellationToken);
        var budget = options.DailyAnalysisCap - analysedToday;
        if (budget <= 0)
        {
            logger.LogInformation("Twitch clips: daily analysis cap {Cap} reached, skipping {Login}.", options.DailyAnalysisCap, login);
            return [];
        }

        List<TwitchClip> clips;
        try
        {
            clips = await ListClipsAsync(login, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Twitch GQL: не удалось получить клипы '{login}': {ex.Message}", ex);
        }

        var analysed = LoadAnalysed(source.SettingsJson, now, options.LookbackHours);
        var minViews = source.MinimumScore > 0 ? source.MinimumScore : options.MinViews;
        var freshLimit = now.AddHours(-options.LookbackHours);

        var picks = clips
            .Where(c => c.ViewCount >= minViews)
            .Where(c => c.CreatedAt >= freshLimit)
            .Where(c => !analysed.ContainsKey(c.Slug))
            .OrderByDescending(c => c.ViewCount)
            .Take(Math.Min(options.MaxClipsPerChannelPerRun, budget))
            .ToList();

        var results = new List<CollectedCandidate>();
        foreach (var clip in picks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Remember the slug regardless of outcome so we never re-download / re-pay for it.
            analysed[clip.Slug] = now;

            byte[]? video;
            try
            {
                var downloadUrl = await ResolveClipDownloadUrlAsync(clip.Slug, options, cancellationToken);
                if (downloadUrl is null)
                {
                    continue;
                }

                video = await DownloadAsync(downloadUrl, options.MaxClipMegabytes, cancellationToken);
                if (video is null || video.Length == 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Twitch clip {Slug}: download failed.", clip.Slug);
                continue;
            }

            ClipJudgement? verdict;
            try
            {
                verdict = await AnalyzeAsync(clip, login, video, options, polza, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Twitch clip {Slug}: analysis failed.", clip.Slug);
                continue;
            }

            if (verdict is null)
            {
                continue;
            }

            // Record the spend for accounting and the daily cap (posted or not).
            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = source.ChannelId,
                Provider = "polza",
                Model = $"twitch-clip:{login}",
                TaskType = AiTaskType.Classification,
                ProviderCostAmount = verdict.CostRub,
                ProviderCostCurrency = "RUB",
                CostAmount = verdict.CostRub,
                CostCurrency = "RUB",
                RequestMetadataJson = JsonSerializer.Serialize(new { slug = clip.Slug, clip.ViewCount, verdict.Kind, verdict.Score }),
                CreatedAtUtc = now
            });

            var kind = (verdict.Kind ?? "skip").Trim().ToLowerInvariant();
            var postWorthy = (kind == "news" || kind == "meme")
                && verdict.Safe
                && verdict.Score >= options.ScoreThreshold;
            if (!postWorthy)
            {
                logger.LogInformation("Twitch clip {Slug}: skipped (kind={Kind} score={Score}).", clip.Slug, kind, verdict.Score);
                continue;
            }

            var headline = string.IsNullOrWhiteSpace(verdict.Headline) ? clip.Title : verdict.Headline!;
            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(verdict.Text)) body.AppendLine(verdict.Text!.Trim());
            body.AppendLine($"Клип со стрима {clip.ChannelDisplayName ?? login} — {clip.ViewCount} просмотров.");
            if (!string.IsNullOrWhiteSpace(clip.CuratorName)) body.AppendLine($"Нарезал: {clip.CuratorName}.");

            var meta = JsonSerializer.Serialize(new
            {
                transport = "twitch-clip",
                kind,
                slug = clip.Slug,
                channel = login,
                clip.ViewCount,
                curator = clip.CuratorName,
                score = verdict.Score,
                hook = verdict.Hook,
                game = clip.GameName
            });

            results.Add(new CollectedCandidate(
                Sanitize(headline),
                clip.Url,
                BuildSummary(headline, body.ToString()),
                Sanitize(body.ToString()),
                clip.ThumbnailUrl,
                clip.ViewCount,
                null,
                // Highlight, not breaking news: stamp "now" so the 48h ingest-staleness filter keeps
                // week-old clips. The real clip date stays in metadata.
                now,
                meta,
                VideoUrl: clip.Url,
                ExternalId: clip.Slug,
                Author: clip.ChannelDisplayName ?? login));

            if (analysed.Count(kv => kv.Value >= dayStart) >= budget)
            {
                break;
            }
        }

        source.SettingsJson = SaveAnalysed(analysed);
        return results;
    }

    // ---- Twitch GQL (keyless, public web client-id) --------------------------------------------

    private async Task<List<TwitchClip>> ListClipsAsync(string login, TwitchOptions options, CancellationToken cancellationToken)
    {
        var query = $$"""
        query {
          user(login: "{{GqlEscape(login)}}") {
            displayName
            clips(first: {{options.EnumerateCount}}, criteria: {period: {{SanitizePeriod(options.Period)}}, sort: VIEWS_DESC}) {
              edges { node {
                slug title viewCount createdAt durationSeconds url
                thumbnailURL(width: 480, height: 272)
                curator { displayName }
                game { name }
              } }
            }
          }
        }
        """;

        var root = await GqlAsync(query, options, cancellationToken);
        var clips = new List<TwitchClip>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            return clips;
        }

        var channelName = GetString(user, "displayName");
        if (!user.TryGetProperty("clips", out var clipsNode) || !clipsNode.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
        {
            return clips;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out var n))
            {
                continue;
            }

            var slug = GetString(n, "slug");
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var created = n.TryGetProperty("createdAt", out var cr) && cr.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(cr.GetString(), out var dt)
                ? dt.ToUniversalTime()
                : clock.UtcNow;

            clips.Add(new TwitchClip(
                slug,
                GetString(n, "title"),
                GetInt(n, "viewCount"),
                created,
                GetInt(n, "durationSeconds"),
                GetString(n, "url"),
                GetString(n, "thumbnailURL"),
                n.TryGetProperty("curator", out var cu) && cu.ValueKind == JsonValueKind.Object ? GetString(cu, "displayName") : null,
                n.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object ? GetString(g, "name") : null,
                channelName));
        }

        return clips;
    }

    private async Task<string?> ResolveClipDownloadUrlAsync(string slug, TwitchOptions options, CancellationToken cancellationToken)
    {
        var query = $$"""
        query {
          clip(slug: "{{GqlEscape(slug)}}") {
            videoQualities { quality sourceURL }
            playbackAccessToken(params: {platform: "web", playerType: "clips-embed"}) { signature value }
          }
        }
        """;

        var root = await GqlAsync(query, options, cancellationToken);
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("clip", out var clip) || clip.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!clip.TryGetProperty("videoQualities", out var quals) || quals.ValueKind != JsonValueKind.Array || quals.GetArrayLength() == 0)
        {
            return null;
        }

        // Smallest quality = cheapest to download and to feed the model.
        string? bestUrl = null;
        var bestQ = int.MaxValue;
        foreach (var q in quals.EnumerateArray())
        {
            var url = GetString(q, "sourceURL");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var qNum = int.TryParse(new string(GetString(q, "quality").Where(char.IsDigit).ToArray()), out var parsed) ? parsed : 9999;
            if (qNum < bestQ)
            {
                bestQ = qNum;
                bestUrl = url;
            }
        }

        if (bestUrl is null || !clip.TryGetProperty("playbackAccessToken", out var tok) || tok.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var sig = GetString(tok, "signature");
        var value = GetString(tok, "value");
        if (string.IsNullOrWhiteSpace(sig) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return $"{bestUrl}?sig={sig}&token={Uri.EscapeDataString(value)}";
    }

    private async Task<JsonElement> GqlAsync(string query, TwitchOptions options, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.GqlUrl);
        request.Headers.TryAddWithoutValidation("Client-ID", options.PublicClientId);
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException($"GQL errors: {errors.GetRawText()}");
        }

        return doc.RootElement.Clone();
    }

    // ---- Download + Polza video analysis -------------------------------------------------------

    private async Task<byte[]?> DownloadAsync(string url, int maxMegabytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; tg-autoposter/0.3)");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var maxBytes = (long)maxMegabytes * 1024 * 1024;
        if (response.Content.Headers.ContentLength is long len && len > maxBytes)
        {
            logger.LogInformation("Twitch clip too large ({Len} bytes), skipping.", len);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length > maxBytes ? null : bytes;
    }

    private async Task<ClipJudgement?> AnalyzeAsync(TwitchClip clip, string login, byte[] video, TwitchOptions options, PolzaOptions polza, CancellationToken cancellationToken)
    {
        var prompt =
            "Ты редактор канала о русских витуберах. Канал постит ТОЛЬКО два типа контента: НОВОСТИ и МЕМЫ. " +
            "Посмотри этот твич-клип (видео со звуком) и реши, тянет ли он на пост.\n" +
            $"Канал витубера: {login}. Заголовок клипа (его дал автор нарезки): «{clip.Title}». Просмотров: {clip.ViewCount}.\n\n" +
            "НОВОСТЬ = в клипе реально произошло что-то заметное: драма, конфликт, признание/каминг-аут, милстоун, " +
            "неожиданное событие, горячее мнение (хот-тейк), инсайт про сцену или витубера — то, что интересно обсудить.\n" +
            "МЕМ = реально смешно/абсурдно/цитируемо; момент, который хочется репостнуть и заскринить.\n" +
            "SKIP = рядовой геймплей, спокойная болтовня ни о чём, «просто стример что-то делает», ничего не происходит. " +
            "Если сомневаешься — SKIP. Лучше пропустить, чем запостить скукоту. Не выдумывай инфоповод, которого в клипе нет.\n\n" +
            "Без кринжа, без первого лица, без NSFW/оскорблений. " +
            "Верни СТРОГО JSON: {\"kind\":\"news\"|\"meme\"|\"skip\"," +
            "\"score\":0-10 (насколько сильный инфоповод или реальный прикол; рядовое = 0-4)," +
            "\"safe\":true|false," +
            "\"hook\":\"одной фразой суть новости или соль мема\"," +
            "\"headline\":\"цепляющий заголовок, подающий хук; без кринжа, без КАПСА, без эмодзи\"," +
            "\"text\":\"1-2 предложения. Для новости — что произошло и почему это важно/интересно. Для мема — подать сам прикол, НЕ пересказывать по секундам\"}";

        var b64 = Convert.ToBase64String(video);
        var payload = new
        {
            model = options.AnalysisModel,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "video_url", video_url = new { url = "data:video/mp4;base64," + b64 } }
                    }
                }
            },
            temperature = 0.4,
            max_tokens = 700,
            response_format = new { type = "json_object" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{polza.BaseUrl.TrimEnd('/')}/{polza.ChatCompletionPath.TrimStart('/')}");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + polza.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, options.TimeoutSeconds)));

        using var response = await httpClient.SendAsync(request, cts.Token);
        var raw = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Polza video analysis {(int)response.StatusCode}: {Truncate(raw, 300)}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var content = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
                ? c.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var costRub = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("cost_rub", out var cr) && cr.ValueKind == JsonValueKind.Number
            ? cr.GetDecimal()
            : (decimal?)null;

        var jsonPayload = ExtractJsonPayload(content) ?? content;
        using var judged = JsonDocument.Parse(jsonPayload);
        var j = judged.RootElement;
        return new ClipJudgement(
            GetString(j, "kind"),
            GetInt(j, "score"),
            !j.TryGetProperty("safe", out var s) || s.ValueKind != JsonValueKind.False,
            j.TryGetProperty("hook", out _) ? GetString(j, "hook") : null,
            j.TryGetProperty("headline", out _) ? GetString(j, "headline") : null,
            j.TryGetProperty("text", out _) ? GetString(j, "text") : null,
            costRub);
    }

    // ---- Analysed-slug memory (per channel, in Source.SettingsJson) -----------------------------

    private static Dictionary<string, DateTimeOffset> LoadAnalysed(string? settingsJson, DateTimeOffset now, int lookbackHours)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("analysed", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var cutoff = now.AddHours(-Math.Max(lookbackHours * 2, 48));
                foreach (var item in arr.EnumerateArray())
                {
                    var slug = GetString(item, "slug");
                    if (string.IsNullOrWhiteSpace(slug))
                    {
                        continue;
                    }

                    var at = item.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(a.GetString(), out var dt)
                        ? dt
                        : now;
                    if (at >= cutoff)
                    {
                        result[slug] = at;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Corrupt / legacy settings: start fresh.
        }

        return result;
    }

    private static string SaveAnalysed(Dictionary<string, DateTimeOffset> analysed)
    {
        var items = analysed
            .OrderByDescending(kv => kv.Value)
            .Take(200)
            .Select(kv => new { slug = kv.Key, at = kv.Value });
        return JsonSerializer.Serialize(new { analysed = items });
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string ExtractLogin(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        var m = Regex.Match(trimmed, @"twitch\.tv/(?<login>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups["login"].Value.ToLowerInvariant();
        }

        return trimmed.TrimStart('@').ToLowerInvariant();
    }

    private static string GqlEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string SanitizePeriod(string? period)
    {
        var p = (period ?? "LAST_WEEK").Trim().ToUpperInvariant();
        return p is "LAST_DAY" or "LAST_WEEK" or "LAST_MONTH" or "ALL_TIME" ? p : "LAST_WEEK";
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    private sealed record TwitchClip(
        string Slug,
        string Title,
        int ViewCount,
        DateTimeOffset CreatedAt,
        int DurationSeconds,
        string Url,
        string? ThumbnailUrl,
        string? CuratorName,
        string? GameName,
        string? ChannelDisplayName);

    private sealed record ClipJudgement(
        string Kind,
        int Score,
        bool Safe,
        string? Hook,
        string? Headline,
        string? Text,
        decimal? CostRub);
}
