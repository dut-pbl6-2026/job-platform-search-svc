using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Models;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

/// <summary>
/// Cache-aside for search query results (PBL6-19). 5-minute TTL keyed by
/// normalized query hash. Backed by Redis when REDIS_URL is set, otherwise
/// in-memory (see <see cref="Extensions.ElasticsearchServiceExtensions"/>).
/// All failures degrade to cache-miss — search stays available.
/// </summary>
public interface ISearchCache
{
    Task<SearchResult<JobDocument>?> GetAsync(SearchQuery query, CancellationToken ct = default);
    Task SetAsync(SearchQuery query, SearchResult<JobDocument> result, CancellationToken ct = default);
    Task InvalidateJobAsync(string jobId, CancellationToken ct = default);
}

public class RedisSearchCache : ISearchCache
{
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
        var key = BuildKey(query);
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
        var key = BuildKey(query);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
            var ttl = TimeSpan.FromSeconds(_options.TtlSeconds <= 0 ? 300 : _options.TtlSeconds);
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

    public Task InvalidateJobAsync(string jobId, CancellationToken ct = default)
    {
        // Query-result keys embed a full query hash; targeted invalidation needs
        // tag-based caching (SHOULD phase). 5m TTL bounds staleness; index writes
        // go through immediately so fresh reads miss cache only briefly.
        _logger.LogInformation("Search cache invalidate requested for job {JobId} (TTL-based, no-op)", jobId);
        return Task.CompletedTask;
    }

    public static string BuildKey(SearchQuery query)
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
            query.SortBy?.Trim().ToLowerInvariant() ?? "newest");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"search:{hash}";
    }
}
