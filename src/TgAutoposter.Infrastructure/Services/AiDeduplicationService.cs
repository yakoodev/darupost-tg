using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// Semantic deduplication (ТЗ §12). First runs the cheap heuristic; if it is confident (Duplicate /
/// Continuation) we trust it. Otherwise — when Polza is enabled and a recent post shares a strong entity
/// token with the candidate — we ask the model whether it's the same event, which catches the same news
/// reported by different outlets or in a different language (where token-overlap heuristics fail).
/// </summary>
public sealed class AiDeduplicationService(
    AppDbContext db,
    BasicDeduplicationService heuristic,
    IAiProvider aiProvider,
    IOptions<PolzaOptions> polzaOptions,
    ILogger<AiDeduplicationService> logger) : IDeduplicationService
{
    private const int LookbackDays = 4;
    private const int MaxCandidates = 20;

    public async Task<DeduplicationResult> CheckAsync(SourceCandidate candidate, CancellationToken cancellationToken)
    {
        var heuristicResult = await heuristic.CheckAsync(candidate, cancellationToken);
        if (heuristicResult.Status != DeduplicationStatus.Unique || !polzaOptions.Value.Enabled)
        {
            return heuristicResult;
        }

        try
        {
            var semantic = await CheckSemanticAsync(candidate, cancellationToken);
            return semantic ?? heuristicResult;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI deduplication failed, keeping heuristic verdict for channel {ChannelId}.", candidate.ChannelId);
            return heuristicResult;
        }
    }

    private async Task<DeduplicationResult?> CheckSemanticAsync(SourceCandidate candidate, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-LookbackDays);
        var recent = await db.Posts
            .AsNoTracking()
            .Where(post => post.ChannelId == candidate.ChannelId &&
                           post.CreatedAtUtc >= since &&
                           post.Status != PostStatus.Rejected &&
                           post.Status != PostStatus.Duplicate)
            .OrderByDescending(post => post.CreatedAtUtc)
            .Select(post => new { post.Id, post.SourceTitle, post.OriginalSummary })
            .Take(120)
            .ToListAsync(cancellationToken);

        var candidateTokens = StrongTokens($"{candidate.Title} {candidate.Summary}");
        if (candidateTokens.Count == 0)
        {
            return null;
        }

        // Prefilter: only consider posts that share a strong entity token (game/studio/platform names
        // survive translation), so we don't spend an AI call when nothing is even remotely related.
        var shortlist = recent
            .Where(post => StrongTokens(post.SourceTitle).Overlaps(candidateTokens))
            .Take(MaxCandidates)
            .ToList();

        if (shortlist.Count == 0)
        {
            return null;
        }

        var system = new StringBuilder()
            .AppendLine("Ты редактор игрового канала и проверяешь, не дублирует ли новый инфоповод уже опубликованные.")
            .AppendLine("Дубль — это про ТО ЖЕ событие/новость (даже если другой источник, другие слова или другой язык).")
            .AppendLine("Развитие темы (continuation) — новые детали по уже освещённому событию.")
            .AppendLine("Ответь СТРОГО одним JSON без markdown: {\"match\":<номер из списка или -1>,\"relation\":\"duplicate|continuation|unique\",\"reason\":\"кратко\"}")
            .ToString();

        var user = new StringBuilder();
        user.AppendLine("НОВЫЙ ИНФОПОВОД:");
        user.AppendLine($"Заголовок: {candidate.Title}");
        user.AppendLine($"Суть: {Truncate(candidate.Summary, 400)}");
        user.AppendLine();
        user.AppendLine("УЖЕ ОПУБЛИКОВАННЫЕ (последние дни):");
        for (var i = 0; i < shortlist.Count; i++)
        {
            user.AppendLine($"{i}. {shortlist[i].SourceTitle}");
        }

        var response = await aiProvider.CompleteAsync(
            new AiRequest(candidate.ChannelId, AiTaskType.Deduplication, system, user.ToString(), RequireJson: true),
            cancellationToken);

        return MapVerdict(response.Text, shortlist.Select(x => x.Id).ToList());
    }

    private static DeduplicationResult? MapVerdict(string? text, IReadOnlyList<Guid> ids)
    {
        var json = ExtractJsonObject(text);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var relation = root.TryGetProperty("relation", out var rel) && rel.ValueKind == JsonValueKind.String
            ? rel.GetString()?.Trim().ToLowerInvariant()
            : "unique";
        var reason = root.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String
            ? re.GetString()?.Trim()
            : null;

        var matchIndex = root.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var idx)
            ? idx
            : -1;
        Guid? matchedId = matchIndex >= 0 && matchIndex < ids.Count ? ids[matchIndex] : null;

        return relation switch
        {
            "duplicate" when matchedId is not null =>
                new DeduplicationResult(DeduplicationStatus.Duplicate, $"AI: тот же инфоповод. {reason}", matchedId),
            "continuation" when matchedId is not null =>
                new DeduplicationResult(DeduplicationStatus.Continuation, $"AI: развитие уже освещённой темы. {reason}", matchedId),
            _ => null,
        };
    }

    private static HashSet<string> StrongTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
        return new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "game", "games", "this", "that", "with", "from", "have", "will", "your", "play", "новый", "новая",
        "будет", "может", "игре", "игры", "игру", "теперь", "после", "часть", "вышел", "вышла",
    };

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
