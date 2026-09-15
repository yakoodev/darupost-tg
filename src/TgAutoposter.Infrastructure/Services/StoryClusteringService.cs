using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Domain.Stories;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// Groups fresh candidates into stories. Embeddings (cosine ≥ <see cref="EmbeddingThreshold"/>) when available,
/// otherwise a title-token Jaccard heuristic with the profile's stop words. Cheap enough to run after every ingest sweep.
/// </summary>
public sealed class StoryClusteringService(
    AppDbContext db,
    IEmbeddingProvider embeddingProvider,
    INicheProfileProvider profiles,
    IDateTimeProvider clock,
    TalentMatcher talents,
    ILogger<StoryClusteringService> logger)
{
    public const int LookbackHours = 48;
    private const int StoryWindowHours = 72;
    private const double EmbeddingThreshold = 0.86;
    private const double JaccardThreshold = 0.5;

    private static readonly string[] GenericStopWords =
    [
        "the", "a", "an", "and", "of", "for", "to", "in", "on", "with", "new", "official", "is", "are", "has", "have",
        "новый", "новая", "новое", "и", "в", "на", "с", "по", "для", "от", "это"
    ];

    public async Task<int> ClusterPendingAsync(Channel channel, CancellationToken cancellationToken)
    {
        var profile = profiles.Get(channel.ProfileKey);
        var now = clock.UtcNow;
        var since = now.AddHours(-LookbackHours);

        var pending = await db.SourceCandidates
            .Where(candidate => candidate.ChannelId == channel.Id &&
                                candidate.StoryId == null &&
                                candidate.CreatedAtUtc >= since &&
                                (candidate.ConsumedReason == null || candidate.ConsumedReason == "post" || candidate.ConsumedReason == "duplicate"))
            .OrderBy(candidate => candidate.FoundAtUtc)
            .Take(300)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var stories = await db.Stories
            .Where(story => story.ChannelId == channel.Id &&
                            story.LastSeenAtUtc >= now.AddHours(-StoryWindowHours) &&
                            story.Status != StoryStatus.Dismissed)
            .ToListAsync(cancellationToken);

        var stopWords = new HashSet<string>(GenericStopWords, StringComparer.Ordinal);
        foreach (var word in profile.Markers.DedupStopWords)
        {
            stopWords.Add(word.ToLowerInvariant());
        }

        var vectors = stories.ToDictionary(story => story.Id, story => Deserialize(story.EmbeddingJson));
        var tokens = stories.ToDictionary(story => story.Id, story => TopicTokens(story.Title, stopWords));
        var touched = new HashSet<Guid>();
        var assigned = 0;

        foreach (var candidate in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vector = Deserialize(candidate.EmbeddingJson);
            if (vector.Length == 0)
            {
                try
                {
                    vector = await embeddingProvider.EmbedAsync(channel.Id, AiDeduplicationService.EmbeddingText(candidate.Title, candidate.Summary), cancellationToken) ?? [];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Embedding failed for candidate {CandidateId}; falling back to title heuristics.", candidate.Id);
                    vector = [];
                }

                if (vector.Length > 0)
                {
                    candidate.EmbeddingJson = JsonSerializer.Serialize(vector);
                }
            }

            var candidateTokens = TopicTokens(candidate.Title, stopWords);
            var candidateTalents = await talents.MatchAsync(channel.Id, $"{candidate.Title}\n{candidate.Summary}", cancellationToken);
            var candidateTalentNames = candidateTalents.Select(talent => talent.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Story? match = null;
            var bestScore = 0.0;

            foreach (var story in stories)
            {
                double score;
                var sharesTalent = candidateTalentNames.Count > 0 &&
                                   !string.IsNullOrWhiteSpace(story.TalentsCsv) &&
                                   story.TalentsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(candidateTalentNames.Contains);
                if (vector.Length > 0 && vectors[story.Id].Length == vector.Length)
                {
                    score = CosineSimilarity(vector, vectors[story.Id]);
                    if (score < EmbeddingThreshold)
                    {
                        // Embeddings disagree strongly — don't let a loose title match override that,
                        // unless the same talent is involved and the titles still overlap.
                        var jaccard = Jaccard(candidateTokens, tokens[story.Id]);
                        score = score >= 0.75 ? jaccard : sharesTalent && score >= 0.6 && jaccard >= 0.3 ? jaccard : 0;
                        if (score < (sharesTalent ? 0.3 : JaccardThreshold))
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    score = Jaccard(candidateTokens, tokens[story.Id]);
                    var threshold = sharesTalent ? 0.3 : JaccardThreshold;
                    if (score < threshold || (!sharesTalent && !HasStrongSharedToken(candidateTokens, tokens[story.Id])))
                    {
                        continue;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    match = story;
                }
            }

            if (match is null)
            {
                match = new Story
                {
                    ChannelId = channel.Id,
                    Title = candidate.Title,
                    Summary = candidate.Summary,
                    FirstSeenAtUtc = candidate.FoundAtUtc,
                    LastSeenAtUtc = candidate.FoundAtUtc,
                    IsBreaking = ContainsAny($"{candidate.Title}\n{candidate.Summary}", profile.Markers.BreakingSignal),
                    KindHint = GuessKind(profile, candidate),
                    EmbeddingJson = vector.Length > 0 ? JsonSerializer.Serialize(vector) : null,
                    TalentsCsv = candidateTalentNames.Count == 0 ? null : string.Join(", ", candidateTalentNames),
                    LeadCandidateId = candidate.Id
                };
                db.Stories.Add(match);
                stories.Add(match);
                vectors[match.Id] = vector;
                tokens[match.Id] = candidateTokens;
            }
            else
            {
                if (vector.Length > 0)
                {
                    var current = vectors[match.Id];
                    var weight = Math.Max(1, match.CandidatesCount);
                    var merged = current.Length == vector.Length
                        ? current.Select((value, index) => (value * weight + vector[index]) / (weight + 1)).ToArray()
                        : vector;
                    vectors[match.Id] = merged;
                    match.EmbeddingJson = JsonSerializer.Serialize(merged);
                }

                match.IsBreaking |= ContainsAny($"{candidate.Title}\n{candidate.Summary}", profile.Markers.BreakingSignal);
                if (candidateTalentNames.Count > 0)
                {
                    var merged = (match.TalentsCsv ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Union(candidateTalentNames, StringComparer.OrdinalIgnoreCase)
                        .Take(12);
                    match.TalentsCsv = string.Join(", ", merged);
                }
                if (candidate.FoundAtUtc > match.LastSeenAtUtc)
                {
                    match.LastSeenAtUtc = candidate.FoundAtUtc;
                }

                if (candidate.FoundAtUtc < match.FirstSeenAtUtc)
                {
                    match.FirstSeenAtUtc = candidate.FoundAtUtc;
                }
            }

            candidate.Story = match;
            candidate.StoryId = match.Id;
            match.CandidatesCount++;
            touched.Add(match.Id);
            assigned++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await RecomputeAsync(channel.Id, touched, now, cancellationToken);
        logger.LogInformation("Clustering: channel {Channel} assigned {Assigned} candidate(s) into {Stories} story(ies).", channel.Name, assigned, touched.Count);
        return assigned;
    }

    /// <summary>Recomputes counts, lead candidate and score for the given stories from their candidates.</summary>
    public async Task RecomputeAsync(Guid channelId, IReadOnlyCollection<Guid> storyIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (storyIds.Count == 0)
        {
            return;
        }

        var ids = storyIds.ToList();
        var stories = await db.Stories.Where(story => ids.Contains(story.Id)).ToListAsync(cancellationToken);
        var candidates = await db.SourceCandidates
            .Where(candidate => candidate.StoryId != null && ids.Contains(candidate.StoryId.Value))
            .Select(candidate => new { candidate.Id, StoryId = candidate.StoryId!.Value, candidate.SourceId, candidate.Score, candidate.CommentsCount, candidate.FoundAtUtc, candidate.Title, candidate.Summary })
            .ToListAsync(cancellationToken);

        var kinds = await db.Sources
            .Where(source => source.ChannelId == channelId)
            .Select(source => new { source.Id, source.Kind })
            .ToDictionaryAsync(source => source.Id, source => source.Kind, cancellationToken);

        foreach (var story in stories)
        {
            var own = candidates.Where(candidate => candidate.StoryId == story.Id).ToList();
            if (own.Count == 0)
            {
                continue;
            }

            story.CandidatesCount = own.Count;
            story.SourcesCount = own.Select(candidate => candidate.SourceId).Distinct().Count();

            // Lead: an official / curated source beats crowd sources; then engagement; then earliest.
            var lead = own
                .OrderByDescending(candidate => kinds.TryGetValue(candidate.SourceId, out var kind) && kind is SourceKind.YouTube or SourceKind.Twitter or SourceKind.Telegram ? 1 : 0)
                .ThenByDescending(candidate => candidate.Score ?? 0)
                .ThenBy(candidate => candidate.FoundAtUtc)
                .First();
            story.LeadCandidateId = lead.Id;
            if (string.IsNullOrWhiteSpace(story.Summary) || story.Summary == story.Title)
            {
                story.Summary = lead.Summary;
            }

            var engagement = own.Sum(candidate => (candidate.Score ?? 0) + (candidate.CommentsCount ?? 0) * 2);
            var hoursSinceLast = Math.Max(0, (now - story.LastSeenAtUtc).TotalHours);
            var recency = hoursSinceLast < 6 ? 3 : hoursSinceLast < 12 ? 2 : hoursSinceLast < 24 ? 1 : 0;
            var kindBonus = own.Count(candidate => kinds.TryGetValue(candidate.SourceId, out var kind) && kind is SourceKind.YouTube or SourceKind.Twitter) > 0 ? 2 : 0;
            var talentMatches = await talents.MatchAsync(channelId, $"{story.Title}\n{story.Summary}", cancellationToken);
            var talentBonus = talentMatches.Count == 0 ? 0 : talentMatches.Min(talent => talent.Priority) switch { 1 => 5, 2 => 2, _ => 1 };

            story.Score = story.SourcesCount * 3
                          + Math.Min(story.CandidatesCount, 10)
                          + Math.Log10(1 + Math.Max(0, engagement)) * 2
                          + (story.IsBreaking ? 10 : 0)
                          + recency
                          + kindBonus
                          + talentBonus;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static PublicationKind? GuessKind(NicheProfile profile, SourceCandidate candidate)
    {
        var text = $"{candidate.Title}\n{candidate.Summary}";
        foreach (var rule in profile.Classification)
        {
            if (rule.Kind != PublicationKind.Meme && ContainsAny(text, rule.Keywords))
            {
                return rule.Kind;
            }
        }

        return null;
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers)
        => MarkerMatcher.ContainsAny(text, markers);

    private static HashSet<string> TopicTokens(string? value, HashSet<string> stopWords)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+", " ");
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !stopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static bool HasStrongSharedToken(HashSet<string> a, HashSet<string> b)
        => a.Intersect(b).Any(token => token.Length >= 4);

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

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
