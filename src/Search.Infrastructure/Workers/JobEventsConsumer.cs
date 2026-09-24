using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Search.Infrastructure.Workers;

/// <summary>
/// Kafka job-events consumer (PBL6-34, SRS KAFKA-01-03).
/// Subscribes to <c>job-events</c> as group <c>search-svc</c>.
/// At-least-once: offsets commit only after handling; handlers are idempotent
/// via ES document <c>_id</c> = job id. Unknown event types and poison messages
/// are skipped (committed) instead of looping. Index writes also arrive via
/// POST /api/search/index until all producers migrate to Kafka.
/// </summary>
public class JobEventsConsumer : KafkaConsumerService
{
    private readonly IConfiguration _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<JobEventsConsumer> _typedLogger;

    public JobEventsConsumer(
        IOptions<KafkaOptions> kafka,
        IConfiguration config,
        IServiceProvider services,
        ILogger<JobEventsConsumer> logger)
        : base(kafka, logger)
    {
        _config = config;
        _services = services;
        _typedLogger = logger;
    }

    protected override string Topic =>
        (_config["KAFKA_TOPIC_JOB_EVENTS"] ?? _config["Kafka:Topic"] ?? "job-events").Trim() is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : "job-events";

    protected override string GroupId =>
        (_config["KAFKA_GROUP_ID"] ?? _config["Kafka:GroupId"] ?? "search-svc").Trim() is { } g && !string.IsNullOrWhiteSpace(g) ? g.Trim() : "search-svc";

    protected override async Task<MessageOutcome> HandleMessageAsync(string? key, string value, CancellationToken ct)
    {
        var eventType = TryGetEventType(value);
        if (string.IsNullOrWhiteSpace(eventType))
        {
            _typedLogger.LogWarning("Kafka poison message on {Topic}: missing eventType. Skipping.", Topic);
            return MessageOutcome.Skip;
        }

        try
        {
            switch (eventType.Trim())
            {
                case JobEventTypes.Created:
                    if (TryParseEnvelope<JobCreatedEvent>(value, out var created) && created is not null)
                    {
                        var document = ToDocument(created.Payload.OccurredAt, created.Payload.JobId, created.Payload.Title, created.Payload.Description,
                            created.Payload.CompanyId, created.Payload.CompanyName, created.Payload.Location,
                            created.Payload.SalaryMin, created.Payload.SalaryMax, created.Payload.Currency,
                            created.Payload.CategoryId, created.Payload.CategoryName, created.Payload.EmploymentType,
                            created.Payload.ExperienceLevel, created.Payload.RecruiterId, created.Payload.Requirements,
                            created.Payload.Benefits, created.Payload.Status);

                        if (string.IsNullOrWhiteSpace(key) || key.Trim() != document.Id)
                        {
                            _typedLogger.LogWarning("Kafka key mismatch on {Topic}: key={Key} job={JobId}. Using payload id.", Topic, key, document.Id);
                        }

                        var createdOk = await IndexDocumentAsync(document, ct);
                        return createdOk ? MessageOutcome.Handled : MessageOutcome.Retry;
                    }

                    _typedLogger.LogWarning("Kafka poison message on {Topic}: cannot parse {EventType}. Skipping.", Topic, eventType);
                    return MessageOutcome.Skip;

                case JobEventTypes.Updated:
                    if (TryParseEnvelope<JobUpdatedEvent>(value, out var updated) && updated is not null)
                    {
                        var document = ToDocument(updated.Payload.OccurredAt, updated.Payload.JobId, updated.Payload.Title, updated.Payload.Description,
                            updated.Payload.CompanyId, updated.Payload.CompanyName, updated.Payload.Location,
                            updated.Payload.SalaryMin, updated.Payload.SalaryMax, updated.Payload.Currency,
                            updated.Payload.CategoryId, updated.Payload.CategoryName, updated.Payload.EmploymentType,
                            updated.Payload.ExperienceLevel, updated.Payload.RecruiterId, updated.Payload.Requirements,
                            updated.Payload.Benefits, updated.Payload.Status);

                        if (string.IsNullOrWhiteSpace(key) || key.Trim() != document.Id)
                        {
                            _typedLogger.LogWarning("Kafka key mismatch on {Topic}: key={Key} job={JobId}. Using payload id.", Topic, key, document.Id);
                        }

                        var updatedOk = await IndexDocumentAsync(document, ct);
                        return updatedOk ? MessageOutcome.Handled : MessageOutcome.Retry;
                    }

                    _typedLogger.LogWarning("Kafka poison message on {Topic}: cannot parse {EventType}. Skipping.", Topic, eventType);
                    return MessageOutcome.Skip;

                case JobEventTypes.Deleted:
                    if (TryParseEnvelope<JobDeletedEvent>(value, out var deleted) && deleted is not null)
                    {
                        var removed = await DeleteDocumentAsync(deleted.Payload.JobId.ToString(), ct);
                        return removed ? MessageOutcome.Handled : MessageOutcome.Retry;
                    }

                    _typedLogger.LogWarning("Kafka poison message on {Topic}: cannot parse {EventType}. Skipping.", Topic, eventType);
                    return MessageOutcome.Skip;

                default:
                    _typedLogger.LogWarning("Kafka unknown event {EventType} on {Topic}. Skipping.", eventType, Topic);
                    return MessageOutcome.Skip;
            }
        }
        catch (JsonException ex)
        {
            _typedLogger.LogWarning(ex, "Kafka poison JSON on {Topic}. Skipping.", Topic);
            return MessageOutcome.Skip;
        }
    }

    public async Task HandleIndexAsync(JobDocument document, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var cache = scope.ServiceProvider.GetRequiredService<ISearchCache>();
        await search.IndexJobAsync(document, ct);
        await cache.InvalidateJobAsync(document.Id, ct);
    }

    public async Task HandleDeleteAsync(string jobId, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var cache = scope.ServiceProvider.GetRequiredService<ISearchCache>();
        await search.DeleteJobAsync(jobId, ct);
        await cache.InvalidateJobAsync(jobId, ct);
    }

    private async Task<bool> IndexDocumentAsync(JobDocument document, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var cache = scope.ServiceProvider.GetRequiredService<ISearchCache>();
        var ok = await search.IndexJobAsync(document, ct);
        if (ok)
        {
            await cache.InvalidateJobAsync(document.Id, ct);
        }

        return ok;
    }

    private async Task<bool> DeleteDocumentAsync(string jobId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();
        var cache = scope.ServiceProvider.GetRequiredService<ISearchCache>();
        var ok = await search.DeleteJobAsync(jobId, ct);
        if (ok)
        {
            await cache.InvalidateJobAsync(jobId, ct);
        }

        return ok;
    }

    private static JobDocument ToDocument(
        DateTime occurredAt, Guid jobId, string title, string description,
        Guid companyId, string companyName, string location,
        decimal? salaryMin, decimal? salaryMax, string currency,
        Guid? categoryId, string? categoryName, string employmentType,
        string experienceLevel, Guid recruiterId, string? requirements,
        string? benefits, string status) => new()
        {
            Id = jobId.ToString(),
            Title = title,
            Description = description,
            CompanyId = companyId.ToString(),
            CompanyName = companyName,
            Location = location,
            SalaryMin = salaryMin,
            SalaryMax = salaryMax,
            Currency = string.IsNullOrWhiteSpace(currency) ? "VND" : currency,
            CategoryId = categoryId?.ToString(),
            CategoryName = categoryName,
            EmploymentType = employmentType,
            ExperienceLevel = experienceLevel,
            Status = status,
            RecruiterId = recruiterId.ToString(),
            Requirements = requirements,
            Benefits = benefits,
            UpdatedAt = occurredAt == default ? DateTime.UtcNow : occurredAt,
            CreatedAt = occurredAt == default ? DateTime.UtcNow : occurredAt,
        };

    private static string? TryGetEventType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("eventType", out var camel) && camel.ValueKind == JsonValueKind.String)
            {
                return camel.GetString();
            }

            if (doc.RootElement.TryGetProperty("EventType", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            {
                return pascal.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
