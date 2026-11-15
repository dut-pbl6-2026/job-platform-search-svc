using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

public class ElasticsearchService : ISearchService
{
    private readonly ElasticsearchClient _client;
    private readonly ElasticsearchOptions _options;
    private readonly IEmbeddingClient _embeddings;
    private readonly ILogger<ElasticsearchService> _logger;

    // kNN candidates per returned hit: higher is more accurate and slower (ES num_candidates).
    private const int SemanticCandidatesPerHit = 10;
    private const int SemanticMinCandidates = 100;

    public ElasticsearchService(
        ElasticsearchClient client,
        IOptions<ElasticsearchOptions> options,
        IEmbeddingClient embeddings,
        ILogger<ElasticsearchService> logger)
    {
        _client = client;
        _options = options.Value;
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task<SearchResult<JobDocument>> SearchJobsAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var mustClauses = new List<Action<QueryDescriptor<JobDocument>>>();
        var filterClauses = new List<Action<QueryDescriptor<JobDocument>>>();

        // 1. Keyword search with relevance scoring (SRS SEARCH-01-02)
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            mustClauses.Add(q => q
                .MultiMatch(m => m
                    .Query(keyword)
                    .Fields(new[]
                    {
                        Infer.Field<JobDocument>(f => f.Title, 2.0),
                        Infer.Field<JobDocument>(f => f.CompanyName, 1.5),
                        Infer.Field<JobDocument>(f => f.Description, 1.0),
                        Infer.Field<JobDocument>(f => f.Requirements!, 0.8)
                    })
                    .Fuzziness(new Fuzziness("AUTO"))
                ));
        }

        // 2. Location filter — MatchPhrase for exact phrase matching (SRS SEARCH-01-03)
        if (!string.IsNullOrWhiteSpace(query.Location))
        {
            var location = query.Location.Trim();
            filterClauses.Add(q => q
                .MatchPhrase(m => m
                    .Field(f => f.Location)
                    .Query(location)
                ));
        }

        // 3. Status filter (Default: Active — matches backend JobStatus enum)
        filterClauses.Add(q => q.Term(t => t.Field(f => f.Status).Value("Active")));

        // 4. Category filter — use CategoryId (keyword) for exact match, not CategoryName (text)
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var cat = query.Category.Trim();
            filterClauses.Add(q => q.Term(t => t.Field(f => f.CategoryId!).Value(cat)));
        }

        // 5. Employment Type filter — CSV "FullTime,PartTime" means any of them (OR),
        //    same semantics as the skills filter (api-conventions §4).
        var employmentTypes = query.EmploymentTypes;
        if (employmentTypes.Count > 0)
        {
            var values = employmentTypes.Select(v => (FieldValue)v).ToArray();
            filterClauses.Add(q => q.Terms(t => t
                .Field(f => f.EmploymentType)
                .Terms(new TermsQueryField(values))));
        }

        // 6. Experience Level filter — CSV, OR semantics.
        var experienceLevels = query.ExperienceLevels;
        if (experienceLevels.Count > 0)
        {
            var values = experienceLevels.Select(v => (FieldValue)v).ToArray();
            filterClauses.Add(q => q.Terms(t => t
                .Field(f => f.ExperienceLevel!)
                .Terms(new TermsQueryField(values))));
        }

        // 7. Salary range filter (SRS 3.4.4)
        //    Job has salary range [SalaryMin, SalaryMax].
        //    Overlap: user's [min,max] must intersect with job's [min,max].
        //    So: SalaryMax >= userMin AND SalaryMin <= userMax
        if (query.MinSalary.HasValue)
        {
            filterClauses.Add(q => q.Range(new RangeQuery(
                new NumberRangeQuery(Infer.Field<JobDocument>(f => f.SalaryMax!))
                { Gte = (double)query.MinSalary.Value })));
        }
        if (query.MaxSalary.HasValue)
        {
            filterClauses.Add(q => q.Range(new RangeQuery(
                new NumberRangeQuery(Infer.Field<JobDocument>(f => f.SalaryMin!))
                { Lte = (double)query.MaxSalary.Value })));
        }

        // 8. Skills filter — skillOp=or (default): Terms, any requested skill matches.
        //    skillOp=and: one Term filter per skill, the job must have all of them.
        //    Field Skills is keyword with "lowercase_normalizer", so ES normalizes both
        //    index and query values; no manual .ToLowerInvariant() needed here.
        var skills = query.SkillList;
        if (skills.Count > 0)
        {
            if (query.MatchAllSkills)
            {
                foreach (var skill in skills)
                {
                    filterClauses.Add(q => q.Term(t => t.Field(f => f.Skills).Value(skill)));
                }
            }
            else
            {
                var values = skills.Select(s => (FieldValue)s).ToArray();
                filterClauses.Add(q => q.Terms(t => t
                    .Field(f => f.Skills)
                    .Terms(new TermsQueryField(values))));
            }
        }

        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .From(query.From)
            .Size(query.NormalizedSize)
            .Source(new SourceConfig(new SourceFilter { Excludes = "embedding" }))
            .Query(q => q
                .Bool(b =>
                {
                    if (mustClauses.Count > 0)
                        b.Must(mustClauses.ToArray());
                    else
                        b.Must(m => m.MatchAll(new MatchAllQuery()));

                    if (filterClauses.Count > 0)
                        b.Filter(filterClauses.ToArray());
                })
            )
            .Sort(sort => ApplySorting(sort, query)),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Elasticsearch search query failed: {DebugInfo}", response.DebugInformation);
            throw new InvalidOperationException($"Search failed: {response.ElasticsearchServerError?.Error.Reason ?? response.DebugInformation}");
        }

        // Elastic.Clients.Elasticsearch 8.13.0 does not populate SearchResponse.Total
        // from the hits.total union (it reads 0 even when documents are returned).
        // Read the true total from HitsMetadata.Total (TotalHits.Value), falling back
        // to the page hit count so pagination stays correct.
        var total = response.HitsMetadata?.Total is { } totalHits
            ? totalHits.Match(t => t.Value, l => l)
            : response.Hits.Count;

        // SRS SEARCH-01-05: Handle empty results gracefully (before materializing).
        if (total == 0)
        {
            return SearchResult<JobDocument>.Empty(query.NormalizedPage, query.NormalizedSize);
        }

        var items = response.Documents.ToList();
        return SearchResult<JobDocument>.Create(items, total, query.NormalizedPage, query.NormalizedSize);
    }

    public async Task<IReadOnlyList<SemanticJobHit>> SearchSemanticAsync(string text, int size, CancellationToken cancellationToken = default)
    {
        var vector = (await _embeddings.EmbedAsync([text], cancellationToken))[0];

        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .Size(size)
            .Knn(k => k
                .Field("embedding")
                .QueryVector(vector)
                .k(size)
                .NumCandidates(Math.Max(SemanticMinCandidates, size * SemanticCandidatesPerHit))
                // Pre-filter: post-filtering would return fewer than k hits for a selective filter.
                .Filter([new TermQuery(Infer.Field<JobDocument>(f => f.Status)) { Value = "Active" }])
            )
            .Source(new SourceConfig(new SourceFilter { Excludes = "embedding" })),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Elasticsearch kNN query failed: {DebugInfo}", response.DebugInformation);
            throw new InvalidOperationException($"Semantic search failed: {response.ElasticsearchServerError?.Error.Reason ?? response.DebugInformation}");
        }

        return response.Hits
            .Where(h => h.Source is not null)
            .Select(h => new SemanticJobHit(h.Source!, (float)(h.Score ?? 0)))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return Array.Empty<string>();

        var normalizedLimit = Math.Clamp(limit, 1, 20);
        var trimmed = prefix.Trim();

        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .Size(normalizedLimit)
            .Query(q => q
                .Bool(b => b
                    .Should(
                        sh => sh.MatchPhrasePrefix(m => m.Field(f => f.Title).Query(trimmed)),
                        sh => sh.MatchPhrasePrefix(m => m.Field(f => f.CompanyName).Query(trimmed))
                    )
                    // Required: with a Filter clause and no Must, Should would
                    // otherwise be optional and match Active docs with no prefix hit.
                    .MinimumShouldMatch(1)
                    .Filter(f => f.Term(t => t.Field(fld => fld.Status).Value("Active")))
                )
            ),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogWarning("Suggest query failed: {DebugInfo}", response.DebugInformation);
            return Array.Empty<string>();
        }

        var suggestions = response.Documents
            .Select(d => d.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(normalizedLimit)
            .ToList();

        return suggestions;
    }

    private Refresh WriteRefresh => _options.RefreshOnWrite ? Refresh.WaitFor : Refresh.False;

    /// <summary>
    /// Documents with their embedding. Called after the merge, so the text includes the skills that
    /// Kafka events do not carry. Null when the embedding service fails: nothing is written without a vector.
    /// </summary>
    private async Task<JsonNode[]?> ToIndexBodiesAsync(IReadOnlyList<JobDocument> documents, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await _embeddings.EmbedAsync(documents.Select(JobEmbeddingText.From).ToArray(), cancellationToken);
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogError(ex, "Embedding unavailable; {Count} job(s) not indexed.", documents.Count);
            return null;
        }

        return documents
            .Select((doc, i) =>
            {
                var body = JsonSerializer.SerializeToNode(doc)!.AsObject();
                body["embedding"] = JsonSerializer.SerializeToNode(vectors[i]);
                return (JsonNode)body;
            })
            .ToArray();
    }

    private static SortOptionsDescriptor<JobDocument> ApplySorting(SortOptionsDescriptor<JobDocument> sort, SearchQuery query)
    {
        return query.SortBy?.ToLowerInvariant() switch
        {
            "created_at_desc" => sort.Field(f => f.CreatedAt, s => s.Order(SortOrder.Desc)),
            "created_at_asc" => sort.Field(f => f.CreatedAt, s => s.Order(SortOrder.Asc)),
            "salary_desc" => sort.Field(f => f.SalaryMax!, s => s.Order(SortOrder.Desc)),
            "salary_asc" => sort.Field(f => f.SalaryMin!, s => s.Order(SortOrder.Asc)),
            _ => string.IsNullOrWhiteSpace(query.Keyword)
                ? sort.Field(f => f.CreatedAt, s => s.Order(SortOrder.Desc))
                : sort.Score(s => s.Order(SortOrder.Desc)).Field(f => f.CreatedAt, s => s.Order(SortOrder.Desc))
        };
    }

    public virtual async Task<JobDocument?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;

        var response = await _client.GetAsync<JobDocument>(_options.Index, jobId, cancellationToken);
        if (response.Found)
            return response.Source;

        // Missing document (or index not created yet) is a normal "no previous version".
        if (response.IsValidResponse || response.ApiCallDetails?.HttpStatusCode == 404)
            return null;

        _logger.LogError("Failed to read job '{JobId}': {DebugInfo}", jobId, response.DebugInformation);
        throw new InvalidOperationException($"Failed to read job '{jobId}' from the search index.");
    }

    public virtual async Task<bool> IndexJobAsync(JobDocument document, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(document.Id))
            throw new ArgumentException("JobDocument.Id must not be empty.", nameof(document));

        var bodies = await ToIndexBodiesAsync([document], cancellationToken);
        if (bodies is null)
            return false;

        var response = await _client.IndexAsync(bodies[0], _options.Index, idx => idx
            .Id(document.Id)
            .Refresh(WriteRefresh),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Failed to index job '{JobId}': {DebugInfo}", document.Id, response.DebugInformation);
            return false;
        }

        _logger.LogInformation("Successfully indexed job '{JobId}'.", document.Id);
        return true;
    }

    public virtual async Task<int> BulkIndexJobsAsync(IEnumerable<JobDocument> documents, CancellationToken cancellationToken = default)
    {
        var docList = documents.ToList();
        if (docList.Count == 0)
            return 0;

        var bodies = await ToIndexBodiesAsync(docList, cancellationToken);
        if (bodies is null)
            return 0;

        var response = await _client.BulkAsync(b => b
            .Index(_options.Index)
            .IndexMany(bodies, (descriptor, body) => descriptor.Id(body["id"]!.GetValue<string>()))
            .Refresh(WriteRefresh),
            cancellationToken);

        if (!response.IsValidResponse || response.Errors)
        {
            _logger.LogWarning("Bulk index completed with some errors: {DebugInfo}", response.DebugInformation);
            var successCount = response.Items.Count(item => item.IsValid);
            return successCount;
        }

        _logger.LogInformation("Successfully bulk indexed {Count} jobs.", docList.Count);
        return docList.Count;
    }

    public virtual async Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("JobId must not be empty.", nameof(jobId));

        var response = await _client.DeleteAsync(_options.Index, jobId, d => d
            .Refresh(WriteRefresh),
            cancellationToken);

        if (!response.IsValidResponse && response.ApiCallDetails?.HttpStatusCode != 404)
        {
            _logger.LogError("Failed to delete job '{JobId}': {DebugInfo}", jobId, response.DebugInformation);
            return false;
        }

        _logger.LogInformation("Successfully deleted job '{JobId}' from index.", jobId);
        return true;
    }
}
