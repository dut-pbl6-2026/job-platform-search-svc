using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Search.Core.Models;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;

namespace Search.Tests.Infrastructure;

public class SearchCacheTests
{
    private static RedisSearchCache CreateCache()
    {
        var memory = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return new RedisSearchCache(
            memory,
            Options.Create(new RedisOptions { Url = "", TtlSeconds = 300 }),
            NullLogger<RedisSearchCache>.Instance);
    }

    [Fact]
    public void BuildKey_SameQuery_ShouldReturnSameKey()
    {
        var q1 = new SearchQuery(Keyword: "Engineer", Location: "Hanoi", Page: 0, Size: 20);
        var q2 = new SearchQuery(Keyword: " engineer ", Location: "HANOI", Page: 0, Size: 20);

        RedisSearchCache.BuildKey(q1).Should().Be(RedisSearchCache.BuildKey(q2));
    }

    [Fact]
    public void BuildKey_DifferentQuery_ShouldReturnDifferentKey()
    {
        var q1 = new SearchQuery(Keyword: "Engineer", Page: 0, Size: 20);
        var q2 = new SearchQuery(Keyword: "Designer", Page: 0, Size: 20);

        RedisSearchCache.BuildKey(q1).Should().NotBe(RedisSearchCache.BuildKey(q2));
    }

    [Fact]
    public async Task SetAndGet_Roundtrip_ShouldReturnCachedResult()
    {
        var cache = CreateCache();
        var query = new SearchQuery(Keyword: "qa", Page: 0, Size: 20);
        var result = SearchResult<JobDocument>.Create(
            new[] { new JobDocument { Id = "j1", Title = "QA Engineer" } }, 1, 0, 20);

        await cache.SetAsync(query, result);
        var cached = await cache.GetAsync(query);

        cached.Should().NotBeNull();
        cached!.Total.Should().Be(1);
        cached.Items.Should().ContainSingle(i => i.Title == "QA Engineer");
    }

    [Fact]
    public async Task Get_MissingKey_ShouldReturnNull()
    {
        var cache = CreateCache();

        var cached = await cache.GetAsync(new SearchQuery(Keyword: "nope", Page: 0, Size: 20));

        cached.Should().BeNull();
    }
}
