using System.Text;
using System.Text.RegularExpressions;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// Removes the Unicode replacement char (U+FFFD "") and stray control characters that leak in from
/// mis-decoded feeds, scraped pages, or model output, so corrupted glyphs never reach a post.
/// </summary>
public static partial class TextSanitizer
{
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '�')
            {
                continue;
            }

            if (char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))
            {
                continue;
            }

            builder.Append(ch);
        }

        // Collapse runs of spaces/tabs but keep line breaks.
        return HorizontalSpaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex("[ \\t]{2,}")]
    private static partial Regex HorizontalSpaceRegex();
}
