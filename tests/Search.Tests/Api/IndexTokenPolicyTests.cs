using Microsoft.Extensions.Configuration;
using Search.Api.Endpoints;

namespace Search.Tests.Api;

public class IndexTokenPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfiguredIndexToken_UnsetOrBlank_IsNull(string? value)
    {
        IndexEndpoints.ConfiguredIndexToken(ConfigWith(value)).Should().BeNull();
    }

    [Fact]
    public void ConfiguredIndexToken_SurroundingWhitespace_IsTrimmed()
    {
        IndexEndpoints.ConfiguredIndexToken(ConfigWith("  secret  ")).Should().Be("secret");
    }

    private static IConfiguration ConfigWith(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [IndexEndpoints.IndexTokenConfigKey] = token })
            .Build();

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
