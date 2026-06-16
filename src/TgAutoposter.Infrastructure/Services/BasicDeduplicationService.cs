using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed class BasicDeduplicationService(AppDbContext db) : IDeduplicationService
{
    public async Task<DeduplicationResult> CheckAsync(SourceCandidate candidate, CancellationToken cancellationToken)
    {
        var normalizedTitle = Normalize(candidate.Title);
        var normalizedTopic = NormalizeTopic(candidate.Title);
        var normalizedUrl = NormalizeUrl(candidate.CanonicalUrl ?? candidate.Url);
        var normalizedVideoUrl = NormalizeUrl(candidate.VideoUrl);

        var recentPosts = await db.Posts
            .Where(post => post.ChannelId == candidate.ChannelId)
            .OrderByDescending(post => post.CreatedAtUtc)
            .Take(250)
            .Select(post => new
            {
                post.Id,
                post.SourceTitle,
                post.SourceUrl,
                post.VideoUrl,
                post.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        foreach (var post in recentPosts)
        {
            if (!string.IsNullOrWhiteSpace(normalizedUrl) &&
                normalizedUrl == NormalizeUrl(post.SourceUrl))
            {
                return new DeduplicationResult(
                    DeduplicationStatus.Duplicate,
                    "Совпал URL уже сохранённого поста.",
                    post.Id);
            }

            if (!string.IsNullOrWhiteSpace(normalizedVideoUrl) &&
                normalizedVideoUrl == NormalizeUrl(post.VideoUrl))
            {
                return new DeduplicationResult(
                    DeduplicationStatus.Duplicate,
                    "Совпал URL видео уже сохранённого поста.",
                    post.Id);
            }

            var titleSimilarity = JaccardSimilarity(normalizedTitle, Normalize(post.SourceTitle));
            if (titleSimilarity >= 0.82)
            {
                return new DeduplicationResult(
                    DeduplicationStatus.Duplicate,
                    $"Очень похожий заголовок: similarity={titleSimilarity:0.00}.",
                    post.Id);
            }

            var topicSimilarity = JaccardSimilarity(normalizedTopic, NormalizeTopic(post.SourceTitle));
            if (topicSimilarity >= 0.55 && HasStrongSharedToken(normalizedTopic, NormalizeTopic(post.SourceTitle)))
            {
                return new DeduplicationResult(
                    DeduplicationStatus.Duplicate,
                    $"Похоже на тот же инфоповод: topic_similarity={topicSimilarity:0.00}.",
                    post.Id);
            }

            if (titleSimilarity >= 0.65)
            {
                return new DeduplicationResult(
                    DeduplicationStatus.Continuation,
                    $"Похоже на развитие уже опубликованной темы: similarity={titleSimilarity:0.00}.",
                    post.Id);
            }
        }

        return new DeduplicationResult(DeduplicationStatus.Unique, "Дублей в последних постах не найдено.");
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutAcronymDots = Regex.Replace(
            value,
            @"\b(?:[A-Za-z]\.){2,}[A-Za-z]?\.?",
            match => match.Value.Replace(".", string.Empty, StringComparison.Ordinal));

        var chars = withoutAcronymDots
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();

        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeTopic(string? value)
    {
        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "and", "of", "for", "to", "in", "on", "with",
            "official", "trailer", "teaser", "announcement", "announce", "announced",
            "launch", "gameplay", "story", "release", "date", "new", "revealed",
            "вышел", "вышла", "новый", "новая", "трейлер", "анонс", "анонсировали",
            "сюжетный", "релиз", "дата", "показали"
        };

        var tokens = Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !stopWords.Contains(token))
            .ToArray();

        return string.Join(' ', tokens);
    }

    private static string NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return Normalize(value);
        }

        var host = uri.Host.ToLowerInvariant().Replace("www.", string.Empty, StringComparison.Ordinal);
        if (host is "youtube.com" or "m.youtube.com" or "youtu.be")
        {
            var videoId = host == "youtu.be"
                ? uri.AbsolutePath.Trim('/')
                : ExtractQueryValue(uri.Query, "v");

            if (!string.IsNullOrWhiteSpace(videoId))
            {
                return $"youtube:{videoId}";
            }
        }

        return $"{host}{uri.AbsolutePath}".TrimEnd('/').ToLowerInvariant();
    }

    private static string? ExtractQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private static bool HasStrongSharedToken(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 4).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 4).ToHashSet(StringComparer.Ordinal);
        return leftTokens.Overlaps(rightTokens);
    }

    private static double JaccardSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var leftSet = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightSet = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        var intersection = leftSet.Intersect(rightSet).Count();
        var union = leftSet.Union(rightSet).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }
}
