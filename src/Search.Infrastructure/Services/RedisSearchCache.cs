using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

/// <summary>
/// Cache-aside for search query results (PBL6-19). Keyed by normalized query
/// hash namespaced with a generation counter; index/delete writes bump the
/// generation (O(1), no key SCAN) so stale entries are orphaned immediately,
/// with TTL as the outer staleness bound. Backed by Redis when REDIS_URL is
/// set, otherwise in-memory (see <see cref="Extensions.ElasticsearchServiceExtensions"/>).
/// All failures degrade to cache-miss — search stays available.
/// </summary>
public class RedisSearchCache : ISearchCache
{
    private const string GenerationKey = "search:gen";

    private readonly IDistributedCache _cache;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisSearchCache> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RedisSearchCache(IDistributedCache cache, IOptions<RedisOptions> options, ILogger<RedisSearchCache> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SearchResult<JobDocument>?> GetAsync(SearchQuery query, CancellationToken ct = default)
    {
        var key = await BuildKeyAsync(query, ct);
        try
        {
            var bytes = await _cache.GetAsync(key, ct);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<SearchResult<JobDocument>>(bytes, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search cache GET failed for {Key}", key);
            return null;
        }
    }

    public async Task SetAsync(SearchQuery query, SearchResult<JobDocument> result, CancellationToken ct = default)
    {
        var key = await BuildKeyAsync(query, ct);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            var ttl = TimeSpan.FromSeconds(
                _options.TtlSeconds <= 0 ? SearchCacheDefaults.DefaultTtlSeconds : _options.TtlSeconds);
            await _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search cache SET failed for {Key}", key);
        }
    }

    public async Task InvalidateJobAsync(string jobId, CancellationToken ct = default)
    {
        // Generation bump orphans every query-result key at once (O(1), no SCAN —
        // IDistributedCache has no enumeration API). At MVP write volumes a
        // full-namespace orphan is cheaper than per-key tracking; the next read
        // repopulates from Elasticsearch.
        try
        {
            var generation = await GetGenerationAsync(ct) + 1;
            await _cache.SetAsync(
                GenerationKey,
                Encoding.UTF8.GetBytes(generation.ToString()),
                new DistributedCacheEntryOptions
                {
                    // Longer than any result TTL so the counter outlives the keys it orphans.
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                }, ct);
            _logger.LogInformation("Search cache invalidated for job {JobId} (generation {Generation})", jobId, generation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search cache invalidate failed for job {JobId}", jobId);
        }
    }

    private async Task<string> BuildKeyAsync(SearchQuery query, CancellationToken ct)
    {
        var generation = await GetGenerationAsync(ct);
        return $"search:g{generation}:{BuildKeyHash(query)}";
    }

    private async Task<long> GetGenerationAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await _cache.GetAsync(GenerationKey, ct);
            if (bytes is not null && long.TryParse(Encoding.UTF8.GetString(bytes), out var generation))
            {
                return generation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search cache generation read failed");
        }

        return 0;
    }

    internal static string BuildKeyHash(SearchQuery query)
    {
        var raw = string.Join("|",
            query.Keyword?.Trim().ToLowerInvariant() ?? "",
            query.Location?.Trim().ToLowerInvariant() ?? "",
            query.Category?.Trim().ToLowerInvariant() ?? "",
            query.EmploymentType?.Trim().ToLowerInvariant() ?? "",
            query.ExperienceLevel?.Trim().ToLowerInvariant() ?? "",
            query.MinSalary?.ToString() ?? "",
            query.MaxSalary?.ToString() ?? "",
            query.NormalizedPage.ToString(),
            query.NormalizedSize.ToString(),
            query.SortBy?.Trim().ToLowerInvariant() ?? "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return hash;
    }

    /// <summary>Legacy key builder kept for tests: hash portion of the cache key.</summary>
    public static string BuildKey(SearchQuery query) => BuildKeyHash(query);
}
