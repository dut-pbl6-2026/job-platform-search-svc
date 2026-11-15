using Search.Core.Interfaces;

namespace Search.Tests.Infrastructure;

/// <summary>Deterministic vectors: [text length, 1, 0]. Records every batch it was asked to embed.</summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public int Dimension { get; init; } = 3;
    public bool Fail { get; set; }
    public List<IReadOnlyList<string>> Batches { get; } = new();

    public Task<EmbeddingModelInfo> GetModelInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new EmbeddingModelInfo("fake-model", Dimension));

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new EmbeddingUnavailableException("embedding service down");
        }

        Batches.Add(texts);
        IReadOnlyList<float[]> vectors = texts.Select(t => new float[] { t.Length, 1f, 0f }).ToArray();
        return Task.FromResult(vectors);
    }
}
