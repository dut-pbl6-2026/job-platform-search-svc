using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Search.Infrastructure.Configuration;

namespace Search.Api.Health;

/// <summary>
/// Probes the distributed cache only when Redis is configured (REDIS_URL). The cache degrades to
/// cache-miss on failure (see RedisSearchCache), so Redis down is Degraded, not Unhealthy.
/// </summary>
public sealed class RedisHealthCheck(IOptions<RedisOptions> options, IDistributedCache cache) : IHealthCheck
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Url))
            return HealthCheckResult.Healthy("Redis not configured (in-memory cache).");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            await cache.GetAsync("health:probe", cts.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("Redis probe timed out; cache bypassed.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"Redis probe failed ({ex.GetType().Name}); cache bypassed.");
        }
    }
}
