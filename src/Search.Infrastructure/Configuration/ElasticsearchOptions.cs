namespace Search.Infrastructure.Configuration;

public class ElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";

    public string Url { get; set; } = string.Empty;
    public string Index { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
