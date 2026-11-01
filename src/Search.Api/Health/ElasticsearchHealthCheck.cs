using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Search.Api.Health;

/// <summary>Elasticsearch ping bounded by a short timeout. ES is the core dependency: down = Unhealthy.</summary>
public sealed class ElasticsearchHealthCheck(ElasticsearchClient client) : IHealthCheck
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            var ping = await client.PingAsync(cts.Token);
            return ping.IsValidResponse
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Elasticsearch ping failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Elasticsearch ping timed out.");
        }
        catch (Exception ex)
        {
            // Exception type only — messages may embed URLs/credentials.
            return HealthCheckResult.Unhealthy($"Elasticsearch check failed: {ex.GetType().Name}");
        }
    }
}
