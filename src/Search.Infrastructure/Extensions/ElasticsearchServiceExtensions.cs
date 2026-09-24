using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;
using Search.Infrastructure.Workers;
using SharedKafkaOptions = SharedKernel.Kafka.KafkaOptions;

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
            var refreshRaw = configuration["ELASTICSEARCH_REFRESH_ON_WRITE"] ?? configuration["Elasticsearch:RefreshOnWrite"];
            o.RefreshOnWrite = !string.Equals(refreshRaw, "false", StringComparison.OrdinalIgnoreCase);
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

        // PBL6-19: Redis cache (5m TTL) with in-memory fallback for local/test.
        services.Configure<RedisOptions>(o =>
        {
            o.Url = configuration["REDIS_URL"] ?? configuration["Redis:Url"] ?? "";
            var ttlRaw = configuration["REDIS_TTL_SECONDS"] ?? configuration["Redis:TtlSeconds"];
            o.TtlSeconds = int.TryParse(ttlRaw, out var ttl) && ttl > 0 ? ttl : SearchCacheDefaults.DefaultTtlSeconds;
        });
        services.Configure<KafkaOptions>(o =>
        {
            o.BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"]
                ?? configuration["Kafka:BootstrapServers"]
                ?? configuration["KAFKA_BOOTSTRAP"] ?? "";
            o.Topic = configuration["KAFKA_TOPIC"] ?? configuration["Kafka:Topic"] ?? "job-events";
            o.GroupId = configuration["KAFKA_GROUP_ID"] ?? configuration["Kafka:GroupId"] ?? "search-svc";
        });

        // PBL6-34: shared transport options for KafkaConsumerService base
        // (Topic/GroupId stay on the local options above; the base only needs bootstrap/SASL).
        services.Configure<SharedKafkaOptions>(o =>
        {
            o.BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"]
                ?? configuration["Kafka:BootstrapServers"]
                ?? configuration["KAFKA_BOOTSTRAP"] ?? "";
        });

        var redisUrl = configuration["REDIS_URL"] ?? configuration["Redis:Url"];
        if (!string.IsNullOrWhiteSpace(redisUrl))
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redisUrl;
                o.InstanceName = "search:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddSingleton<ISearchCache, RedisSearchCache>();
        // Singleton + hosted so the concrete consumer is resolvable in handlers/tests.
        services.AddSingleton<JobEventsConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<JobEventsConsumer>());

        return services;
    }
}
