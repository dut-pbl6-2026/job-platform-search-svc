using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Search.Tests.Api;

/// <summary>
/// /metrics backs the Prometheus scrape target (infra/docker/prometheus/prometheus.yml).
/// Asserts the instrumentation is actually wired up, not just that the route exists.
/// </summary>
public class MetricsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public MetricsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetMetrics_ReturnsPrometheusTextWithRuntimeAndHttpMetrics()
    {
        // Arrange: one request so ASP.NET Core metrics have a sample to export.
        await _client.GetAsync("/health");

        // Act
        var response = await _client.GetAsync("/metrics");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Contain("text/plain");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("dotnet_gc_last_collection_heap_size_bytes");
        content.Should().Contain("http_server_request_duration_seconds_count");
    }
}
