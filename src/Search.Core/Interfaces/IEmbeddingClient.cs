namespace Search.Core.Interfaces;

/// <summary>Local embedding model served by ai-svc (internal endpoints only).</summary>
public interface IEmbeddingClient
{
    /// <summary>Model name and vector size reported by the running model. Throws <see cref="EmbeddingUnavailableException"/>.</summary>
    Task<EmbeddingModelInfo> GetModelInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>One vector per text, in input order. Throws <see cref="EmbeddingUnavailableException"/> on any failure.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}

public record EmbeddingModelInfo(string Model, int Dimension);

/// <summary>The embedding service cannot produce vectors. Callers must not store or query without them.</summary>
public class EmbeddingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
