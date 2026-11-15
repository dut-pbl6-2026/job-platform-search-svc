using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Search.Tests.Integration;

namespace Search.Tests.Api;

/// <summary>
/// Shared host for the Api test classes. Every host start runs the startup index check against
/// Elasticsearch; with the real client that dials http://localhost:9200, which hangs for about a
/// minute per host on machines where closed localhost ports are not refused. This factory routes
/// that client to the in-memory <see cref="ElasticsearchDouble"/> so no test host touches the network.
/// Per-test overrides still run after this registration and take precedence.
/// </summary>
public class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly ElasticsearchDouble _es = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ElasticsearchClient>();
            services.AddSingleton(_ => new ElasticsearchClient(
                new ElasticsearchClientSettings(
                        new SingleNodePool(new Uri("http://search-es.test:9200")),
                        _es)
                    .DisableDirectStreaming()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _es.Dispose();
        }

        base.Dispose(disposing);
    }
}
