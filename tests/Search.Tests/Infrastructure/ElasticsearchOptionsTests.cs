using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Extensions;

namespace Search.Tests.Infrastructure;

public class ElasticsearchOptionsTests
{
    [Fact]
    public void AddElasticsearchInfrastructure_WithValidConfig_ShouldBindOptions()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ELASTICSEARCH_URL", "http://localhost:9200" },
            { "ELASTICSEARCH_INDEX", "jobs" }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();

        // Act
        services.AddLogging();
        services.AddElasticsearchInfrastructure(configuration);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;

        // Assert
        options.Url.Should().Be("http://localhost:9200");
        options.Index.Should().Be("jobs");
    }

    [Fact]
    public void AddElasticsearchInfrastructure_MissingUrl_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ELASTICSEARCH_INDEX", "jobs" }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();

        // Act
        var act = () => services.AddElasticsearchInfrastructure(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ES URL not configured*");
    }

    [Fact]
    public void AddElasticsearchInfrastructure_MissingIndex_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ELASTICSEARCH_URL", "http://localhost:9200" }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();

        // Act
        var act = () => services.AddElasticsearchInfrastructure(configuration);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ES index not configured*");
    }
}

public class HybridSearchOptionsTests
{
    [Fact]
    public void Defaults_AreKeyword40Vector60()
    {
        var options = new HybridSearchOptions();

        options.Enabled.Should().BeTrue();
        options.KeywordWeight.Should().Be(0.4);
        options.VectorWeight.Should().Be(0.6);
        options.Validate();
    }

    [Fact]
    public void EnvVars_OverrideTheWeights()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ELASTICSEARCH_URL", "http://localhost:9200" },
                { "ELASTICSEARCH_INDEX", "jobs" },
                { "SEARCH_HYBRID_ENABLED", "false" },
                { "SEARCH_HYBRID_KEYWORD_WEIGHT", "0.7" },
                { "SEARCH_HYBRID_VECTOR_WEIGHT", "0.3" },
                { "SEARCH_HYBRID_MAX_CANDIDATES", "120" }
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElasticsearchInfrastructure(configuration);
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<HybridSearchOptions>>().Value;

        options.Enabled.Should().BeFalse();
        options.KeywordWeight.Should().Be(0.7);
        options.VectorWeight.Should().Be(0.3);
        options.MaxCandidates.Should().Be(120);
    }

    [Theory]
    [InlineData(-0.1, 0.6)]
    [InlineData(0.4, -0.6)]
    [InlineData(0, 0)]
    public void InvalidWeights_FailFast(double keyword, double vector)
    {
        var options = new HybridSearchOptions { KeywordWeight = keyword, VectorWeight = vector };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>();
    }
}
