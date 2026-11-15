using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Search.Api.DTOs;
using Search.Api.Endpoints;

namespace Search.Tests.Integration;

/// <summary>
/// Full request path against the Elasticsearch double: auth on every index route, the body limit,
/// bulk indexing, and the public search routes reading what was written.
/// </summary>
public class IndexAndSearchIntegrationTests : IClassFixture<SearchApiFactory>
{
    private const string IndexToken = SearchApiFactory.IndexToken;
    private const string TokenHeader = "X-Internal-Token";

    private readonly SearchApiFactory _factory;

    public IndexAndSearchIntegrationTests(SearchApiFactory factory)
    {
        _factory = factory;
        _factory.Es.Reset();
    }

    public static IEnumerable<object[]> IndexRoutes() =>
    [
        ["POST", "/api/search/index"],
        ["POST", "/api/search/bulk-index"],
        ["DELETE", "/api/search/index/job-guarded"],
    ];

    [Theory]
    [MemberData(nameof(IndexRoutes))]
    public async Task IndexRoute_WithoutToken_IsUnauthorized_AndNeverWrites(string method, string path)
    {
        var client = _factory.CreateClient();

        var response = await SendAsync(client, method, path, ValidBodyFor(path), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Es.DocumentWrites.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(IndexRoutes))]
    public async Task IndexRoute_WithWrongToken_IsUnauthorized_AndNeverWrites(string method, string path)
    {
        var client = _factory.CreateClient();

        var response = await SendAsync(client, method, path, ValidBodyFor(path), token: "not-the-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Es.DocumentWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexRoute_WithInvalidJsonAndNoToken_IsRejectedBeforeParsing()
    {
        // Auth runs before the body is read, so malformed JSON without a token is a 401, not a 400.
        var client = _factory.CreateClient();

        var response = await SendAsync(client, "POST", "/api/search/index", "{not json", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IndexRoute_OversizedBodyWithoutToken_IsUnauthorized_NotPayloadTooLarge()
    {
        // Auth runs before the size check and before parsing. The body is deliberately not JSON:
        // parsing it would give 400, so a 401 shows the body was never parsed.
        var client = _factory.CreateClient();

        var response = await SendAsync(client, "POST", "/api/search/index", OversizedNonJson(), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Es.DocumentWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_OversizedBodyWithToken_IsPayloadTooLarge_AndNeverWrites()
    {
        var client = _factory.CreateClient();

        var response = await SendAsync(client, "POST", "/api/search/index", OversizedJobJson(), IndexToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _factory.Es.DocumentWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_MalformedJsonWithToken_IsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await SendAsync(client, "POST", "/api/search/index", "{not json", IndexToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Es.DocumentWrites.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_WithToken_WritesDocumentAndSearchFindsIt()
    {
        var client = _factory.CreateClient();
        var job = JobJson("job-int-1", "Integration Engineer ZQ1");

        var write = await SendAsync(client, "POST", "/api/search/index", job, IndexToken);

        write.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Es.DocumentWrites.Should().ContainSingle(r => r.Path.EndsWith("/_doc/job-int-1", StringComparison.Ordinal));

        // Search is public: no token needed to read.
        var search = await client.GetAsync("/api/search/jobs?q=ZQ1");
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await search.Content.ReadFromJsonAsync<JobSearchResponseDto>();
        body!.Items.Select(d => d.Id).Should().Contain("job-int-1");
    }

    [Fact]
    public async Task Bulk_WithToken_IndexesEveryDocumentAndReportsCounts()
    {
        var client = _factory.CreateClient();
        var body = $"[{JobJson("job-bulk-1", "Bulk One")},{JobJson("job-bulk-2", "Bulk Two")}]";

        var response = await SendAsync(client, "POST", "/api/search/bulk-index", body, IndexToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BulkSyncResponseDto>();
        result!.TotalRequested.Should().Be(2);
        result.TotalIndexed.Should().Be(2);
        _factory.Es.Documents.Keys.Should().Contain(new[] { "job-bulk-1", "job-bulk-2" });
    }

    [Fact]
    public async Task Delete_WithToken_RemovesDocumentFromIndex()
    {
        var client = _factory.CreateClient();
        await SendAsync(client, "POST", "/api/search/index", JobJson("job-del-1", "To Delete"), IndexToken);

        var response = await SendAsync(client, "DELETE", "/api/search/index/job-del-1", null, IndexToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Es.Documents.Should().NotContainKey("job-del-1");
    }

    [Fact]
    public async Task SemanticSearch_IsPublic_ReturnsHitsFromIndex()
    {
        var client = _factory.CreateClient();
        await SendAsync(client, "POST", "/api/search/index", JobJson("job-sem-1", "Semantic Target"), IndexToken);

        var response = await client.GetAsync("/api/search/jobs/semantic?q=semantic-probe-7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SemanticSearchResponseDto>();
        body!.Items.Select(i => i.Job.Id).Should().Contain("job-sem-1");
    }

    private static string ValidBodyFor(string path) =>
        path.EndsWith("/bulk-index", StringComparison.Ordinal) ? $"[{JobJson("job-guarded", "Guarded")}]" :
        path.Contains("/index/", StringComparison.Ordinal) ? string.Empty :
        JobJson("job-guarded", "Guarded");

    private static string JobJson(string id, string title) =>
        $$"""{"id":"{{id}}","title":"{{title}}","description":"Desc for {{id}}","companyName":"Acme","location":"Ha Noi","salaryMin":1000000,"salaryMax":2000000,"skills":["React"]}""";

    private static string OversizedNonJson() => new('x', (int)IndexEndpoints.MaxIndexBodyBytes + 1);

    private static string OversizedJobJson()
    {
        var description = new string('x', (int)IndexEndpoints.MaxIndexBodyBytes);
        return $$"""{"id":"job-big","title":"Big","description":"{{description}}"}""";
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string path, string? body, string? token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Add(TokenHeader, token);
        }

        return await client.SendAsync(request);
    }
}
