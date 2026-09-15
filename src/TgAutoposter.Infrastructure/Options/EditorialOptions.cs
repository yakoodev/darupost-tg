namespace TgAutoposter.Infrastructure.Options;

/// <summary>
/// Editorial selection: instead of turning every fresh candidate into a post, the worker periodically asks the model
/// to rate open stories and drafts only the best one, spaced out and capped by the channel's daily limit.
/// </summary>
public sealed class EditorialOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minimum gap between two generated posts (breaking stories with a top score may skip it).</summary>
    public int MinMinutesBetweenPosts { get; set; } = 150;
    public int LookbackHours { get; set; } = 24;
    public int MaxStoriesPerReview { get; set; } = 20;
    /// <summary>Stories rated at least this (0–10) are publishable.</summary>
    public int MinScore { get; set; } = 7;
    /// <summary>Stories rated below this are dismissed so they are never reconsidered.</summary>
    public int RejectBelow { get; set; } = 5;
    /// <summary>Score that lets a story bypass the interval between posts.</summary>
    public int BreakingScore { get; set; } = 9;
    /// <summary>How many points lower the publish bar is for stories rated as RU/CIS scene.</summary>
    public int CisScoreDiscount { get; set; } = 1;
    /// <summary>Only draft posts inside the channel's schedule windows (local time).</summary>
    public bool RespectScheduleWindows { get; set; } = true;

    /// <summary>Meme lane: memes per local day drafted from meme-only sources (0 = off).</summary>
    public int MemesPerDay { get; set; } = 1;
    public int MemeLookbackHours { get; set; } = 36;
    public int MemeAttemptsPerRun { get; set; } = 3;
}
