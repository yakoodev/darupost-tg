namespace TgAutoposter.Infrastructure.Options;

/// <summary>
/// Keyless Twitch clip analysis. No app registration / no OAuth: clips are public and are read via the
/// public web GraphQL client-id (the same one yt-dlp / streamlink / twitch-dl use). A source's
/// <see cref="Domain.Sources.Source.Url"/> holds the channel login (e.g. "qchaan_9").
/// </summary>
public sealed class TwitchOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Public Twitch web client-id used by all public clip tooling. No secret, no account.</summary>
    public string PublicClientId { get; set; } = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    public string GqlUrl { get; set; } = "https://gql.twitch.tv/gql";

    /// <summary>Polza multimodal model that watches the clip video (with audio) and judges it.</summary>
    public string AnalysisModel { get; set; } = "google/gemini-3.8-flash";

    /// <summary>GQL clip period filter: LAST_DAY, LAST_WEEK, LAST_MONTH, ALL_TIME.</summary>
    public string Period { get; set; } = "LAST_WEEK";

    /// <summary>Only clips created within this window are considered. A week matches the "clip of the week"
    /// highlight framing; RU clip volume is low, so a 48h window starves the feed.</summary>
    public int LookbackHours { get; set; } = 168;

    /// <summary>Community view floor: clips below this are not worth analysing (views = the crowd's "funny" vote).</summary>
    public int MinViews { get; set; } = 400;

    /// <summary>Per channel, per run: the top-N fresh unseen clips to analyse.</summary>
    public int MaxClipsPerChannelPerRun { get; set; } = 2;

    /// <summary>Hard global spend guard: stop analysing once this many clips were analysed today (across all channels).</summary>
    public int DailyAnalysisCap { get; set; } = 30;

    /// <summary>Post-worthy bar: keep a clip only if it is a real news moment or a genuinely funny meme,
    /// scored at least this high (0-10). Mundane gameplay / small talk is dropped.</summary>
    public int ScoreThreshold { get; set; } = 7;

    /// <summary>Skip a clip whose smallest downloadable file is bigger than this (guards memory / cost).</summary>
    public int MaxClipMegabytes { get; set; } = 20;

    /// <summary>How many clips to pull per channel from GQL before filtering (views-desc).</summary>
    public int EnumerateCount { get; set; } = 30;

    public int TimeoutSeconds { get; set; } = 180;

    public bool IsConfigured => Enabled;
}
