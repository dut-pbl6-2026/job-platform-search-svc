using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Search.Core.Interfaces;
using Search.Tests.Infrastructure;

namespace Search.Tests.Integration;

/// <summary>
/// Runs the real API pipeline (endpoints, index-token filter, body limits, ElasticsearchService,
/// cache) with only the network edges replaced: the Elasticsearch client talks to
/// <see cref="ElasticsearchDouble"/> and the embedding client to <see cref="FakeEmbeddingClient"/>.
/// </summary>
public sealed class SearchApiFactory : WebApplicationFactory<Program>
{
    public const string IndexToken = "integration-test-token";

    public ElasticsearchDouble Es { get; } = new();

    public FakeEmbeddingClient Embeddings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["SEARCH_INDEX_TOKEN"] = IndexToken }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ElasticsearchClient>();
            services.AddSingleton(_ => new ElasticsearchClient(
                new ElasticsearchClientSettings(
                        new SingleNodePool(new Uri("http://search-es.test:9200")),
                        Es)
                    .DisableDirectStreaming()));

            services.RemoveAll<IEmbeddingClient>();
            services.AddSingleton<IEmbeddingClient>(Embeddings);
        });
    }
}
