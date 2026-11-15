using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Search.Core.Interfaces;
using Search.Core.Models;

namespace Search.Tests.Api;

public class SemanticSearchEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SemanticSearchEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient Client(Mock<ISearchService> search) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(search.Object)))
            .CreateClient();

    [Theory]
    [InlineData("/api/search/jobs/semantic")]
    [InlineData("/api/search/jobs/semantic?q=%20%20")]
    public async Task MissingOrBlankQuery_ReturnsBadRequest(string url)
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);

        var response = await Client(search).GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task SizeOutOfRange_ReturnsBadRequest(int size)
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);

        var response = await Client(search).GetAsync($"/api/search/jobs/semantic?q=backend&size={size}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryLongerThanLimit_ReturnsBadRequest()
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);

        var response = await Client(search).GetAsync($"/api/search/jobs/semantic?q={new string('a', 501)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmbeddingUnavailable_ReturnsServiceUnavailable()
    {
        var search = new Mock<ISearchService>();
        search.Setup(s => s.SearchSemanticAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EmbeddingUnavailableException("down"));

        var response = await Client(search).GetAsync("/api/search/jobs/semantic?q=backend");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ValidQuery_ReturnsScoredJobsAndSearchesTrimmedText()
    {
        var job = new JobDocument { Id = "job-1", Title = "Backend Engineer", CompanyName = "Acme" };
        var search = new Mock<ISearchService>();
        search.Setup(s => s.SearchSemanticAsync("backend", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SemanticJobHit(job, 0.87f)]);

        var response = await Client(search).GetAsync("/api/search/jobs/semantic?q=%20backend%20&size=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var item = body.GetProperty("items")[0];
        item.GetProperty("score").GetSingle().Should().BeApproximately(0.87f, 0.001f);
        item.GetProperty("job").GetProperty("id").GetString().Should().Be("job-1");
        item.GetProperty("job").GetProperty("title").GetString().Should().Be("Backend Engineer");
        search.VerifyAll();
    }
}
