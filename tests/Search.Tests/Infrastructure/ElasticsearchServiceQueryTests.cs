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
/// Asserts the query DSL that <see cref="ElasticsearchService"/> sends, so swapping the AND/OR
/// branches or dropping the CSV split fails a test instead of only failing against a real cluster.
/// </summary>
public class ElasticsearchServiceQueryTests
{
    private const string EmptySearchResponse =
        """{"took":1,"timed_out":false,"_shards":{"total":1,"successful":1,"skipped":0,"failed":0},"hits":{"total":{"value":0,"relation":"eq"},"max_score":null,"hits":[]}}""";

    private static async Task<JsonElement> SentFilters(SearchQuery query)
    {
        byte[]? body = null;
        var settings = new ElasticsearchClientSettings(
                new SingleNodePool(new Uri("http://localhost:9200")),
                new InMemoryRequestInvoker(Encoding.UTF8.GetBytes(EmptySearchResponse)))
            .DisableDirectStreaming()
            .OnRequestCompleted(call => body = call.RequestBodyInBytes);
        var service = new ElasticsearchService(
            new ElasticsearchClient(settings),
            Options.Create(new ElasticsearchOptions { Index = "jobs" }),
            NullLogger<ElasticsearchService>.Instance);

        await service.SearchJobsAsync(query);

        body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(body!);
        return doc.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter").Clone();
    }

    // The client writes a single filter clause as an object and several as an array.
    private static IEnumerable<JsonElement> Clauses(JsonElement filters, string kind) =>
        (filters.ValueKind == JsonValueKind.Array ? filters.EnumerateArray().ToArray() : [filters])
            .Where(f => f.TryGetProperty(kind, out _))
            .Select(f => f.GetProperty(kind));

    private static string[] TermsValues(JsonElement filters, string field) =>
        Clauses(filters, "terms")
            .Where(t => t.TryGetProperty(field, out _))
            .SelectMany(t => t.GetProperty(field).EnumerateArray().Select(v => v.GetString()!))
            .ToArray();

    private static string[] TermValues(JsonElement filters, string field) =>
        Clauses(filters, "term")
            .Where(t => t.TryGetProperty(field, out _))
            .Select(t => t.GetProperty(field) is { ValueKind: JsonValueKind.Object } o
                ? o.GetProperty("value").GetString()!
                : t.GetProperty(field).GetString()!)
            .ToArray();

    [Fact]
    public async Task EmploymentType_Csv_IsOneTermsClauseWithEveryValue()
    {
        var filters = await SentFilters(new SearchQuery(EmploymentType: "fulltime,PartTime"));

        TermsValues(filters, "employment_type").Should().Equal("FullTime", "PartTime");
        TermValues(filters, "employment_type").Should().BeEmpty();
    }

    [Fact]
    public async Task ExperienceLevel_Csv_IsOneTermsClauseWithEveryValue()
    {
        var filters = await SentFilters(new SearchQuery(ExperienceLevel: "Mid,Senior"));

        TermsValues(filters, "experience_level").Should().Equal("Mid", "Senior");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("or")]
    public async Task Skills_Or_IsOneTermsClause(string? skillOp)
    {
        var filters = await SentFilters(new SearchQuery(Skills: "C#,React", SkillOp: skillOp));

        TermsValues(filters, "skills").Should().Equal("C#", "React");
        TermValues(filters, "skills").Should().BeEmpty();
    }

    [Fact]
    public async Task Skills_And_IsOneTermClausePerSkill()
    {
        var filters = await SentFilters(new SearchQuery(Skills: "C#,React", SkillOp: "and"));

        TermValues(filters, "skills").Should().Equal("C#", "React");
        TermsValues(filters, "skills").Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyActiveJobs_AreSearched()
    {
        var filters = await SentFilters(new SearchQuery());

        TermValues(filters, "status").Should().Equal("Active");
    }
}
