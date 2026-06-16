namespace TgAutoposter.Application.Abstractions;

public interface IAiAccountStatusClient
{
    Task<AiAccountStatus> GetStatusAsync(CancellationToken cancellationToken);
}

public sealed record AiAccountStatus(
    bool Enabled,
    bool HasApiKey,
    string BaseUrl,
    string DefaultModel,
    string ImageModel,
    decimal? BalanceRub,
    string? Error);
