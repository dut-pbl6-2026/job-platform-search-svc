using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Search.Api.DTOs;
using Search.Core.Interfaces;
using Search.Core.Models;

namespace Search.Tests.Api;

public class SearchCacheEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SearchCacheEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SearchJobs_RepeatedQuery_ShouldServeSecondHitFromCache()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        var docs = new List<JobDocument>
        {
            new() { Id = "job-9", Title = "Cache Engineer", CompanyName = "CacheCorp" }
        };
        mockSearchService
            .Setup(s => s.SearchJobsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResult<JobDocument>.Create(docs, 1, 0, 20));

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        // Act
        var first = await client.GetAsync("/api/search/jobs?q=cache");
        var second = await client.GetAsync("/api/search/jobs?q=cache");

        // Assert
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<JobSearchResponseDto>();
        var secondBody = await second.Content.ReadFromJsonAsync<JobSearchResponseDto>();
        secondBody.Should().BeEquivalentTo(firstBody);
        mockSearchService.Verify(
            s => s.SearchJobsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchJobs_CacheHit_ShouldNotCallSearchService()
    {
        // Arrange: prime the host's cache directly, then assert the endpoint
        // serves the query without touching Elasticsearch (review T-2/B-1).
        var mockSearchService = new Mock<ISearchService>();
        var primed = SearchResult<JobDocument>.Create(
            new[] { new JobDocument { Id = "job-primed", Title = "Primed Engineer" } }, 1, 0, 20);

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        });

        using (var scope = customFactory.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<ISearchCache>();
            await cache.SetAsync(new SearchQuery(Keyword: "primed"), primed);
        }

        // Act
        var response = await customFactory.CreateClient().GetAsync("/api/search/jobs?q=primed");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JobSearchResponseDto>();
        body!.Items.Should().ContainSingle(i => i.Title == "Primed Engineer");
        mockSearchService.Verify(
            s => s.SearchJobsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
