using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TgAutoposter.Domain.Talents;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Infrastructure.Services;

public sealed record TalentMatch(Guid Id, string Name, string? Agency, int Priority);

/// <summary>
/// Finds registry talents mentioned in a text by canonical name or alias (case-insensitive, whole-word for Latin,
/// substring for CJK). Loaded once per channel per scope.
/// </summary>
public sealed class TalentMatcher(AppDbContext db)
{
    private readonly Dictionary<Guid, List<(Talent Talent, Regex Pattern)>> _cache = new();

    public async Task<IReadOnlyList<TalentMatch>> MatchAsync(Guid channelId, string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var index = await LoadAsync(channelId, cancellationToken);
        if (index.Count == 0)
        {
            return [];
        }

        var result = new List<TalentMatch>();
        var seen = new HashSet<Guid>();
        foreach (var (talent, pattern) in index)
        {
            if (seen.Contains(talent.Id) || !pattern.IsMatch(text))
            {
                continue;
            }

            seen.Add(talent.Id);
            result.Add(new TalentMatch(talent.Id, talent.Name, talent.Agency, talent.Priority));
        }

        return result;
    }

    public void Invalidate(Guid channelId) => _cache.Remove(channelId);

    private async Task<List<(Talent, Regex)>> LoadAsync(Guid channelId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(channelId, out var cached))
        {
            return cached;
        }

        var talents = await db.Talents
            .AsNoTracking()
            .Where(talent => talent.ChannelId == channelId && talent.IsActive)
            .ToListAsync(cancellationToken);

        var index = new List<(Talent, Regex)>();
        foreach (var talent in talents)
        {
            var names = new List<string> { talent.Name };
            if (!string.IsNullOrWhiteSpace(talent.AliasesCsv))
            {
                names.AddRange(talent.AliasesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            var alternatives = names
                .Where(name => name.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => IsCjk(name) ? Regex.Escape(name) : $"(?<![\\p{{L}}\\p{{Nd}}]){Regex.Escape(name)}(?![\\p{{L}}\\p{{Nd}}])")
                .ToList();
            if (alternatives.Count == 0)
            {
                continue;
            }

            var pattern = new Regex(string.Join("|", alternatives), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            index.Add((talent, pattern));
        }

        _cache[channelId] = index;
        return index;
    }

    private static bool IsCjk(string value) => value.Any(ch => ch >= 0x3040 && ch <= 0x9FFF);
}
