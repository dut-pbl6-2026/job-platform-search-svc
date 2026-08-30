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

        var existsResponse = await _client.Indices.ExistsAsync(indexName, cancellationToken);
        if (existsResponse.IsValidResponse)
        {
            _logger.LogInformation("Elasticsearch index '{IndexName}' already exists.", indexName);
            return;
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
