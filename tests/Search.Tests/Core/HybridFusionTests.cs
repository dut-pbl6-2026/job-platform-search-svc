using Search.Core.Models;

namespace Search.Tests.Core;

/// <summary>
/// The fusion is the part that decides the order, so it is tested without Elasticsearch:
/// weights must be able to change the ranking, both contributions must be added for a shared hit,
/// and paging over the same candidate set must be stable.
/// </summary>
public class HybridFusionTests
{
    private static readonly DateTime Earlier = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ScoredJob Job(string id, double score, DateTime? createdAt = null) =>
        new(new JobDocument { Id = id, Title = id, CreatedAt = createdAt ?? Earlier }, score);

    [Fact]
    public void SingleList_IsNormalisedAndWeighted()
    {
        var fused = HybridFusion.Fuse([Job("a", 10), Job("b", 5)], [], 0.4, 0.6);

        fused.Select(hit => hit.Job.Id).Should().Equal("a", "b");
        fused[0].Score.Should().BeApproximately(0.4, 1e-9);
        fused[1].Score.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void SharedHit_AddsBothContributions()
    {
        // a wins BM25 (10 vs 5), b wins the vector ranking (0.9 vs 0.8).
        var fused = HybridFusion.Fuse([Job("a", 10), Job("b", 5)], [Job("b", 0.9), Job("a", 0.8)], 0.4, 0.6);

        fused.Select(hit => hit.Job.Id).Should().Equal("b", "a");
        fused[0].Score.Should().BeApproximately(0.6, 1e-9);   // b: 0 keyword + 0.6 vector
        fused[1].Score.Should().BeApproximately(0.4, 1e-9);   // a: 0.4 keyword + 0 vector
    }

    [Fact]
    public void Weights_ChangeTheRanking()
    {
        var keyword = new[] { Job("a", 10), Job("b", 5) };
        var vector = new[] { Job("b", 0.9), Job("a", 0.8) };

        HybridFusion.Fuse(keyword, vector, 0.9, 0.1).Select(hit => hit.Job.Id).Should().Equal("a", "b");
        HybridFusion.Fuse(keyword, vector, 0.1, 0.9).Select(hit => hit.Job.Id).Should().Equal("b", "a");
    }

    [Fact]
    public void SemanticOnlyHit_CanOutrankAKeywordOnlyHit()
    {
        // The default 40/60 must let a strong semantic match win even if it contains none of the words.
        var fused = HybridFusion.Fuse([Job("keyword", 10)], [Job("semantic", 0.9), Job("weak", 0.5)], 0.4, 0.6);

        fused.Select(hit => hit.Job.Id).Should().Equal("semantic", "keyword", "weak");
    }

    [Fact]
    public void EqualScores_KeepEveryDocument()
    {
        var fused = HybridFusion.Fuse([Job("a", 1), Job("b", 1)], [], 0.4, 0.6);

        fused.Should().HaveCount(2);
        fused.Should().OnlyContain(hit => Math.Abs(hit.Score - 0.4) < 1e-9);
    }

    [Fact]
    public void Ties_BreakOnCreatedAtThenId_SoPagingIsStable()
    {
        var fused = HybridFusion.Fuse([], [Job("b", 0.5, Earlier), Job("a", 0.5, Earlier), Job("c", 0.5, Later)], 0.4, 0.6);

        fused.Select(hit => hit.Job.Id).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void EmptyBothLists_ReturnsEmpty()
    {
        HybridFusion.Fuse([], [], 0.4, 0.6).Should().BeEmpty();
    }
}
