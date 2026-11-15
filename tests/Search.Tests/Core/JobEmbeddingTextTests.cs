using Search.Core.Models;

namespace Search.Tests.Core;

public class JobEmbeddingTextTests
{
    [Fact]
    public void From_PutsTitleAndSkillsBeforeDescription()
    {
        var job = new JobDocument { Title = "Backend Engineer", Skills = ["c#", "sql"], Description = "Build APIs" };

        JobEmbeddingText.From(job).Should().Be("Backend Engineer c# sql Build APIs");
    }

    [Fact]
    public void From_CapsAtTheChunkWordLimit()
    {
        var job = new JobDocument { Title = "T", Description = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"w{i}")) };

        JobEmbeddingText.From(job).Split(' ').Should().HaveCount(JobEmbeddingText.MaxWords);
    }

    [Fact]
    public void From_IgnoresEmptySkillsAndExtraWhitespace()
    {
        var job = new JobDocument { Title = "  Dev ", Skills = [], Description = "\n Desc  " };

        JobEmbeddingText.From(job).Should().Be("Dev Desc");
    }
}
