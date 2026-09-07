namespace Search.Infrastructure.Configuration;

public class ElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";

    public string Url { get; set; } = string.Empty;
    public string Index { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// When true, index/delete/bulk writes use Refresh.WaitFor (read-after-write
    /// for E2E tests and low-volume deploys). Set false in high-write production:
    /// WaitFor blocks shard refresh per write and increases write latency —
    /// the 1s refresh cycle then handles visibility (review B-4).
    /// Env: ELASTICSEARCH_REFRESH_ON_WRITE (default true).
    /// </summary>
    public bool RefreshOnWrite { get; set; } = true;
}
