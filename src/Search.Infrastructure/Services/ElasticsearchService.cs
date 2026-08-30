using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using Search.Infrastructure.Configuration;

namespace Search.Infrastructure.Services;

public class ElasticsearchService : ISearchService
{
    private readonly ElasticsearchClient _client;
    private readonly ElasticsearchOptions _options;
    private readonly ILogger<ElasticsearchService> _logger;

    public ElasticsearchService(
        ElasticsearchClient client,
        IOptions<ElasticsearchOptions> options,
        ILogger<ElasticsearchService> logger)
    {
        _client = client;
        _options = options.Value;
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

        // 5. Employment Type filter
        if (!string.IsNullOrWhiteSpace(query.EmploymentType))
        {
            var empType = query.EmploymentType.Trim();
            filterClauses.Add(q => q.Term(t => t.Field(f => f.EmploymentType).Value(empType)));
        }

        // 6. Experience Level filter
        if (!string.IsNullOrWhiteSpace(query.ExperienceLevel))
        {
            var expLevel = query.ExperienceLevel.Trim();
            filterClauses.Add(q => q.Term(t => t.Field(f => f.ExperienceLevel!).Value(expLevel)));
        }

        // 7. Salary range filter (SRS 3.4.4)
        //    User specifies minSalary → job's SalaryMax >= minSalary (job can pay at least minSalary)
        //    User specifies maxSalary → job's SalaryMin <= maxSalary (job's min is within budget)
        if (query.MinSalary.HasValue)
        {
            filterClauses.Add(q => q
                .Range(new RangeQuery(
                    new NumberRangeQuery(Infer.Field<JobDocument>(f => f.SalaryMax!))
                    { Gte = (double)query.MinSalary.Value })));
        }
        if (query.MaxSalary.HasValue)
        {
            filterClauses.Add(q => q
                .Range(new RangeQuery(
                    new NumberRangeQuery(Infer.Field<JobDocument>(f => f.SalaryMin!))
                    { Lte = (double)query.MaxSalary.Value })));
        }

        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .From(query.From)
            .Size(query.NormalizedSize)
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

        var total = response.Total;
        var items = response.Documents.ToList();

        // SRS SEARCH-01-05: Handle empty results gracefully
        if (total == 0)
        {
            return SearchResult<JobDocument>.Empty(query.NormalizedPage, query.NormalizedSize);
        }

        return SearchResult<JobDocument>.Create(items, total, query.NormalizedPage, query.NormalizedSize);
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

    public virtual Task<bool> IndexJobAsync(JobDocument document, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public virtual Task<int> BulkIndexJobsAsync(IEnumerable<JobDocument> documents, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public virtual Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
