using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

public class ElasticsearchInitializer
{
    private readonly ElasticsearchClient _client;
    private readonly ElasticsearchOptions _options;
    private readonly IEmbeddingClient _embeddings;
    private readonly ILogger<ElasticsearchInitializer> _logger;

    public ElasticsearchInitializer(
        ElasticsearchClient client,
        IOptions<ElasticsearchOptions> options,
        IEmbeddingClient embeddings,
        ILogger<ElasticsearchInitializer> logger)
    {
        _client = client;
        _options = options.Value;
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task EnsureIndexCreatedAsync(CancellationToken cancellationToken = default)
    {
        var indexName = _options.Index;
        _logger.LogInformation("Checking if Elasticsearch index '{IndexName}' exists...", indexName);

        // ExistsResponse has no Exists flag in client 8.13: discriminate by HTTP
        // status. IsValidResponse alone conflates "index missing" (404, expected)
        // with infra errors (network/auth) — the latter must fail loudly instead
        // of attempting a create with a misleading error (review B-3).
        var existsResponse = await _client.Indices.ExistsAsync(indexName, cancellationToken);
        var existsStatus = existsResponse.ApiCallDetails?.HttpStatusCode;
        // The vector size comes from the running model, never from config: a mismatch makes every write fail.
        var model = await _embeddings.GetModelInfoAsync(cancellationToken);

        if (existsStatus == 200)
        {
            _logger.LogInformation("Elasticsearch index '{IndexName}' already exists.", indexName);
            await VerifyEmbeddingDimensionAsync(indexName, model.Dimension, cancellationToken);
            return;
        }

        if (existsStatus != 404)
        {
            throw new InvalidOperationException(
                $"Failed to check Elasticsearch index '{indexName}': unexpected status {(int?)existsStatus}. " +
                $"{existsResponse.DebugInformation}");
        }

        _logger.LogInformation("Creating Elasticsearch index '{IndexName}' with mappings...", indexName);

        var createResponse = await _client.Indices.CreateAsync<JobDocument>(indexName, c => c
            .Settings(s => s
                .Analysis(a => a
                    // Vietnamese analyzer: icu_tokenizer + icu_folding (accent/case folding)
                    // so typing without diacritics ("ky su") matches accented docs ("Kỹ sư").
                    // Single folding filter — icu_folding already does NFKC + case folding,
                    // an explicit icu_normalizer would be redundant.
                    .Analyzers(an => an
                        .Custom("vietnamese_icu", ca => ca
                            .Tokenizer("icu_tokenizer")
                            .Filter(new[] { "vietnamese_icu_fold" }))
                    )
                    .TokenFilters(tf => tf
                        .IcuFolding("vietnamese_icu_fold", _ => { })
                    )
                    // Lowercase normalizer for the Skills keyword field: makes term/terms
                    // matching case-insensitive (producer may send "React"/"C#" while the
                    // query sends lowercase). ES applies it on both index and query time.
                    .Normalizers(n => n
                        .Custom("lowercase_normalizer", cn => cn
                            .Filter(new[] { "lowercase" }))
                    )
                )
            )
            .Mappings(m => m
                .Properties(p => p
                    .Keyword(k => k.Id)
                    .Text(t => t.Title, f => f
                        .Analyzer("vietnamese_icu")
                        .Fields(ff => ff.Keyword(k => k.Suffix("raw"))))
                    .Text(t => t.Description, f => f.Analyzer("vietnamese_icu"))
                    .Keyword(k => k.CompanyId!)
                    .Text(t => t.CompanyName, f => f
                        .Analyzer("vietnamese_icu")
                        .Fields(ff => ff.Keyword(k => k.Suffix("raw"))))
                    .Text(t => t.Location, f => f
                        .Analyzer("vietnamese_icu")
                        .Fields(ff => ff.Keyword(k => k.Suffix("raw"))))
                    // Salaries are decimal? in C#: DoubleNumber preserves fractions.
                    // LongNumber would silently truncate/error on values like 1500000.50 (review B-5).
                    // NOTE: changing an existing index mapping requires recreation —
                    // this mapping only applies when the index is created.
                    .DoubleNumber(n => n.SalaryMin!)
                    .DoubleNumber(n => n.SalaryMax!)
                    .Keyword(k => k.Currency)
                    .Keyword(k => k.CategoryId!)
                    .Keyword(k => k.CategoryName!)
                    .Keyword(k => k.EmploymentType)
                    .Keyword(k => k.ExperienceLevel!)
                    .Keyword(k => k.Status)
                    .Keyword(k => k.RecruiterId!)
                    .Date(d => d.CreatedAt)
                    .Date(d => d.UpdatedAt)
                    .Date(d => d.ExpiresAt!)
                    .Text(t => t.Requirements!, f => f.Analyzer("vietnamese_icu"))
                    .Text(t => t.Benefits!, f => f.Analyzer("vietnamese_icu"))
                    .Keyword(k => k.Skills, k => k.Normalizer("lowercase_normalizer"))
                    // Cosine over unit-length vectors (ai-svc normalises). Filled from the embedding service, not the document model.
                    .DenseVector("embedding", d => d
                        .Dims(model.Dimension)
                        .Index(true)
                        .Similarity("cosine"))
                )
            ), cancellationToken);

        if (!createResponse.IsValidResponse)
        {
            _logger.LogError("Failed to create index '{IndexName}': {Reason}", indexName, createResponse.DebugInformation);
            throw new InvalidOperationException($"Failed to create Elasticsearch index '{indexName}': {createResponse.ElasticsearchServerError?.Error.Reason ?? createResponse.DebugInformation}");
        }

        _logger.LogInformation(
            "Elasticsearch index '{IndexName}' created successfully (embedding model '{Model}', {Dimension} dims).",
            indexName, model.Model, model.Dimension);
    }

    private async Task VerifyEmbeddingDimensionAsync(string indexName, int expectedDims, CancellationToken cancellationToken)
    {
        var mapping = await _client.Indices.GetMappingAsync(r => r.Indices(indexName), cancellationToken);
        if (!mapping.IsValidResponse)
        {
            throw new InvalidOperationException(
                $"Failed to read the mapping of '{indexName}': {mapping.DebugInformation}");
        }

        var properties = mapping.Indices.Values.FirstOrDefault()?.Mappings.Properties;
        int? dims = null;
        if (properties is not null
            && ((IDictionary<PropertyName, IProperty>)properties).TryGetValue("embedding", out var property)
            && property is DenseVectorProperty vector)
        {
            dims = vector.Dims;
        }

        if (dims != expectedDims)
        {
            throw new InvalidOperationException(
                $"Index '{indexName}' has no dense_vector 'embedding' of {expectedDims} dims. " +
                "The mapping cannot change in place: create a new index name (ELASTICSEARCH_INDEX) and reindex.");
        }
    }
}
