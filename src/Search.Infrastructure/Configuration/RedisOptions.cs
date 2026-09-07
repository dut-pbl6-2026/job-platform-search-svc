namespace Search.Infrastructure.Configuration;

public class RedisOptions
{
    public const string SectionName = "Redis";

    public string Url { get; set; } = string.Empty;
    public int TtlSeconds { get; set; } = SearchCacheDefaults.DefaultTtlSeconds;
}

/// <summary>Single source for cache TTL fallbacks (review Q-3).</summary>
public static class SearchCacheDefaults
{
    public const int DefaultTtlSeconds = 300;
}
