namespace Search.Infrastructure.Configuration;

public class RedisOptions
{
    public const string SectionName = "Redis";

    public string Url { get; set; } = string.Empty;
    public int TtlSeconds { get; set; } = 300;
}
