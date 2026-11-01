using System.Net;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;
using HttpMethod = System.Net.Http.HttpMethod;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using Search.Api.Health;
using Search.Infrastructure.Configuration;

namespace Search.Tests.Api;

public class HealthReadinessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthReadinessTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // In-memory transport: deterministic, no network.
    private static ElasticsearchClient EsClient(InMemoryRequestInvoker connection) =>
        new(new ElasticsearchClientSettings(new SingleNodePool(new Uri("http://localhost:9200")), connection));

    private HttpClient ClientWith(ElasticsearchClient es) =>
        _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ElasticsearchClient>();
            s.AddSingleton(es);
        })).CreateClient();

    [Fact]
    public async Task Ready_Returns200_WhenElasticsearchPings()
    {
        var client = ClientWith(EsClient(new InMemoryRequestInvoker()));

        var res = await client.GetAsync("/health/ready");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
        doc.RootElement.GetProperty("checks").GetProperty("elasticsearch").GetProperty("status").GetString().Should().Be("healthy");
        doc.RootElement.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString().Should().Be("healthy");
    }

    [Fact]
    public async Task Ready_Returns503_WhenElasticsearchDown()
    {
        var client = ClientWith(EsClient(new InMemoryRequestInvoker(
            Array.Empty<byte>(), statusCode: 503)));

        var res = await client.GetAsync("/health/ready");

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("unhealthy");
    }

    [Fact]
    public async Task Health_StaysStatic_EvenWhenElasticsearchDown()
    {
        var client = ClientWith(EsClient(new InMemoryRequestInvoker(Array.Empty<byte>(), statusCode: 503)));

        var res = await client.GetAsync("/health");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Be("""{"status":"ok","service":"search"}""");
    }

    [Fact]
    public async Task RedisCheck_Degraded_WhenConfiguredAndProbeThrows()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var check = new RedisHealthCheck(
            Options.Create(new RedisOptions { Url = "localhost:6379" }), cache.Object);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task RedisCheck_Healthy_WhenNotConfigured()
    {
        var check = new RedisHealthCheck(
            Options.Create(new RedisOptions { Url = "" }), Mock.Of<IDistributedCache>());

        (await check.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CorrelationId_GeneratedWhenAbsent_EchoedWhenProvided()
    {
        var client = ClientWith(EsClient(new InMemoryRequestInvoker()));

        var generated = await client.GetAsync("/health");
        Guid.TryParse(generated.Headers.GetValues("X-Correlation-Id").Single(), out _).Should().BeTrue();

        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Correlation-Id", "abc-123");
        var echoed = await client.SendAsync(req);
        echoed.Headers.GetValues("X-Correlation-Id").Single().Should().Be("abc-123");

        var bad = new HttpRequestMessage(HttpMethod.Get, "/health");
        bad.Headers.Add("X-Correlation-Id", "bad value\twith spaces");
        (await client.SendAsync(bad)).Headers.GetValues("X-Correlation-Id").Single().Should().NotBe("bad value\twith spaces");
    }
}
