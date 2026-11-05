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
    public void Skills_IsNullByDefault()
    {
        var query = new SearchQuery();

        query.Skills.Should().BeNull();
    }

    [Fact]
    public void Skills_CanBeSet()
    {
        var query = new SearchQuery(Skills: "C#, .NET");

        query.Skills.Should().Be("C#, .NET");
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("FullTime", new[] { "FullTime" })]
    [InlineData("FullTime,PartTime", new[] { "FullTime", "PartTime" })]
    [InlineData(" FullTime , ,PartTime,fulltime ", new[] { "FullTime", "PartTime" })]
    public void SplitCsv_TrimsDropsEmptiesAndDuplicates(string? input, string[] expected)
    {
        SearchQuery.SplitCsv(input).Should().Equal(expected);
    }

    [Fact]
    public void MultiValueFilters_AreSplitFromCsv()
    {
        var query = new SearchQuery(EmploymentType: "FullTime,PartTime", ExperienceLevel: "Mid, Senior", Skills: "C#, React");

        query.EmploymentTypes.Should().Equal("FullTime", "PartTime");
        query.ExperienceLevels.Should().Equal("Mid", "Senior");
        query.SkillList.Should().Equal("C#", "React");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("or", false)]
    [InlineData("OR", false)]
    [InlineData("and", true)]
    [InlineData(" AND ", true)]
    public void MatchAllSkills_OnlyForAnd_DefaultIsOr(string? skillOp, bool expected)
    {
        new SearchQuery(SkillOp: skillOp).MatchAllSkills.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("and", true)]
    [InlineData("Or", true)]
    [InlineData("xor", false)]
    [InlineData("all", false)]
    public void IsValidSkillOp_AcceptsOnlyAndOr(string? skillOp, bool expected)
    {
        SearchQuery.IsValidSkillOp(skillOp).Should().Be(expected);
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
