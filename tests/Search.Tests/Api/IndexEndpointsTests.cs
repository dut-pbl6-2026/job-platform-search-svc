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

public class IndexEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IndexEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task IndexJob_WithMissingRequiredFields_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();
        var invalidDto = new { Id = "", Title = "", Description = "Test", CompanyName = "", Location = "" };

        // Act
        var response = await client.PostAsJsonAsync("/api/search/index", invalidDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IndexJob_WithValidDto_ReturnsOk()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        mockSearchService
            .Setup(s => s.IndexJobAsync(It.IsAny<JobDocument>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        var validDto = new JobSyncDto(
            Id: "job-101",
            Title: "Fullstack Engineer",
            Description: "Awesome opportunity",
            CompanyName: "Acme Corp",
            Location: "Ha Noi"
        );

        // Act
        var response = await client.PostAsJsonAsync("/api/search/index", validDto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("id").GetString().Should().Be("job-101");
    }

    [Fact]
    public async Task BulkIndexJobs_WithEmptyList_ReturnsBadRequest()
    {
        // Arrange
        var client = _factory.CreateClient();
        var emptyList = new List<JobSyncDto>();

        // Act
        var response = await client.PostAsJsonAsync("/api/search/bulk-index", emptyList);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkIndexJobs_WithItems_ReturnsIndexedCount()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        mockSearchService
            .Setup(s => s.BulkIndexJobsAsync(It.IsAny<IEnumerable<JobDocument>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        var items = new List<JobSyncDto>
        {
            new("job-1", "Dev 1", "Desc 1", CompanyName: "C1", Location: "L1"),
            new("job-2", "Dev 2", "Desc 2", CompanyName: "C2", Location: "L2")
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/search/bulk-index", items);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BulkSyncResponseDto>();
        result.Should().NotBeNull();
        result!.TotalRequested.Should().Be(2);
        result.TotalIndexed.Should().Be(2);
    }

    [Fact]
    public async Task DeleteJob_WithValidId_ReturnsOk()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        mockSearchService
            .Setup(s => s.DeleteJobAsync("job-101", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        // Act
        var response = await client.DeleteAsync("/api/search/index/job-101");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("id").GetString().Should().Be("job-101");
    }

    [Fact]
    public async Task DeleteJob_WhenServiceFails_ReturnsInternalServerError()
    {
        // Arrange
        var mockSearchService = new Mock<ISearchService>();
        mockSearchService
            .Setup(s => s.DeleteJobAsync("job-err", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(mockSearchService.Object);
            });
        }).CreateClient();

        // Act
        var response = await client.DeleteAsync("/api/search/index/job-err");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
