namespace Search.Infrastructure.Configuration;

public class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;
    public string Topic { get; set; } = "job-events";
    public string GroupId { get; set; } = "search-svc";
}
