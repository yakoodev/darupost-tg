using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// Keyword markers match at word boundaries instead of as raw substrings: short markers (up to 3 characters) must be a whole
/// word, longer ones must start a word (stems like "graduat" still match "graduation"). This stops false hits such as
/// "бан" inside "Higanbanban" or "clip" inside "eclipse".
/// </summary>
public static class MarkerMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool ContainsAny(string? text, IEnumerable<string> markers)
    {
        return !string.IsNullOrEmpty(text) && markers.Any(marker => Matches(text, marker));
    }

    public static bool Matches(string? text, string? marker)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        return Cache.GetOrAdd(marker.Trim(), Build).IsMatch(text);
    }

    private static Regex Build(string marker)
    {
        var escaped = Regex.Escape(marker);
        var pattern = marker.Length <= 3
            ? $@"(?<![\p{{L}}\p{{Nd}}]){escaped}(?![\p{{L}}\p{{Nd}}])"
            : $@"(?<![\p{{L}}\p{{Nd}}]){escaped}";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
