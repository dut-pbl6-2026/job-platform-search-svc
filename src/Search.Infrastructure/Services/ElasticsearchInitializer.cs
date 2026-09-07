using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Models;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

public class ElasticsearchInitializer
{
    private readonly ElasticsearchClient _client;
    private readonly ElasticsearchOptions _options;
    private readonly ILogger<ElasticsearchInitializer> _logger;

    public ElasticsearchInitializer(
        ElasticsearchClient client,
        IOptions<ElasticsearchOptions> options,
        ILogger<ElasticsearchInitializer> logger)
    {
        _client = client;
        _options = options.Value;
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
        if (existsStatus == 200)
        {
            _logger.LogInformation("Elasticsearch index '{IndexName}' already exists.", indexName);
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
            .Mappings(m => m
                .Properties(p => p
                    .Keyword(k => k.Id)
                    .Text(t => t.Title, f => f.Fields(ff => ff.Keyword(k => k.Suffix("raw"))))
                    .Text(t => t.Description)
                    .Keyword(k => k.CompanyId!)
                    .Text(t => t.CompanyName, f => f.Fields(ff => ff.Keyword(k => k.Suffix("raw"))))
                    .Text(t => t.Location, f => f.Fields(ff => ff.Keyword(k => k.Suffix("raw"))))
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
                    .Text(t => t.Requirements!)
                    .Text(t => t.Benefits!)
                )
            ), cancellationToken);

        if (!createResponse.IsValidResponse)
        {
            _logger.LogError("Failed to create index '{IndexName}': {Reason}", indexName, createResponse.DebugInformation);
            throw new InvalidOperationException($"Failed to create Elasticsearch index '{indexName}': {createResponse.ElasticsearchServerError?.Error.Reason ?? createResponse.DebugInformation}");
        }

        _logger.LogInformation("Elasticsearch index '{IndexName}' created successfully.", indexName);
    }
}
