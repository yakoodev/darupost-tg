namespace TgAutoposter.Infrastructure.Options;

/// <summary>Evening digest scheduler. The per-channel time and enable flag live on the channel itself.</summary>
public sealed class DigestOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 5;
    /// <summary>If the digest time was missed by more than this (downtime), skip today's run instead of posting late.</summary>
    public int LateWindowHours { get; set; } = 4;
}
