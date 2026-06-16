namespace TgAutoposter.Infrastructure.Options;

public sealed class WorkerOptions
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 15;
    public int MaxPostsPerRun { get; set; } = 1;
}
