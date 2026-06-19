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
/// Semantic deduplication (ТЗ §12) via embeddings. Pipeline:
/// 1. cheap heuristic (URL / title similarity) — trusted when confident;
/// 2. embedding vector search: embed the candidate once and compare (cosine) against stored embeddings
///    of recent posts — catches the same event reported by different outlets / in another language;
/// 3. only for the ambiguous similarity band do we spend one chat call to confirm duplicate vs continuation.
/// So there is no expensive chat call per candidate — just a cheap embedding plus an in-memory compare.
/// </summary>
public sealed class AiDeduplicationService(
    AppDbContext db,
    BasicDeduplicationService heuristic,
    IEmbeddingProvider embeddingProvider,
    IAiProvider aiProvider,
    IOptions<PolzaOptions> polzaOptions,
    ILogger<AiDeduplicationService> logger) : IDeduplicationService
{
    private const int LookbackDays = 5;
    private const double DuplicateThreshold = 0.90;
    private const double GrayZoneThreshold = 0.82;

    public static string EmbeddingText(string title, string? summary) => $"{title}\n{summary}".Trim();

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
            logger.LogWarning(ex, "Semantic deduplication failed, keeping heuristic verdict for channel {ChannelId}.", candidate.ChannelId);
            return heuristicResult;
        }
    }

    private async Task<DeduplicationResult?> CheckSemanticAsync(SourceCandidate candidate, CancellationToken cancellationToken)
    {
        var candidateVector = await embeddingProvider.EmbedAsync(
            candidate.ChannelId, EmbeddingText(candidate.Title, candidate.Summary), cancellationToken);
        if (candidateVector is null)
        {
            return null;
        }

        var since = DateTimeOffset.UtcNow.AddDays(-LookbackDays);
        var recent = await db.Posts
            .AsNoTracking()
            .Where(post => post.ChannelId == candidate.ChannelId &&
                           post.CreatedAtUtc >= since &&
                           post.EmbeddingJson != null &&
                           post.Status != PostStatus.Rejected &&
                           post.Status != PostStatus.Duplicate)
            .OrderByDescending(post => post.CreatedAtUtc)
            .Select(post => new { post.Id, post.SourceTitle, post.EmbeddingJson })
            .Take(200)
            .ToListAsync(cancellationToken);

        var scored = recent
            .Select(post => new { post.Id, post.SourceTitle, Score = CosineSimilarity(candidateVector, Deserialize(post.EmbeddingJson)) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored[0];
        if (best.Score >= DuplicateThreshold)
        {
            return new DeduplicationResult(
                DeduplicationStatus.Duplicate,
                $"Семантический дубль (cosine={best.Score:0.00}): «{best.SourceTitle}».",
                best.Id);
        }

        if (best.Score >= GrayZoneThreshold)
        {
            // Ambiguous: confirm with a single chat call over the closest few posts only.
            var shortlist = scored.Take(6).ToList();
            return await ConfirmWithChatAsync(candidate, shortlist.Select(x => (x.Id, x.SourceTitle)).ToList(), best.Score, cancellationToken);
        }

        return null;
    }

    private async Task<DeduplicationResult?> ConfirmWithChatAsync(
        SourceCandidate candidate,
        IReadOnlyList<(Guid Id, string Title)> shortlist,
        double topScore,
        CancellationToken cancellationToken)
    {
        var system = new StringBuilder()
            .AppendLine("Ты редактор игрового канала. Решаешь, дублирует ли новый инфоповод уже опубликованные.")
            .AppendLine("duplicate — то же событие (даже другой источник/слова/язык). continuation — новые детали по уже освещённому событию. unique — другое.")
            .AppendLine("Ответь СТРОГО одним JSON без markdown: {\"match\":<номер или -1>,\"relation\":\"duplicate|continuation|unique\"}")
            .ToString();

        var user = new StringBuilder();
        user.AppendLine($"НОВЫЙ: {candidate.Title}");
        user.AppendLine($"Суть: {Truncate(candidate.Summary, 300)}");
        user.AppendLine();
        user.AppendLine("УЖЕ ОПУБЛИКОВАНО:");
        for (var i = 0; i < shortlist.Count; i++)
        {
            user.AppendLine($"{i}. {shortlist[i].Title}");
        }

        var response = await aiProvider.CompleteAsync(
            new AiRequest(candidate.ChannelId, AiTaskType.Deduplication, system, user.ToString(), RequireJson: true),
            cancellationToken);

        var json = ExtractJsonObject(response.Text);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var relation = root.TryGetProperty("relation", out var rel) && rel.ValueKind == JsonValueKind.String
            ? rel.GetString()?.Trim().ToLowerInvariant()
            : "unique";
        var matchIndex = root.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var idx) ? idx : -1;
        Guid? matchedId = matchIndex >= 0 && matchIndex < shortlist.Count ? shortlist[matchIndex].Id : shortlist[0].Id;

        return relation switch
        {
            "duplicate" => new DeduplicationResult(DeduplicationStatus.Duplicate, $"AI-дубль (cosine={topScore:0.00}).", matchedId),
            "continuation" => new DeduplicationResult(DeduplicationStatus.Continuation, $"AI: развитие темы (cosine={topScore:0.00}).", matchedId),
            _ => null,
        };
    }

    private static float[] Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<float[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= max ? value : value[..max];

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
