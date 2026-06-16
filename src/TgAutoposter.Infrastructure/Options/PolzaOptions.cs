namespace TgAutoposter.Infrastructure.Options;

public sealed class PolzaOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://polza.ai/api/v1";
    public string ChatCompletionPath { get; set; } = "/chat/completions";
    public string DefaultModel { get; set; } = "openai/gpt-5.5";
    public string ImageModel { get; set; } = "openai/gpt-5.4-image-2";
    public int MaxTokens { get; set; } = 900;
    public double Temperature { get; set; } = 0.55;
    public string ImageAspectRatio { get; set; } = "4:5";
    public string ImageResolution { get; set; } = "1K";
    public int TimeoutSeconds { get; set; } = 180;
}
