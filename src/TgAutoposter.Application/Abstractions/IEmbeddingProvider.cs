namespace TgAutoposter.Application.Abstractions;

public interface IEmbeddingProvider
{
    /// <summary>
    /// Returns an embedding vector for the text, or null when embeddings are unavailable
    /// (provider disabled, no key, or the call failed). Callers must handle null gracefully.
    /// </summary>
    Task<float[]?> EmbedAsync(Guid channelId, string text, CancellationToken cancellationToken);
}
