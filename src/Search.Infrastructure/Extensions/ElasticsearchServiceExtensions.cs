using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;

namespace Search.Infrastructure.Extensions;

public static class ElasticsearchServiceExtensions
{
    public static IServiceCollection AddElasticsearchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Fail-fast config resolution (Zero hardcoding rule)
        var esUrl = configuration["ELASTICSEARCH_URL"]
                    ?? configuration["Elasticsearch:Url"]
                    ?? throw new InvalidOperationException("ES URL not configured. Set ELASTICSEARCH_URL or Elasticsearch:Url.");

        var esIndex = configuration["ELASTICSEARCH_INDEX"]
                      ?? configuration["Elasticsearch:Index"]
                      ?? throw new InvalidOperationException("ES index not configured. Set ELASTICSEARCH_INDEX or Elasticsearch:Index.");

        services.Configure<ElasticsearchOptions>(o =>
        {
            o.Url = esUrl;
            o.Index = esIndex;
            o.Username = configuration["ELASTICSEARCH_USERNAME"] ?? configuration["Elasticsearch:Username"];
            o.Password = configuration["ELASTICSEARCH_PASSWORD"] ?? configuration["Elasticsearch:Password"];
        });

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;
            var settings = new ElasticsearchClientSettings(new Uri(options.Url))
                .DefaultIndex(options.Index);

            if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.Password))
            {
                settings.Authentication(new Elastic.Transport.BasicAuthentication(options.Username, options.Password));
            }

            return new ElasticsearchClient(settings);
        });

        services.AddSingleton<ElasticsearchInitializer>();
        services.AddSingleton<ISearchService, ElasticsearchService>();

        return services;
    }
}
