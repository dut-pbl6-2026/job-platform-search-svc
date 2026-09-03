using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Search.Api.DTOs;
using Search.Core.Interfaces;
using Search.Core.Models;

namespace Search.Tests.Api;

public class SearchEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SearchEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SearchJobs_WithInvalidPage_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/search/jobs?page=-1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchJobs_WithInvalidSize_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/search/jobs?size=150");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchJobs_WithValidParams_ReturnsMatchingJobs()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        var sampleDocs = new List<JobDocument>
        {
            new()
            {
                Id = "job-1",
                Title = "Senior Backend Developer",
                CompanyName = "TechCorp",
                Location = "Da Nang",
                SalaryMin = 25000000,
                SalaryMax = 45000000,
                CategoryName = "IT / Software"
            }
        };

        var expectedResult = SearchResult<JobDocument>.Create(sampleDocs, 1, 0, 20);

        mockSearchService
            .Setup(s => s.SearchJobsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/api/search/jobs?q=Backend&location=Da%20Nang");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JobSearchResponseDto>();

        result.Should().NotBeNull();
        result!.Total.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].Title.Should().Be("Senior Backend Developer");
        result.Items[0].CompanyName.Should().Be("TechCorp");
    }

    [Fact]
    public async Task SearchJobs_WhenNoResults_ReturnsOkWithGracefulMessage()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        var emptyResult = SearchResult<JobDocument>.Empty(0, 20);

        mockSearchService
            .Setup(s => s.SearchJobsAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyResult);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/api/search/jobs?q=nonexistentjob");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JobSearchResponseDto>();

        result.Should().NotBeNull();
        result!.Total.Should().Be(0);
        result.Items.Should().BeEmpty();
        result.Message.Should().Be("No jobs found matching your criteria");
    }

    [Fact]
    public async Task SuggestJobs_WithoutQuery_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/search/suggest?q=");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SuggestJobs_WithValidQuery_ReturnsSuggestionsList()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        var suggestions = new List<string> { "Software Engineer", "Solutions Architect" };

        mockSearchService
            .Setup(s => s.SuggestAsync("Soft", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(suggestions);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        // Act
        var response = await client.GetAsync("/api/search/suggest?q=Soft");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<string>>();

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(suggestions);
    }
}
