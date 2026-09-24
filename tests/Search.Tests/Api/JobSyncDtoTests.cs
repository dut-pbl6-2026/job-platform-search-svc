using Search.Api.DTOs;

namespace Search.Tests.Api;

public class JobSyncDtoTests
{
    [Fact]
    public void ToDocument_WithSkills_MapsSkillsToDocument()
    {
        // Arrange
        var dto = new JobSyncDto(
            Id: "job-1",
            Title: "Backend Dev",
            Description: "desc",
            Skills: new() { "C#", ".NET" });

        // Act
        var doc = dto.ToDocument();

        // Assert
        doc.Skills.Should().BeEquivalentTo("C#", ".NET");
    }

    [Fact]
    public void ToDocument_WithoutSkills_DefaultsToEmptyList()
    {
        // Arrange
        var dto = new JobSyncDto(Id: "job-1", Title: "Backend Dev", Description: "desc");

        // Act
        var doc = dto.ToDocument();

        // Assert
        doc.Skills.Should().NotBeNull().And.BeEmpty();
    }
}
