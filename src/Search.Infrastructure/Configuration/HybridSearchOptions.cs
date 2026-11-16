namespace Search.Infrastructure.Configuration;

/// <summary>
/// Hybrid (keyword + vector) ranking for <c>GET /api/search/jobs</c> (SRS NIC VECTOR-01-03/04/05).
/// Weights default to keyword 40% / vector 60%; both lists are min-max normalised before they are
/// combined, because a raw BM25 score (0-20+) is not comparable to a cosine similarity (0-1).
/// </summary>
public class HybridSearchOptions
{
    public const string SectionName = "Search:Hybrid";

    /// <summary>When false, a keyword query uses BM25 only (same behaviour as before VECTOR-01).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Weight of the normalised BM25 score in the fused score.</summary>
    public double KeywordWeight { get; set; } = 0.4;

    /// <summary>Weight of the normalised cosine similarity in the fused score.</summary>
    public double VectorWeight { get; set; } = 0.6;

    /// <summary>
    /// A job that does not match the keywords is only added when its vector score reaches this floor.
    /// The kNN score is cosine mapped to 0-1; on the local MiniLM model unrelated jobs sit around
    /// 0.55-0.70 and a real semantic match starts around 0.72. Without a floor every query returns
    /// the whole index, just reordered. Vector scores are still used to re-rank keyword matches;
    /// the floor only gates additions. Set 0 to disable the gate.
    /// </summary>
    public double MinVectorScore { get; set; } = 0.72;

    /// <summary>
    /// Candidates fetched per retriever for a page: <c>max(from + size, size * multiplier)</c>.
    /// Higher is more accurate and slower; the pool is clamped by <see cref="MaxCandidates"/>.
    /// </summary>
    public int CandidateMultiplier { get; set; } = 3;

    /// <summary>
    /// Upper bound of the candidate pool per retriever. The HTTP API rejects offsets above 500,
    /// so the default covers every page the API can serve (500 + 100).
    /// </summary>
    public int MaxCandidates { get; set; } = 600;

    /// <summary>Fail-fast validation: a bad weight pair must stop startup, not silently rank with 0/0.</summary>
    public void Validate()
    {
        if (KeywordWeight < 0 || VectorWeight < 0)
            throw new InvalidOperationException("Search:Hybrid weights must not be negative.");
        if (KeywordWeight + VectorWeight <= 0)
            throw new InvalidOperationException("Search:Hybrid weights must not both be zero.");
        if (MinVectorScore is < 0 or > 1)
            throw new InvalidOperationException("Search:Hybrid:MinVectorScore must be between 0 and 1.");
        if (CandidateMultiplier < 1)
            throw new InvalidOperationException("Search:Hybrid:CandidateMultiplier must be at least 1.");
        if (MaxCandidates < 1)
            throw new InvalidOperationException("Search:Hybrid:MaxCandidates must be at least 1.");
    }
}
