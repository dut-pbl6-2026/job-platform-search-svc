using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using SourceFilter = Elastic.Clients.Elasticsearch.Core.Search.SourceFilter;
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
    private readonly HybridSearchOptions _hybrid;
    private readonly IEmbeddingClient _embeddings;
    private readonly ILogger<ElasticsearchService> _logger;

    // kNN candidates per returned hit: higher is more accurate and slower (ES num_candidates).
    private const int SemanticCandidatesPerHit = 10;
    private const int SemanticMinCandidates = 100;

    public ElasticsearchService(
        ElasticsearchClient client,
        IOptions<ElasticsearchOptions> options,
        IEmbeddingClient embeddings,
        ILogger<ElasticsearchService> logger,
        IOptions<HybridSearchOptions>? hybrid = null)
    {
        _client = client;
        _options = options.Value;
        _hybrid = hybrid?.Value ?? new HybridSearchOptions();
        if (_hybrid.Enabled)
            _hybrid.Validate();
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task<SearchResult<JobDocument>> SearchJobsAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var keyword = string.IsNullOrWhiteSpace(query.Keyword) ? null : query.Keyword.Trim();
        var filters = BuildFilters(query);

        // VECTOR-01-03/04: hybrid ranking only replaces relevance ranking. An explicit sort means the
        // caller asked for an order (newest, salary), so vector scores would be ignored anyway.
        var hybrid = _hybrid.Enabled && keyword is not null && IsRelevanceSort(query.SortBy);

        return hybrid
            ? await HybridSearchAsync(query, keyword!, filters, cancellationToken)
            : await SingleSearchAsync(query, keyword, filters, cancellationToken);
    }

    /// <summary>BM25 (or a plain filter browse when there is no keyword). The pre-hybrid behaviour.</summary>
    private async Task<SearchResult<JobDocument>> SingleSearchAsync(
        SearchQuery query,
        string? keyword,
        IReadOnlyList<Query> filters,
        CancellationToken cancellationToken)
    {
        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .From(query.From)
            .Size(query.NormalizedSize)
            .Source(EmbeddingExcluded)
            .Query(q => q
                .Bool(b =>
                {
                    if (keyword is not null)
                        b.Must(m => m.MultiMatch(mm => KeywordFields(mm, keyword)));
                    else
                        b.Must(m => m.MatchAll(new MatchAllQuery()));

                    if (filters.Count > 0)
                        b.Filter(filters.ToArray());
                })
            )
            .Sort(sort => ApplySorting(sort, query)),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Elasticsearch search query failed: {DebugInfo}", response.DebugInformation);
            throw new InvalidOperationException($"Search failed: {response.ElasticsearchServerError?.Error.Reason ?? response.DebugInformation}");
        }

        var total = TotalOf(response);
        if (total == 0)
            return SearchResult<JobDocument>.Empty(query.NormalizedPage, query.NormalizedSize);

        var items = response.Documents.ToList();
        return SearchResult<JobDocument>.Create(items, total, query.NormalizedPage, query.NormalizedSize);
    }

    /// <summary>
    /// VECTOR-01-03/04/05: fetch a candidate pool from BM25 and from kNN, fuse the two rankings with
    /// the configured weights, then page the fused list. Filters are applied inside both retrievers,
    /// so a filtered page never narrows after fusion. Any embedding failure falls back to BM25 only.
    /// </summary>
    private async Task<SearchResult<JobDocument>> HybridSearchAsync(
        SearchQuery query,
        string keyword,
        IReadOnlyList<Query> filters,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var pool = HybridPoolSize(query);

        float[] vector;
        try
        {
            vector = (await _embeddings.EmbedAsync([keyword], cancellationToken))[0];
        }
        catch (EmbeddingUnavailableException ex)
        {
            // VECTOR-01-05: the embedding service is optional at query time; keyword search still answers.
            _logger.LogWarning("Hybrid search fell back to keyword only: {Reason}", ex.Message);
            return await SingleSearchAsync(query, keyword, filters, cancellationToken);
        }

        var keywordTask = KeywordCandidatesAsync(keyword, filters, pool, cancellationToken);
        var vectorTask = VectorCandidatesAsync(vector, filters, pool, cancellationToken);

        try
        {
            await Task.WhenAll(keywordTask, vectorTask);
        }
        catch
        {
            // Handled below: a broken keyword search is a real failure, a broken vector search is not.
        }

        var (keywordHits, keywordTotal) = await keywordTask;

        IReadOnlyList<ScoredJob> vectorHits;
        if (vectorTask.IsCompletedSuccessfully)
        {
            vectorHits = vectorTask.Result;
        }
        else
        {
            _logger.LogWarning(vectorTask.Exception, "Hybrid search fell back to keyword only: the kNN query failed.");
            vectorHits = Array.Empty<ScoredJob>();
        }

        // Vector scores re-rank keyword matches. They only *add* a job when the keyword side found
        // nothing at all and the semantic match clears the floor; otherwise every query would return
        // the whole index reordered (the MiniLM score of an unrelated job is only ~0.1 below a real hit).
        IReadOnlyList<ScoredJob> vectorForFusion;
        if (keywordHits.Count == 0)
        {
            vectorForFusion = vectorHits.Where(hit => hit.Score >= _hybrid.MinVectorScore).ToList();
        }
        else
        {
            var keywordIds = keywordHits.Select(hit => hit.Job.Id).ToHashSet(StringComparer.Ordinal);
            vectorForFusion = vectorHits.Where(hit => keywordIds.Contains(hit.Job.Id)).ToList();
        }

        var fused = HybridFusion.Fuse(keywordHits, vectorForFusion, _hybrid.KeywordWeight, _hybrid.VectorWeight);
        var items = fused
            .Skip(query.From)
            .Take(query.NormalizedSize)
            .Select(hit => hit.Job)
            .ToList();

        // Both retrievers exhausted means the union is the whole result set (exact). Otherwise the
        // fused total is only known for the candidate pool, and the BM25 total is the better estimate.
        var exhausted = keywordTotal <= pool && vectorHits.Count < pool;
        var total = exhausted ? fused.Count : Math.Max(keywordTotal, fused.Count);

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _logger.LogInformation(
            "Hybrid search: pool={Pool} keywordHits={KeywordHits} keywordTotal={KeywordTotal} vectorHits={VectorHits} admitted={Admitted} elapsedMs={ElapsedMs:F0}",
            pool, keywordHits.Count, keywordTotal, vectorHits.Count, vectorForFusion.Count, elapsedMs);

        if (total == 0)
            return SearchResult<JobDocument>.Empty(query.NormalizedPage, query.NormalizedSize);

        return SearchResult<JobDocument>.Create(items, total, query.NormalizedPage, query.NormalizedSize);
    }

    /// <summary>Top <paramref name="size"/> BM25 hits with their scores, plus the exact match total.</summary>
    private async Task<(IReadOnlyList<ScoredJob> Hits, long Total)> KeywordCandidatesAsync(
        string keyword,
        IReadOnlyList<Query> filters,
        int size,
        CancellationToken cancellationToken)
    {
        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .From(0)
            .Size(size)
            .Source(EmbeddingExcluded)
            .Query(q => q
                .Bool(b =>
                {
                    b.Must(m => m.MultiMatch(mm => KeywordFields(mm, keyword)));
                    if (filters.Count > 0)
                        b.Filter(filters.ToArray());
                })
            )
            .Sort(sort => sort
                .Score(sc => sc.Order(SortOrder.Desc))
                .Field(f => f.CreatedAt, sc => sc.Order(SortOrder.Desc))),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Elasticsearch hybrid keyword query failed: {DebugInfo}", response.DebugInformation);
            throw new InvalidOperationException($"Search failed: {response.ElasticsearchServerError?.Error.Reason ?? response.DebugInformation}");
        }

        var hits = response.Hits
            .Where(hit => hit.Source is not null)
            .Select(hit => new ScoredJob(hit.Source!, hit.Score ?? 0))
            .ToList();

        return (hits, TotalOf(response));
    }

    /// <summary>Top <paramref name="size"/> kNN hits with their cosine-based scores.</summary>
    private async Task<IReadOnlyList<ScoredJob>> VectorCandidatesAsync(
        float[] vector,
        IReadOnlyList<Query> filters,
        int size,
        CancellationToken cancellationToken)
    {
        var response = await _client.SearchAsync<JobDocument>(s => s
            .Index(_options.Index)
            .Size(size)
            .Source(EmbeddingExcluded)
            .Knn(k => k
                .Field("embedding")
                .QueryVector(vector)
                .k(size)
                .NumCandidates(Math.Max(SemanticMinCandidates, size * SemanticCandidatesPerHit))
                // Pre-filter: post-filtering would return fewer than k hits for a selective filter.
                .Filter(filters.ToArray())),
            cancellationToken);

        if (!response.IsValidResponse)
        {
            _logger.LogError("Elasticsearch kNN query failed: {DebugInfo}", response.DebugInformation);
            throw new InvalidOperationException($"Semantic search failed: {response.ElasticsearchServerError?.Error.Reason ?? response.DebugInformation}");
        }

        return response.Hits
            .Where(hit => hit.Source is not null)
            .Select(hit => new ScoredJob(hit.Source!, hit.Score ?? 0))
            .ToList();
    }

    private int HybridPoolSize(SearchQuery query)
    {
        var wanted = Math.Max(query.From + query.NormalizedSize, query.NormalizedSize * _hybrid.CandidateMultiplier);
        return Math.Min(wanted, _hybrid.MaxCandidates);
    }

    private static bool IsRelevanceSort(string? sortBy) =>
        string.IsNullOrWhiteSpace(sortBy)
        || string.Equals(sortBy.Trim(), "relevance", StringComparison.OrdinalIgnoreCase);

    /// <summary>The BM25 field weights and fuzziness shared by the plain and hybrid keyword queries.</summary>
    private static MultiMatchQueryDescriptor<JobDocument> KeywordFields(MultiMatchQueryDescriptor<JobDocument> mm, string keyword) => mm
        .Query(keyword)
        .Fields(new[]
        {
            Infer.Field<JobDocument>(f => f.Title, 2.0),
            Infer.Field<JobDocument>(f => f.CompanyName, 1.5),
            Infer.Field<JobDocument>(f => f.Description, 1.0),
            Infer.Field<JobDocument>(f => f.Requirements!, 0.8)
        })
        .Fuzziness(new Fuzziness("AUTO"))
        // Every word must appear: OR matching made a query like "công nghệ thông tin" hit every job
        // because "công" is in every description. Multi-concept phrases that match nothing fall back
        // to the vector side in HybridSearchAsync.
        .MinimumShouldMatch("100%");

    /// <summary>Filters shared by the BM25 and kNN retrievers (and the pre-hybrid single query).</summary>
    private static IReadOnlyList<Query> BuildFilters(SearchQuery query)
    {
        var filters = new List<Query>();

        // Location filter — MatchPhrase for exact phrase matching (SRS SEARCH-01-03)
        if (!string.IsNullOrWhiteSpace(query.Location))
        {
            filters.Add(new MatchPhraseQuery(Infer.Field<JobDocument>(f => f.Location))
            {
                Query = query.Location.Trim()
            });
        }

        // Status filter (Default: Active — matches backend JobStatus enum)
        filters.Add(new TermQuery(Infer.Field<JobDocument>(f => f.Status)) { Value = "Active" });

        // Category filter — use CategoryId (keyword) for exact match, not CategoryName (text)
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            filters.Add(new TermQuery(Infer.Field<JobDocument>(f => f.CategoryId!))
            {
                Value = query.Category.Trim()
            });
        }

        // Employment Type filter — CSV "FullTime,PartTime" means any of them (OR),
        // same semantics as the skills filter (api-conventions §4).
        var employmentTypes = query.EmploymentTypes;
        if (employmentTypes.Count > 0)
        {
            var values = employmentTypes.Select(v => (FieldValue)v).ToArray();
            filters.Add(new TermsQuery
            {
                Field = Infer.Field<JobDocument>(f => f.EmploymentType),
                Terms = new TermsQueryField(values)
            });
        }

        // Experience Level filter — CSV, OR semantics.
        var experienceLevels = query.ExperienceLevels;
        if (experienceLevels.Count > 0)
        {
            var values = experienceLevels.Select(v => (FieldValue)v).ToArray();
            filters.Add(new TermsQuery
            {
                Field = Infer.Field<JobDocument>(f => f.ExperienceLevel!),
                Terms = new TermsQueryField(values)
            });
        }

        // Salary range filter (SRS 3.4.4)
        //    Job has salary range [SalaryMin, SalaryMax].
        //    Overlap: user's [min,max] must intersect with job's [min,max].
        //    So: SalaryMax >= userMin AND SalaryMin <= userMax
        if (query.MinSalary.HasValue)
        {
            filters.Add(Query.Range(new RangeQuery(new NumberRangeQuery(Infer.Field<JobDocument>(f => f.SalaryMax!))
            {
                Gte = (double)query.MinSalary.Value
            })));
        }
        if (query.MaxSalary.HasValue)
        {
            filters.Add(Query.Range(new RangeQuery(new NumberRangeQuery(Infer.Field<JobDocument>(f => f.SalaryMin!))
            {
                Lte = (double)query.MaxSalary.Value
            })));
        }

        // Skills filter — skillOp=or (default): Terms, any requested skill matches.
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
                    filters.Add(new TermQuery(Infer.Field<JobDocument>(f => f.Skills))
                    {
                        Value = skill
                    });
                }
            }
            else
            {
                var values = skills.Select(s => (FieldValue)s).ToArray();
                filters.Add(new TermsQuery
                {
                    Field = Infer.Field<JobDocument>(f => f.Skills),
                    Terms = new TermsQueryField(values)
                });
            }
        }

        return filters;
    }

    private static readonly SourceConfig EmbeddingExcluded =
        new(new SourceFilter { Excludes = "embedding" });

    /// <summary>
    /// Elastic.Clients.Elasticsearch 8.13.0 does not populate SearchResponse.Total
    /// from the hits.total union (it reads 0 even when documents are returned).
    /// Read the true total from HitsMetadata.Total (TotalHits.Value), falling back
    /// to the page hit count so pagination stays correct.
    /// </summary>
    private static long TotalOf(SearchResponse<JobDocument> response) =>
        response.HitsMetadata?.Total is { } totalHits
            ? totalHits.Match(total => total.Value, lower => lower)
            : response.Hits.Count;
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
