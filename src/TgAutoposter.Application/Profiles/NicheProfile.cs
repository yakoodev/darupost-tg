using TgAutoposter.Domain.Common;

namespace TgAutoposter.Application.Profiles;

/// <summary>
/// Everything that makes a channel "about gaming" or "about VTubing" lives here as data:
/// prompts, keyword markers, classification rules, web-search templates and default sources.
/// Profiles are loaded from JSON (embedded defaults + optional override directory) and selected per channel.
/// </summary>
public sealed class NicheProfile
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    /// <summary>Used when a channel has no name (image cards, meme localization).</summary>
    public string BrandFallbackName { get; init; } = string.Empty;
    public string Language { get; init; } = "ru";

    public NicheChannelDefaults Channel { get; init; } = new();
    public NichePrompts Prompts { get; init; } = new();
    public NicheMarkers Markers { get; init; } = new();
    public List<NicheClassificationRule> Classification { get; init; } = [];
    public NicheWebSearch WebSearch { get; init; } = new();
    public List<NichePublicationType> PublicationTypes { get; init; } = [];
    public List<NicheSource> Sources { get; init; } = [];
    public List<NicheFooterLink> FooterLinks { get; init; } = [];
    public List<NicheScheduleWindow> ScheduleWindows { get; init; } = [];
    /// <summary>Starter talent registry (names, agencies, aliases, official accounts). Sources are created only when tracking is enabled.</summary>
    public List<NicheTalent> Talents { get; init; } = [];
}

public sealed class NicheChannelDefaults
{
    public string Name { get; init; } = string.Empty;
    public string TimeZone { get; init; } = "Europe/Moscow";
    public string Positioning { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public string StyleGuide { get; init; } = string.Empty;
    public int DailyPostLimit { get; init; } = 6;
    public decimal? DailyAiBudgetLimit { get; init; }
}

public sealed class NichePrompts
{
    /// <summary>First line of the fact-check system prompt, e.g. "Ты фактчек-редактор игрового Telegram-канала."</summary>
    public string FactCheckPersona { get; init; } = string.Empty;
    public string FactCheckSoftRule { get; init; } = string.Empty;
    public string FactCheckMediumRule { get; init; } = string.Empty;
    public string FactCheckStrictRule { get; init; } = string.Empty;

    /// <summary>First line of the dedup confirmation prompt.</summary>
    public string DedupPersona { get; init; } = string.Empty;

    /// <summary>Bullet requirements appended to every post-generation prompt.</summary>
    public List<string> TextRules { get; init; } = [];

    /// <summary>Full-bleed card background style description for the image model.</summary>
    public string ImageStyle { get; init; } = string.Empty;
    /// <summary>How to depict the subject safely (no logos etc.). Supports {subject}.</summary>
    public string ImageSubjectHint { get; init; } = string.Empty;
    /// <summary>Meme localisation prompt. Supports {brand}, {sourceImage}, {title}, {url}, {text}.</summary>
    public string MemeLocalization { get; init; } = string.Empty;

    /// <summary>Rubric chip text per publication kind (e.g. News → "НОВОСТИ").</summary>
    public Dictionary<string, string> Rubrics { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Digest generation instructions (evening summary of the day's stories).</summary>
    public string DigestInstructions { get; init; } = string.Empty;
}

public sealed class NicheMarkers
{
    /// <summary>If a candidate contains any of these it is dropped as low news value (reviews, guides...).</summary>
    public List<string> LowValue { get; init; } = [];
    /// <summary>A non-meme candidate must contain at least one of these to be considered news.</summary>
    public List<string> NewsSignal { get; init; } = [];
    /// <summary>Tokens ignored when comparing titles in the cheap dedup heuristic.</summary>
    public List<string> DedupStopWords { get; init; } = [];
    /// <summary>Markers that make a candidate a video/trailer candidate worth page-scraping for a video URL.</summary>
    public List<string> VideoSignal { get; init; } = [];
    /// <summary>Markers that mark a candidate as breaking / high priority.</summary>
    public List<string> BreakingSignal { get; init; } = [];
}

public sealed class NicheClassificationRule
{
    public PublicationKind Kind { get; init; }
    public List<string> Keywords { get; init; } = [];
    /// <summary>Also match when the source's subreddit / name contains any of these.</summary>
    public List<string> SourceNameHints { get; init; } = [];
}

public sealed class NicheWebSearch
{
    public string ResearcherPersona { get; init; } = string.Empty;
    public string DefaultQuery { get; init; } = string.Empty;
    /// <summary>Supports {today}, {brand}, {itemJson}.</summary>
    public string NewsPrompt { get; init; } = string.Empty;
    /// <summary>Supports {today}, {brand}, {itemJson}.</summary>
    public string VideoPrompt { get; init; } = string.Empty;
}

public sealed class NichePublicationType
{
    public PublicationKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Priority { get; init; } = 100;
    public ModerationMode ModerationMode { get; init; } = ModerationMode.Manual;
    public FactCheckMode FactCheckMode { get; init; } = FactCheckMode.Soft;
    public RumorPolicy RumorPolicy { get; init; } = RumorPolicy.AllowWithLabel;
    public bool RequiresFactCheck { get; init; } = true;
    public MediaGenerationMode MediaMode { get; init; } = MediaGenerationMode.None;
    public int MaxTextLength { get; init; } = 1000;
    public string SystemPrompt { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
}

public sealed class NicheSource
{
    public string Name { get; init; } = string.Empty;
    public SourceKind Kind { get; init; }
    public string? Url { get; init; }
    public string? Subreddit { get; init; }
    public RedditListingKind RedditListing { get; init; } = RedditListingKind.Hot;
    public int MinimumScore { get; init; }
    public int MinimumComments { get; init; }
    public int CheckEveryMinutes { get; init; } = 60;
    public string? AllowedPublicationKindsCsv { get; init; }
    public string? WhitelistKeywordsCsv { get; init; }
    public string? BlacklistKeywordsCsv { get; init; }
    public string Language { get; init; } = "en";
    public bool AllowNsfw { get; init; }
    public bool? RequireNewsSignal { get; init; }
    /// <summary>Editorial role: agency, newsline, personal, community, search or meme. Shown to the editor.</summary>
    public string? Role { get; init; }
    public bool IsEnabled { get; init; } = true;
}

public sealed class NicheFooterLink
{
    public string Label { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public int SortOrder { get; init; }
}

public sealed class NicheTalent
{
    public string Name { get; init; } = string.Empty;
    public string? Agency { get; init; }
    public string? Group { get; init; }
    public string? Aliases { get; init; }
    public int Priority { get; init; } = 2;
    public string? YouTube { get; init; }
    public string? Twitter { get; init; }
    public string? Telegram { get; init; }
}

public sealed class NicheScheduleWindow
{
    public string Start { get; init; } = "10:00";
    public string End { get; init; } = "12:00";
    public int MinimumIntervalMinutes { get; init; } = 60;
}

public interface INicheProfileProvider
{
    NicheProfile Get(string? key);
    IReadOnlyList<NicheProfile> All();
}

public static class NicheProfileKeys
{
    public const string Gaming = "gaming";
    public const string VTubing = "vtubing";
    public const string Default = Gaming;
}
