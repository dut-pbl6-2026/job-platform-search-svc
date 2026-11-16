using System.Text;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Search.Core.Models;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;

namespace Search.Tests.Infrastructure;

/// <summary>
/// VECTOR-01-03/04/05 at the service level: two retrievers with the same filters, weighted fusion,
/// pagination over the fused list, and a keyword-only fallback when the embedding model is down.
/// </summary>
public class HybridSearchServiceTests
{
    private const string NoHits =
        """{"took":1,"timed_out":false,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0},"hits":{"total":{"value":0,"relation":"eq"},"max_score":null,"hits":[]}}""";

    private static string Hits(params (string Id, double Score)[] hits)
    {
        var payload = new
        {
            took = 1,
            timed_out = false,
            hits = new
            {
                total = new { value = hits.Length, relation = "eq" },
                max_score = hits.Length == 0 ? (double?)null : hits.Max(hit => hit.Score),
                hits = hits.Select(hit => new
                {
                    _index = "jobs",
                    _id = hit.Id,
                    _score = hit.Score,
                    _source = new { id = hit.Id, title = hit.Id, created_at = "2026-01-01T00:00:00Z" }
                }).ToArray()
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static (ElasticsearchService Service, ScriptedInvoker Invoker, FakeEmbeddingClient Embeddings) Build(
        Func<string, string> respond,
        HybridSearchOptions? hybrid = null)
    {
        var invoker = new ScriptedInvoker(respond);
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://localhost:9200")),
                invoker)
            .DisableDirectStreaming();
        var embeddings = new FakeEmbeddingClient();
        var service = new ElasticsearchService(
            new ElasticsearchClient(settings),
            Options.Create(new ElasticsearchOptions { Index = "jobs" }),
            embeddings,
            NullLogger<ElasticsearchService>.Instance,
            Options.Create(hybrid ?? new HybridSearchOptions()));
        return (service, invoker, embeddings);
    }

    private static JsonElement Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static bool HasKnn(string body) => Parse(body).TryGetProperty("knn", out _);

    [Fact]
    public async Task KeywordQuery_SendsBm25AndKnn_WithTheSameFilters()
    {
        var (service, invoker, embeddings) = Build(_ => NoHits);

        await service.SearchJobsAsync(new SearchQuery(Keyword: "Java", Location: "Hà Nội", Category: "it", Page: 0, Size: 20));

        embeddings.Batches.Should().HaveCount(1);
        invoker.Bodies.Should().HaveCount(2);

        var keywordBody = Parse(invoker.Bodies.Single(body => !HasKnn(body)));
        keywordBody.GetProperty("query").GetProperty("bool").GetProperty("must").GetRawText().Should().Contain("multi_match");
        keywordBody.TryGetProperty("from", out _).Should().BeTrue();

        var knnBody = Parse(invoker.Bodies.Single(HasKnn));
        knnBody.TryGetProperty("from", out _).Should().BeFalse();
        var filters = knnBody.GetProperty("knn").GetProperty("filter");
        var filterList = filters.ValueKind == JsonValueKind.Array ? filters.EnumerateArray().ToArray() : [filters];
        filterList.Any(f => f.TryGetProperty("term", out var term) && term.TryGetProperty("status", out _)).Should().BeTrue();
        filterList.Any(f => f.TryGetProperty("term", out var term)
                            && term.TryGetProperty("category_id", out var cat)
                            && cat.GetProperty("value").GetString() == "it").Should().BeTrue();
        filterList
            .Single(f => f.TryGetProperty("match_phrase", out _))
            .GetProperty("match_phrase").GetProperty("location").GetProperty("query").GetString()
            .Should().Be("Hà Nội");

        // The vector itself must never be returned to the caller.
        foreach (var body in invoker.Bodies)
        {
            var excludes = Parse(body).GetProperty("_source").GetProperty("excludes");
            if (excludes.ValueKind == JsonValueKind.Array)
                excludes.EnumerateArray().Select(value => value.GetString()).Should().Contain("embedding");
            else
                excludes.GetString().Should().Be("embedding");
        }
    }

    [Fact]
    public async Task EmbeddingFailure_FallsBackToKeywordOnly()
    {
        var (service, invoker, embeddings) = Build(_ => Hits(("a", 10)));
        embeddings.Fail = true;

        var result = await service.SearchJobsAsync(new SearchQuery(Keyword: "Java"));

        invoker.Bodies.Should().ContainSingle();
        HasKnn(invoker.Bodies[0]).Should().BeFalse();
        result.Items.Should().ContainSingle().Which.Id.Should().Be("a");
        result.Total.Should().Be(1);
    }

    [Fact]
    public async Task KnnFailure_FallsBackToKeywordOnly()
    {
        var (service, invoker, _) = Build(body =>
            HasKnn(body)
                ? """{"error":{"root_cause":[{"type":"query_shard_exception","reason":"no kNN"}],"type":"search_phase_execution_exception"},"status":400}"""
                : Hits(("a", 10)));

        var result = await service.SearchJobsAsync(new SearchQuery(Keyword: "Java"));

        invoker.Bodies.Should().HaveCount(2);
        result.Items.Should().ContainSingle().Which.Id.Should().Be("a");
    }

    [Fact]
    public async Task DisabledHybrid_SendsOneRequest()
    {
        var (service, invoker, embeddings) = Build(_ => Hits(("a", 10)), new HybridSearchOptions { Enabled = false });

        await service.SearchJobsAsync(new SearchQuery(Keyword: "Java"));

        embeddings.Batches.Should().BeEmpty();
        invoker.Bodies.Should().ContainSingle();
    }

    [Theory]
    [InlineData("created_at_desc")]
    [InlineData("salary_desc")]
    public async Task ExplicitSort_SkipsHybrid(string sortBy)
    {
        var (service, invoker, embeddings) = Build(_ => Hits(("a", 10)));

        await service.SearchJobsAsync(new SearchQuery(Keyword: "Java", SortBy: sortBy));

        embeddings.Batches.Should().BeEmpty();
        invoker.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task FusedOrderAndPaging_ComeFromBothRankings()
    {
        // BM25: a (10) > b (5). Vector: c (0.9) > b (0.7). Fused, default 40/60: c 0.6, a 0.4, b 0.
        var (service, _, _) = Build(body =>
            HasKnn(body) ? Hits(("c", 0.9), ("b", 0.7)) : Hits(("a", 10), ("b", 5)));

        var first = await service.SearchJobsAsync(new SearchQuery(Keyword: "Java", Page: 0, Size: 2));
        first.Items.Select(job => job.Id).Should().Equal("c", "a");
        first.Total.Should().Be(3);

        var second = await service.SearchJobsAsync(new SearchQuery(Keyword: "Java", Page: 1, Size: 2));
        second.Items.Select(job => job.Id).Should().Equal("b");
        second.Total.Should().Be(3);
    }

    [Fact]
    public async Task NoKeyword_UsesOneRequest_AndNoEmbedding()
    {
        var (service, invoker, embeddings) = Build(_ => Hits(("a", 1)));

        await service.SearchJobsAsync(new SearchQuery(Location: "Hà Nội"));

        embeddings.Batches.Should().BeEmpty();
        invoker.Bodies.Should().ContainSingle();
    }

    /// <summary>
    /// Returns a canned response per request body, so the BM25 and kNN calls can answer differently.
    /// </summary>
    private sealed class ScriptedInvoker : IRequestInvoker
    {
        private readonly Func<string, string> _respond;
        private readonly ResponseBuilder _responses = new();

        public ScriptedInvoker(Func<string, string> respond) => _respond = respond;

        public List<string> Bodies { get; } = [];

        public void Dispose()
        {
        }

        public TResponse Request<TResponse>(RequestData requestData) where TResponse : TransportResponse, new() =>
            RequestAsync<TResponse>(requestData, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<TResponse> RequestAsync<TResponse>(RequestData requestData, CancellationToken cancellationToken)
            where TResponse : TransportResponse, new()
        {
            using var buffer = new MemoryStream();
            requestData.PostData?.Write(buffer, requestData.ConnectionSettings);
            var body = Encoding.UTF8.GetString(buffer.ToArray());
            lock (Bodies)
                Bodies.Add(body);

            return await _responses.BuildAsync<TResponse>(
                requestData, cancellationToken, Encoding.UTF8.GetBytes(_respond(body)), 200);
        }

        private sealed class ResponseBuilder : InMemoryRequestInvoker
        {
            public Task<TResponse> BuildAsync<TResponse>(
                RequestData requestData,
                CancellationToken cancellationToken,
                byte[] body,
                int status)
                where TResponse : TransportResponse, new() =>
                BuildResponseAsync<TResponse>(requestData, cancellationToken, body, status, "application/json");
        }
    }
}
