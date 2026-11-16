using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;
using Search.Infrastructure.Workers;
using System.Globalization;
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

        services.Configure<HybridSearchOptions>(o =>
        {
            o.Enabled = Bool(configuration["SEARCH_HYBRID_ENABLED"] ?? configuration["Search:Hybrid:Enabled"], true);
            o.KeywordWeight = Double(configuration["SEARCH_HYBRID_KEYWORD_WEIGHT"] ?? configuration["Search:Hybrid:KeywordWeight"], 0.4);
            o.VectorWeight = Double(configuration["SEARCH_HYBRID_VECTOR_WEIGHT"] ?? configuration["Search:Hybrid:VectorWeight"], 0.6);
            o.MinVectorScore = Double(configuration["SEARCH_HYBRID_MIN_VECTOR_SCORE"] ?? configuration["Search:Hybrid:MinVectorScore"], 0.72);
            o.CandidateMultiplier = Int(configuration["SEARCH_HYBRID_CANDIDATE_MULTIPLIER"] ?? configuration["Search:Hybrid:CandidateMultiplier"], 3);
            o.MaxCandidates = Int(configuration["SEARCH_HYBRID_MAX_CANDIDATES"] ?? configuration["Search:Hybrid:MaxCandidates"], 600);
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

        // Embeddings come from ai-svc's local model. Config is read here but validated per call
        // (AiEmbeddingClient fails closed), so the service still starts in dev without ai-svc.
        services.Configure<AiServiceOptions>(o =>
        {
            o.Url = configuration["AI_SERVICE_URL"] ?? configuration["AiService:Url"] ?? string.Empty;
            o.InternalToken = configuration["AI_INTERNAL_TOKEN_FOR_SEARCH"] ?? configuration["AiService:InternalToken"] ?? string.Empty;
        });
        services.AddHttpClient<IEmbeddingClient, AiEmbeddingClient>(c => c.Timeout = TimeSpan.FromSeconds(60));

        services.AddSingleton<ElasticsearchInitializer>();
        services.AddSingleton<ISearchService, ElasticsearchService>();

        // PBL6-19: Redis cache (5m TTL) with in-memory fallback for local/test.
        services.Configure<RedisOptions>(o =>
        {
            o.Url = configuration["REDIS_URL"] ?? configuration["Redis:Url"] ?? "";
            var ttlRaw = configuration["REDIS_TTL_SECONDS"] ?? configuration["Redis:TtlSeconds"];
            o.TtlSeconds = int.TryParse(ttlRaw, out var ttl) && ttl > 0 ? ttl : SearchCacheDefaults.DefaultTtlSeconds;
        });
        // PBL6-34 / B-11: single source of truth for Kafka transport options.
        // The local Search.Infrastructure.Configuration.KafkaOptions was removed;
        // the shared SharedKernel.Kafka.KafkaOptions carries bootstrap + SASL/TLS.
        // Topic/GroupId are consumer-specific and read from IConfiguration in
        // JobEventsConsumer (KAFKA_TOPIC_JOB_EVENTS / KAFKA_GROUP_ID).
        services.Configure<SharedKafkaOptions>(o =>
        {
            o.BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"]
                ?? configuration["Kafka:BootstrapServers"]
                ?? configuration["KAFKA_BOOTSTRAP"] ?? "";
            o.SaslUsername = configuration["KAFKA_SASL_USERNAME"]
                ?? configuration["Kafka:SaslUsername"] ?? "";
            o.SaslPassword = configuration["KAFKA_SASL_PASSWORD"]
                ?? configuration["Kafka:SaslPassword"] ?? "";
            o.SecurityProtocol = configuration["KAFKA_SECURITY_PROTOCOL"]
                ?? configuration["Kafka:SecurityProtocol"] ?? "";
            o.SaslMechanism = configuration["KAFKA_SASL_MECHANISM"]
                ?? configuration["Kafka:SaslMechanism"] ?? "Plain";
            o.SslCaLocation = configuration["KAFKA_SSL_CA_LOCATION"]
                ?? configuration["Kafka:SslCaLocation"] ?? "";
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

    // Env-first config: a malformed value falls back to the documented default instead of stopping the service;
    // a weight pair that is negative or all zero is rejected by HybridSearchOptions.Validate at construction.
    private static bool Bool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var value) ? value : fallback;

    private static double Double(string? raw, double fallback) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static int Int(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
