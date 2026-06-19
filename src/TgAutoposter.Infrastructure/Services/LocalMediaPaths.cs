using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public static class LocalMediaPaths
{
    public const string PublicPrefix = "media/";

    public static string ResolveRoot(MediaOptions options)
    {
        var root = string.IsNullOrWhiteSpace(options.RootPath)
            ? "media"
            : options.RootPath.Trim();

        return Path.GetFullPath(Path.IsPathRooted(root)
            ? root
            : Path.Combine(AppContext.BaseDirectory, root));
    }

    public static string BuildPublicPath(string category, string fileName)
    {
        return $"{PublicPrefix}{CleanSegment(category)}/{CleanSegment(fileName)}";
    }

    public static string BuildFullPath(MediaOptions options, string publicPath)
    {
        var root = ResolveRoot(options);
        var relative = publicPath.StartsWith(PublicPrefix, StringComparison.OrdinalIgnoreCase)
            ? publicPath[PublicPrefix.Length..]
            : publicPath;

        relative = relative
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : $"{root}{Path.DirectorySeparatorChar}";
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && !fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Local media path escapes configured media root.");
        }

        return fullPath;
    }

    public static bool TryResolve(MediaOptions options, string? value, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var path = value.Trim();
        if (path.StartsWith("/api/media/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["/api/".Length..];
        }

        if (!path.StartsWith(PublicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = BuildFullPath(options, path);
        return File.Exists(fullPath);
    }

    private static string CleanSegment(string value)
    {
        var cleaned = value
            .Replace('\\', '-')
            .Replace('/', '-')
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
    }
}
