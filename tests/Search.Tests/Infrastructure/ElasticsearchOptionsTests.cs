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
