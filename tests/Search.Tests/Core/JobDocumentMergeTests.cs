using Search.Core.Models;

namespace Search.Tests.Core;

public class JobDocumentMergeTests
{
    private static readonly DateTime Original = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);

    private static JobDocument Existing() => new() { Id = "j1", CreatedAt = Original, Skills = new List<string> { "C#" } };

    private static JobDocument Incoming() => new() { Id = "j1", CreatedAt = Now, UpdatedAt = Now };

    [Fact]
    public void NoExistingDocument_KeepsIncoming()
    {
        var doc = JobDocumentMerge.KeepStableFields(Incoming(), null, createdAtKnown: false, skillsKnown: false);

        doc.CreatedAt.Should().Be(Now);
        doc.Skills.Should().BeEmpty();
    }

    [Fact]
    public void UnknownCreatedAtAndSkills_AreCopiedFromExisting()
    {
        var doc = JobDocumentMerge.KeepStableFields(Incoming(), Existing(), createdAtKnown: false, skillsKnown: false);

        doc.CreatedAt.Should().Be(Original);
        doc.UpdatedAt.Should().Be(Now);
        doc.Skills.Should().Equal("C#");
    }

    [Fact]
    public void KnownCreatedAtAndSkills_WinOverExisting()
    {
        var incoming = Incoming();
        incoming.Skills = new List<string>();

        var doc = JobDocumentMerge.KeepStableFields(incoming, Existing(), createdAtKnown: true, skillsKnown: true);

        doc.CreatedAt.Should().Be(Now);
        doc.Skills.Should().BeEmpty();
    }
}
