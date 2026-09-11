using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TgAutoposter.Application.Profiles;

namespace TgAutoposter.Infrastructure.Profiles;

/// <summary>
/// Loads niche profiles from embedded JSON resources (Profiles/*.json) and, optionally, from a
/// directory given by <c>Profiles:Path</c>. Files in the directory override embedded ones with the same key.
/// </summary>
public sealed class NicheProfileProvider : INicheProfileProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
    };

    private readonly Dictionary<string, NicheProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public NicheProfileProvider(IConfiguration configuration, ILogger<NicheProfileProvider> logger)
    {
        LoadEmbedded(logger);

        var overridePath = configuration["Profiles:Path"];
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            foreach (var file in Directory.EnumerateFiles(overridePath, "*.json"))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<NicheProfile>(File.ReadAllText(file), JsonOptions);
                    if (profile is not null && !string.IsNullOrWhiteSpace(profile.Key))
                    {
                        _profiles[profile.Key] = profile;
                        logger.LogInformation("Niche profile {Key} loaded from {File}.", profile.Key, file);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to load niche profile from {File}.", file);
                }
            }
        }

        if (_profiles.Count == 0)
        {
            throw new InvalidOperationException("No niche profiles are available. At least one embedded Profiles/*.json is required.");
        }
    }

    public NicheProfile Get(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && _profiles.TryGetValue(key, out var profile))
        {
            return profile;
        }

        return _profiles.TryGetValue(NicheProfileKeys.Default, out var fallback)
            ? fallback
            : _profiles.Values.First();
    }

    public IReadOnlyList<NicheProfile> All()
    {
        return _profiles.Values.OrderBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void LoadEmbedded(ILogger logger)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !name.Contains(".Profiles.", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            try
            {
                var profile = JsonSerializer.Deserialize<NicheProfile>(stream, JsonOptions);
                if (profile is not null && !string.IsNullOrWhiteSpace(profile.Key))
                {
                    _profiles[profile.Key] = profile;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embedded niche profile {Resource} is invalid.", name);
            }
        }
    }
}
