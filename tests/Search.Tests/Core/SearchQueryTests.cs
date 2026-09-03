using Search.Core.Models;

namespace Search.Tests.Core;

public class SearchQueryTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    public void NormalizedPage_ShouldHandleNegativeAndValidValues(int inputPage, int expectedPage)
    {
        // Arrange
        var query = new SearchQuery(Page: inputPage);

        // Act & Assert
        query.NormalizedPage.Should().Be(expectedPage);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-10, 20)]
    [InlineData(50, 50)]
    [InlineData(150, 100)]
    public void NormalizedSize_ShouldClampBetween1And100(int inputSize, int expectedSize)
    {
        // Arrange
        var query = new SearchQuery(Size: inputSize);

        // Act & Assert
        query.NormalizedSize.Should().Be(expectedSize);
    }

    [Fact]
    public void From_ShouldCalculateCorrectOffset()
    {
        // Arrange
        var query = new SearchQuery(Page: 3, Size: 20);

        // Act & Assert
        query.From.Should().Be(60);
    }

    [Fact]
    public void SearchResult_Empty_ShouldReturnValidStructureWithMessage()
    {
        // Act
        var emptyResult = SearchResult<JobDocument>.Empty(0, 20);

        // Assert
        emptyResult.Items.Should().BeEmpty();
        emptyResult.Total.Should().Be(0);
        emptyResult.Page.Should().Be(0);
        emptyResult.Size.Should().Be(20);
        emptyResult.TotalPages.Should().Be(0);
        emptyResult.Message.Should().Be("No jobs found matching your criteria");
    }

    [Fact]
    public void SearchResult_Create_ShouldCalculateTotalPagesCorrectly()
    {
        // Arrange
        var items = new List<JobDocument> { new() { Id = "1" }, new() { Id = "2" } };

        // Act
        var result = SearchResult<JobDocument>.Create(items, 45, 0, 20);

        // Assert
        result.Total.Should().Be(45);
        result.TotalPages.Should().Be(3);
    }
}
