using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Search.Api.Endpoints;
using Search.Tests.Integration;

namespace Search.Tests.Contract;

/// <summary>
/// Wire-format contract: JSON field names, JSON value kinds and envelopes for every route, as seen
/// by a client. Asserted on raw JSON (not on the DTO types) so a renamed property fails here.
/// </summary>
public class ApiContractTests : IClassFixture<SearchApiFactory>
{
    private const string IndexToken = SearchApiFactory.IndexToken;

    private readonly SearchApiFactory _factory;

    public ApiContractTests(SearchApiFactory factory)
    {
        _factory = factory;
        _factory.Es.Reset();
    }

    [Fact]
    public async Task IndexRequest_AcceptsCamelCaseFieldsWithTheirJsonTypes()
    {
        // Request shape used by job-svc: camelCase names, numbers as JSON numbers, arrays as arrays.
        var client = _factory.CreateClient();
        var body = """
            {"id":"job-c-1","title":"Backend","description":"Desc","companyName":"Acme","location":"Ha Noi",
             "salaryMin":1000000.5,"salaryMax":2000000,"employmentType":"PartTime","skills":["C#","SQL"],
             "createdAt":"2026-01-02T03:04:05Z"}
            """;

        var response = await PostRawAsync(client, "/api/search/index", body, IndexToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = StoredDocument("job-c-1");
        stored.GetProperty("salary_min").GetDecimal().Should().Be(1000000.5m);
        stored.GetProperty("employment_type").GetString().Should().Be("PartTime");
        stored.GetProperty("skills").EnumerateArray().Select(s => s.GetString()).Should().Equal("C#", "SQL");
        stored.GetProperty("created_at").GetDateTime().Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    [Fact]
    public async Task IndexRequest_WrongJsonTypeForNumber_IsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await PostRawAsync(client, "/api/search/index",
            """{"id":"job-c-2","title":"Backend","description":"Desc","salaryMin":"lots"}""", IndexToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Es.Documents.Should().NotContainKey("job-c-2");
    }

    [Fact]
    public async Task IndexedDocument_UsesSnakeCaseSchemaInElasticsearch()
    {
        var client = _factory.CreateClient();
        await PostRawAsync(client, "/api/search/index",
            """{"id":"job-c-3","title":"Schema","description":"Desc","companyName":"Acme","location":"Da Nang","skills":["React"]}""",
            IndexToken);

        var stored = StoredDocument("job-c-3");
        foreach (var field in new[]
                 {
                     "id", "title", "description", "company_name", "location", "salary_min", "salary_max",
                     "currency", "employment_type", "status", "created_at", "updated_at", "skills",
                 })
        {
            stored.TryGetProperty(field, out _).Should().BeTrue($"document must carry '{field}'");
        }

        stored.TryGetProperty("companyName", out _).Should().BeFalse("ES documents use snake_case names");
    }

    [Fact]
    public async Task IndexResponse_HasMessageAndId()
    {
        var client = _factory.CreateClient();

        var json = await PostJsonAsync(client, "/api/search/index",
            """{"id":"job-c-4","title":"Ok","description":"Desc"}""", IndexToken);

        json.GetProperty("message").ValueKind.Should().Be(JsonValueKind.String);
        json.GetProperty("id").GetString().Should().Be("job-c-4");
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["message", "id"]);
    }

    [Fact]
    public async Task BulkResponse_HasCountsAndMessage()
    {
        var client = _factory.CreateClient();
        var body = """[{"id":"job-c-5","title":"A","description":"D"},{"id":"job-c-6","title":"B","description":"D"}]""";

        var json = await PostJsonAsync(client, "/api/search/bulk-index", body, IndexToken);

        json.GetProperty("totalRequested").ValueKind.Should().Be(JsonValueKind.Number);
        json.GetProperty("totalIndexed").ValueKind.Should().Be(JsonValueKind.Number);
        json.GetProperty("message").ValueKind.Should().Be(JsonValueKind.String);
        json.GetProperty("totalRequested").GetInt32().Should().Be(2);
        json.GetProperty("totalIndexed").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task SearchResponse_EnvelopeAndItemFieldsMatchTheDocumentedShape()
    {
        var client = _factory.CreateClient();
        await PostRawAsync(client, "/api/search/index",
            """{"id":"job-c-7","title":"Envelope","description":"Desc","companyName":"Acme","location":"Ha Noi","recruiterId":"11111111-1111-1111-1111-111111111111"}""",
            IndexToken);

        // The index keeps the recruiter for internal paths; the public response must not.
        StoredDocument("job-c-7").GetProperty("recruiter_id").GetString()
            .Should().Be("11111111-1111-1111-1111-111111111111");

        var json = await GetJsonAsync(client, "/api/search/jobs?q=envelope");

        json.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(["items", "total", "page", "size", "totalPages", "message"]);
        json.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        json.GetProperty("total").ValueKind.Should().Be(JsonValueKind.Number);
        json.GetProperty("page").ValueKind.Should().Be(JsonValueKind.Number);
        json.GetProperty("size").ValueKind.Should().Be(JsonValueKind.Number);
        json.GetProperty("totalPages").ValueKind.Should().Be(JsonValueKind.Number);

        var item = json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "job-c-7");
        item.GetProperty("title").ValueKind.Should().Be(JsonValueKind.String);
        item.GetProperty("companyName").GetString().Should().Be("Acme");
        item.GetProperty("salaryMin").ValueKind.Should().BeOneOf(JsonValueKind.Null, JsonValueKind.Number);
        item.GetProperty("skills").ValueKind.Should().Be(JsonValueKind.Array);
        item.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.String);
        item.TryGetProperty("company_name", out _).Should().BeFalse("API responses use camelCase names");
        item.TryGetProperty("recruiterId", out _).Should().BeFalse("anonymous search must not expose internal user ids");
    }

    [Fact]
    public async Task SemanticResponse_IsItemsOfScoreAndJob()
    {
        var client = _factory.CreateClient();
        await PostRawAsync(client, "/api/search/index",
            """{"id":"job-c-8","title":"Semantic","description":"Desc"}""", IndexToken);

        var json = await GetJsonAsync(client, "/api/search/jobs/semantic?q=contract-probe");

        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["items"]);
        var hit = json.GetProperty("items").EnumerateArray().First();
        hit.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["score", "job"]);
        hit.GetProperty("score").ValueKind.Should().Be(JsonValueKind.Number);
        var job = hit.GetProperty("job");
        job.GetProperty("id").GetString().Should().Be("job-c-8");
        job.EnumerateObject().Select(p => p.Name).Should().Contain(["companyName", "employmentType", "createdAt", "skills"]);
        job.TryGetProperty("company_name", out _).Should().BeFalse("API responses use camelCase names");
        job.TryGetProperty("recruiterId", out _).Should().BeFalse("anonymous search must not expose internal user ids");
    }

    [Fact]
    public async Task ErrorResponses_UseTheDocumentedBodies()
    {
        var client = _factory.CreateClient();

        var unauthorized = await PostRawAsync(client, "/api/search/index", "{}", token: null);
        var unauthorizedJson = await unauthorized.Content.ReadFromJsonAsync<JsonElement>();
        unauthorizedJson.GetProperty("message").ValueKind.Should().Be(JsonValueKind.String);

        var invalid = await PostRawAsync(client, "/api/search/index", """{"id":"","title":"","description":""}""", IndexToken);
        var invalidJson = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        invalidJson.GetProperty("status").GetInt32().Should().Be(400);
        invalidJson.GetProperty("title").ValueKind.Should().Be(JsonValueKind.String);
        invalidJson.GetProperty("detail").ValueKind.Should().Be(JsonValueKind.String);

        var tooLarge = await PostRawAsync(client, "/api/search/index",
            "{\"id\":\"x\",\"title\":\"x\",\"description\":\"" + new string('x', (int)IndexEndpoints.MaxIndexBodyBytes) + "\"}", IndexToken);
        var tooLargeJson = await tooLarge.Content.ReadFromJsonAsync<JsonElement>();
        tooLargeJson.GetProperty("message").ValueKind.Should().Be(JsonValueKind.String);
    }

    private JsonElement StoredDocument(string id) =>
        JsonDocument.Parse(_factory.Es.Documents[id]).RootElement.Clone();

    private async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body, string token)
    {
        var response = await PostRawAsync(client, path, body, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Add("X-Internal-Token", token);
        }

        return await client.SendAsync(request);
    }
}
