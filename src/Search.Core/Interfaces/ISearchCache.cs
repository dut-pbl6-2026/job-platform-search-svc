using Search.Core.Models;

namespace Search.Core.Interfaces;

/// <summary>
/// Cache-aside contract for search query results. All failures must degrade
/// to cache-miss — search stays available without cache.
/// Invalidation is generation-based (see RedisSearchCache): bumping the
/// generation orphans all query-result keys at once, so InvalidateJobAsync
/// is O(1) and works for both Redis and the in-memory fallback.
/// Staleness after writes is bounded by the TTL (default 5 minutes).
/// </summary>
public interface ISearchCache
{
    Task<SearchResult<JobDocument>?> GetAsync(SearchQuery query, CancellationToken ct = default);
    Task SetAsync(SearchQuery query, SearchResult<JobDocument> result, CancellationToken ct = default);
    Task InvalidateJobAsync(string jobId, CancellationToken ct = default);
}
