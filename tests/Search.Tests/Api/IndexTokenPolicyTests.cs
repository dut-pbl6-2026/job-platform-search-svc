using Search.Api.Endpoints;

namespace Search.Tests.Api;

public class IndexTokenPolicyTests
{
    [Fact]
    public void Evaluate_UnsetTokenOutsideDevelopment_IsUnavailable()
    {
        IndexTokenPolicy.Evaluate(isDevelopment: false, expected: null, provided: "")
            .Should().Be(IndexTokenDecision.Unavailable);
        IndexTokenPolicy.Evaluate(isDevelopment: false, expected: null, provided: "anything")
            .Should().Be(IndexTokenDecision.Unavailable);
    }

    [Fact]
    public void Evaluate_UnsetTokenInDevelopment_IsAllowed()
    {
        IndexTokenPolicy.Evaluate(isDevelopment: true, expected: null, provided: "")
            .Should().Be(IndexTokenDecision.Allowed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_WrongOrMissingToken_IsUnauthorized(bool isDevelopment)
    {
        IndexTokenPolicy.Evaluate(isDevelopment, expected: "secret", provided: "")
            .Should().Be(IndexTokenDecision.Unauthorized);
        IndexTokenPolicy.Evaluate(isDevelopment, expected: "secret", provided: "secre")
            .Should().Be(IndexTokenDecision.Unauthorized);
        IndexTokenPolicy.Evaluate(isDevelopment, expected: "secret", provided: "secret ")
            .Should().Be(IndexTokenDecision.Unauthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_MatchingToken_IsAllowed(bool isDevelopment)
    {
        IndexTokenPolicy.Evaluate(isDevelopment, expected: "secret", provided: "secret")
            .Should().Be(IndexTokenDecision.Allowed);
    }
}
