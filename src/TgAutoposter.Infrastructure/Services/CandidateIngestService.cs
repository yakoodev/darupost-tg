using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed record IngestResult(Guid SourceId, string SourceName, int Collected, int NewCandidates, string? Error);

/// <summary>
/// Ingest phase: runs a collector for one source and stores what it found as <see cref="SourceCandidate"/> rows.
/// No AI calls happen here (except when the source itself is an AI web search), so it is safe to run all day.
/// Generation later picks candidates up from the table.
/// </summary>
public sealed class CandidateIngestService(
    AppDbContext db,
    IContentCollector collector,
    IDateTimeProvider clock,
    ILogger<CandidateIngestService> logger)
{
    /// <summary>Candidates older than this are ignored on ingest and expired when pending.</summary>
    public const int StaleHours = 48;

    public async Task<IngestResult> IngestSourceAsync(Channel channel, NicheProfile profile, Source source, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        IReadOnlyCollection<CollectedCandidate> collected;
        try
        {
            collected = await collector.CollectAsync(source, profile, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Source {SourceId} ({SourceName}) collection failed.", source.Id, source.Name);
            source.LastCheckedAtUtc = now;
            source.LastError = Truncate(ex.Message, 500);
            await db.SaveChangesAsync(cancellationToken);
            return new IngestResult(source.Id, source.Name, 0, 0, ex.Message);
        }

        source.LastCheckedAtUtc = now;
        source.LastCollectedAtUtc = now;
        source.LastCollectedCount = collected.Count;
        source.LastError = null;
        AddCollectorProviderUsageIfPresent(channel, source, collected);

        var staleSince = now.AddHours(-StaleHours);
        var fresh = 0;
        foreach (var item in collected)
        {
            if (item.FoundAtUtc < staleSince)
            {
                continue;
            }

            var hash = ComputeHash(source, item);
            var legacyHash = ComputeLegacyHash(item);
            var exists = await db.SourceCandidates.AnyAsync(
                candidate => candidate.ChannelId == channel.Id && (candidate.NormalizedHash == hash || candidate.NormalizedHash == legacyHash),
                cancellationToken);
            if (exists)
            {
                continue;
            }

            db.SourceCandidates.Add(new SourceCandidate
            {
                ChannelId = channel.Id,
                SourceId = source.Id,
                Title = TextSanitizer.Clean(item.Title),
                Url = item.Url,
                CanonicalUrl = item.Url,
                Summary = TextSanitizer.Clean(item.Summary),
                RawText = TextSanitizer.Clean(item.RawText),
                ImageUrl = item.ImageUrl,
                MediaUrlsJson = SerializeMediaUrls(item.MediaUrls),
                VideoUrl = item.VideoUrl,
                Score = item.Score,
                CommentsCount = item.CommentsCount,
                FoundAtUtc = item.FoundAtUtc.ToUniversalTime(),
                NormalizedHash = hash,
                MetadataJson = item.MetadataJson,
                ExternalId = Truncate(item.ExternalId, 256),
                Author = Truncate(item.Author, 256)
            });
            fresh++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new IngestResult(source.Id, source.Name, collected.Count, fresh, null);
    }

    private void AddCollectorProviderUsageIfPresent(Channel channel, Source source, IReadOnlyCollection<CollectedCandidate> collected)
    {
        var usageCandidate = collected.FirstOrDefault(candidate => candidate.ProviderCostAmount is not null);
        if (usageCandidate is null)
        {
            return;
        }

        db.AiUsageRecords.Add(new AiUsageRecord
        {
            ChannelId = channel.Id,
            Provider = "polza",
            Model = source.Name,
            TaskType = AiTaskType.StructuredOutput,
            CostAmount = usageCandidate.ProviderCostAmount,
            CostCurrency = AiCostDefaults.Currency,
            ProviderCostAmount = usageCandidate.ProviderCostAmount,
            ProviderCostCurrency = usageCandidate.ProviderCostCurrency,
            RequestMetadataJson = usageCandidate.ProviderUsageMetadataJson
        });
    }

    private static string ComputeHash(Source source, CollectedCandidate item)
    {
        return string.IsNullOrWhiteSpace(item.ExternalId)
            ? ComputeLegacyHash(item)
            : Sha256($"{source.Kind}|{source.Subreddit ?? source.Url ?? source.Name}|ext|{item.ExternalId}");
    }

    private static string ComputeLegacyHash(CollectedCandidate item) => Sha256($"{item.Url}|{item.Title}|{item.Summary}");

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant().Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? SerializeMediaUrls(IReadOnlyCollection<string>? mediaUrls)
    {
        if (mediaUrls is null || mediaUrls.Count == 0)
        {
            return null;
        }

        var normalized = mediaUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return normalized.Length == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= max ? value : value[..max];
    }
}
