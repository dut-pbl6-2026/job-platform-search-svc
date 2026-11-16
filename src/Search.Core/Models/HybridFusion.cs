namespace Search.Core.Models;

/// <summary>A job together with the score it was retrieved with (BM25 or cosine similarity).</summary>
public sealed record ScoredJob(JobDocument Job, double Score);

/// <summary>
/// Reciprocal fusion of the two rankings used by hybrid search (SRS NIC VECTOR-01-03).
///
/// Both retrievers are min-max normalised over the candidates they returned, then combined as
/// <c>keywordWeight * keywordScore + vectorWeight * vectorScore</c>. Raw BM25 scores and cosine
/// similarities live on different scales (roughly 0-20 and 0-1), so combining them without
/// normalising would let BM25 decide the order almost alone.
///
/// A job found by only one retriever keeps that retriever's contribution, so a strong semantic
/// match that does not contain the query words can still outrank a weak keyword match.
/// </summary>
public static class HybridFusion
{
    /// <summary>
    /// Fuse two ranked lists. Ordering is stable: fused score desc, then created date desc, then id,
    /// so paging over the same candidate set never repeats or skips a document.
    /// </summary>
    public static IReadOnlyList<ScoredJob> Fuse(
        IReadOnlyList<ScoredJob> keyword,
        IReadOnlyList<ScoredJob> vector,
        double keywordWeight,
        double vectorWeight)
    {
        var fused = new Dictionary<string, ScoredJob>(StringComparer.Ordinal);

        foreach (var (id, (job, score)) in Normalize(keyword))
        {
            fused[id] = new ScoredJob(job, keywordWeight * score);
        }

        foreach (var (id, (job, score)) in Normalize(vector))
        {
            fused[id] = fused.TryGetValue(id, out var existing)
                ? existing with { Score = existing.Score + vectorWeight * score }
                : new ScoredJob(job, vectorWeight * score);
        }

        return fused.Values
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Job.CreatedAt)
            .ThenBy(hit => hit.Job.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Id → (job, score) where score is min-max normalised to 0-1 (all equal scores → 1).</summary>
    private static Dictionary<string, (JobDocument Job, double Score)> Normalize(IReadOnlyList<ScoredJob> hits)
    {
        var normalized = new Dictionary<string, (JobDocument Job, double Score)>(StringComparer.Ordinal);
        if (hits.Count == 0)
            return normalized;

        var min = hits.Min(hit => hit.Score);
        var max = hits.Max(hit => hit.Score);
        var span = max - min;

        foreach (var hit in hits)
        {
            var score = span > 1e-9 ? (hit.Score - min) / span : 1.0;
            // Duplicate ids cannot come from Elasticsearch, but a max wins if they ever do.
            if (!normalized.TryGetValue(hit.Job.Id, out var existing) || score > existing.Score)
                normalized[hit.Job.Id] = (hit.Job, score);
        }

        return normalized;
    }
}
