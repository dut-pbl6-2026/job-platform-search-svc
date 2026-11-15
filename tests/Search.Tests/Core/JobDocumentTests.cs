using System.Text.Json;
using System.Text.Json.Nodes;
using Search.Core.Models;

namespace Search.Tests.Core;

public class JobDocumentTests
{
    [Fact]
    public void JobDocument_DefaultValues_ShouldBeSetCorrectly()
    {
        // Arrange & Act
        var doc = new JobDocument();

        // Assert
        doc.Id.Should().BeEmpty();
        doc.Currency.Should().Be("VND");
        doc.EmploymentType.Should().Be("FullTime");
        doc.Status.Should().Be("Active");
        doc.Skills.Should().NotBeNull().And.BeEmpty();
        doc.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        doc.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void JobDocument_Serialization_ShouldMapJsonPropertyNames()
    {
        // Arrange
        var doc = new JobDocument
        {
            Id = "job-123",
            Title = "Senior .NET Developer",
            Description = "Join our team to build scalable microservices.",
            CompanyName = "TechCorp",
            Location = "Da Nang",
            SalaryMin = 20000000,
            SalaryMax = 40000000,
            CategoryName = "IT / Software"
        };

        // Act
        var json = JsonSerializer.Serialize(doc);

        // Assert
        json.Should().Contain("\"id\":\"job-123\"");
        json.Should().Contain("\"company_name\":\"TechCorp\"");
        json.Should().Contain("\"salary_min\":20000000");
        json.Should().Contain("\"salary_max\":40000000");
        json.Should().Contain("\"category_name\":\"IT / Software\"");
    }

    [Fact]
    public void JobDocument_IndexSchema_IsSnakeCaseAndUnchanged()
    {
        // The Elasticsearch mapping and the crawler write these exact names. Changing the API casing
        // must never change this list (API responses map through JobResponseDto instead).
        var node = JsonSerializer.SerializeToNode(new JobDocument())!.AsObject();

        node.Select(p => p.Key).Should().BeEquivalentTo(new[]
        {
            "id", "title", "description", "company_id", "company_name", "location", "salary_min", "salary_max",
            "currency", "category_id", "category_name", "employment_type", "experience_level", "status",
            "recruiter_id", "created_at", "updated_at", "expires_at", "requirements", "benefits", "skills",
        });
    }

    [Fact]
    public void JobDocument_Skills_ShouldSerializeWithSnakeCaseName()
    {
        // Arrange
        var doc = new JobDocument { Id = "job-1", Skills = new() { "C#", "React" } };

        // Act
        var json = JsonSerializer.Serialize(doc);

        // Assert
        json.Should().Contain("\"skills\":[\"C#\",\"React\"]");
    }
}
