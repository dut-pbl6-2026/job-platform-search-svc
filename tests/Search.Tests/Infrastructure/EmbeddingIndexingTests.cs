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

/// <summary>Write and kNN request shapes for the embedding path. Embeddings are faked; ES is an in-memory invoker.</summary>
public class EmbeddingIndexingTests
{
    private const string IndexResponse =
        """{"_index":"jobs","_id":"job-1","_version":1,"result":"created","_shards":{"total":1,"successful":1,"failed":0},"_seq_no":0,"_primary_term":1}""";

    private const string SearchResponse =
        """{"took":1,"timed_out":false,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0},"hits":{"total":{"value":1,"relation":"eq"},"max_score":0.9,"hits":[{"_index":"jobs","_id":"job-1","_score":0.9,"_source":{"id":"job-1","title":"Backend","description":"APIs","status":"Active","skills":["c#"]}}]}}""";

    private sealed class Capture
    {
        public int Requests { get; set; }
        public byte[]? Body { get; set; }
    }

    private static ElasticsearchService Service(FakeEmbeddingClient embeddings, string response, Capture capture)
    {
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://localhost:9200")),
                new InMemoryRequestInvoker(Encoding.UTF8.GetBytes(response)))
            .DisableDirectStreaming()
            .OnRequestCompleted(call =>
            {
                capture.Requests++;
                capture.Body = call.RequestBodyInBytes;
            });

        return new ElasticsearchService(
            new ElasticsearchClient(settings),
            Options.Create(new ElasticsearchOptions { Index = "jobs" }),
            embeddings,
            NullLogger<ElasticsearchService>.Instance);
    }

    private static JobDocument Job(string id, string title = "Backend Engineer") => new()
    {
        Id = id,
        Title = title,
        Description = "Build APIs",
        Skills = ["c#"],
        Status = "Active",
    };

    [Fact]
    public async Task IndexJobAsync_WritesTheEmbeddingWithTheDocument()
    {
        var embeddings = new FakeEmbeddingClient();
        var capture = new Capture();

        var ok = await Service(embeddings, IndexResponse, capture).IndexJobAsync(Job("job-1"));

        ok.Should().BeTrue();
        embeddings.Batches.Should().ContainSingle().Which.Should().Equal("Backend Engineer c# Build APIs");
        using var doc = JsonDocument.Parse(capture.Body!);
        doc.RootElement.GetProperty("id").GetString().Should().Be("job-1");
        doc.RootElement.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle())
            .Should().Equal(new[] { 30f, 1f, 0f });
    }

    [Fact]
    public async Task IndexJobAsync_EmbeddingDown_ReturnsFalseAndWritesNothing()
    {
        var capture = new Capture();
        var embeddings = new FakeEmbeddingClient { Fail = true };

        var ok = await Service(embeddings, IndexResponse, capture).IndexJobAsync(Job("job-1"));

        ok.Should().BeFalse();
        capture.Requests.Should().Be(0);
    }

    [Fact]
    public async Task BulkIndexJobsAsync_EmbeddingDown_IndexesNothing()
    {
        var capture = new Capture();
        var embeddings = new FakeEmbeddingClient { Fail = true };

        var count = await Service(embeddings, IndexResponse, capture).BulkIndexJobsAsync([Job("job-1"), Job("job-2")]);

        count.Should().Be(0);
        capture.Requests.Should().Be(0);
    }

    [Fact]
    public async Task BulkIndexJobsAsync_EveryActionLineIsFollowedByAnEmbeddedDocument()
    {
        var capture = new Capture();
        var embeddings = new FakeEmbeddingClient();
        const string bulkResponse =
            """{"took":1,"errors":false,"items":[{"index":{"_index":"jobs","_id":"job-1","_version":1,"result":"created","status":201,"_shards":{"total":1,"successful":1,"failed":0},"_seq_no":0,"_primary_term":1}},{"index":{"_index":"jobs","_id":"job-2","_version":1,"result":"created","status":201,"_shards":{"total":1,"successful":1,"failed":0},"_seq_no":1,"_primary_term":1}}]}""";

        var count = await Service(embeddings, bulkResponse, capture).BulkIndexJobsAsync([Job("job-1"), Job("job-2", "QA")]);

        count.Should().Be(2);
        embeddings.Batches.Should().ContainSingle().Which.Should().HaveCount(2);
        var lines = Encoding.UTF8.GetString(capture.Body!).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4);
        for (var i = 1; i < lines.Length; i += 2)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            doc.RootElement.GetProperty("embedding").GetArrayLength().Should().Be(3);
        }
    }

    [Fact]
    public async Task SearchSemanticAsync_SendsKnnPrefilteredToActiveAndExcludesVectorsFromSource()
    {
        var capture = new Capture();
        var embeddings = new FakeEmbeddingClient();

        var hits = await Service(embeddings, SearchResponse, capture).SearchSemanticAsync("backend apis", 5);

        embeddings.Batches.Should().ContainSingle().Which.Should().Equal("backend apis");
        hits.Should().ContainSingle().Which.Job.Id.Should().Be("job-1");
        hits[0].Score.Should().BeApproximately(0.9f, 0.001f);

        using var doc = JsonDocument.Parse(capture.Body!);
        var root = doc.RootElement;
        root.GetProperty("size").GetInt32().Should().Be(5);
        var knn = root.GetProperty("knn");
        var knnQuery = knn.ValueKind == JsonValueKind.Array ? knn[0] : knn;
        knnQuery.GetProperty("field").GetString().Should().Be("embedding");
        knnQuery.GetProperty("k").GetInt32().Should().Be(5);
        knnQuery.GetProperty("num_candidates").GetInt32().Should().Be(100);
        knnQuery.GetProperty("query_vector").EnumerateArray().Should().HaveCount(3);
        var filter = knnQuery.GetProperty("filter");
        var term = filter.ValueKind == JsonValueKind.Array ? filter[0] : filter;
        term.GetProperty("term").GetProperty("status").GetProperty("value").GetString().Should().Be("Active");
        root.GetProperty("_source").GetProperty("excludes").ToString().Should().Contain("embedding");
    }

    [Fact]
    public async Task SearchSemanticAsync_EmbeddingDown_ThrowsWithoutQueryingIndex()
    {
        var capture = new Capture();
        var embeddings = new FakeEmbeddingClient { Fail = true };

        var act = () => Service(embeddings, SearchResponse, capture).SearchSemanticAsync("backend", 5);

        await act.Should().ThrowAsync<Search.Core.Interfaces.EmbeddingUnavailableException>();
        capture.Requests.Should().Be(0);
    }
}
