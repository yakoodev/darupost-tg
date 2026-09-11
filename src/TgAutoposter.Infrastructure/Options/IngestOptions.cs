namespace TgAutoposter.Infrastructure.Options;

/// <summary>All-day source ingestion (no AI). Each source is still gated by its own CheckEveryMinutes.</summary>
public sealed class IngestOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
}
