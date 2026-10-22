using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using Search.Infrastructure.Workers;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Search.Tests.Infrastructure;

/// <summary>
/// PBL6-34 consumer contract tests: routing of job.created/updated/deleted,
/// poison/unknown message handling and idempotent ES document keying.
/// </summary>
public class JobEventsConsumerTests
{
    private static readonly DateTime OccurredAt = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleMessageAsync_JobCreated_IndexesMappedDocumentAndInvalidatesCache()
    {
        var jobId = Guid.NewGuid();
        var payload = CreatedPayload(jobId);
        var (consumer, search, cache) = BuildConsumer();

        var outcome = await consumer.HandleAsync(
            jobId.ToString(),
            Serialize(EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, payload)));

        outcome.Should().Be(MessageOutcome.Handled);
        search.LastIndexed.Should().NotBeNull();
        var doc = search.LastIndexed!;
        doc.Id.Should().Be(jobId.ToString());
        doc.Title.Should().Be("Senior .NET Dev");
        doc.Description.Should().Be("Experienced .NET");
        doc.CompanyId.Should().Be(payload.CompanyId.ToString());
        doc.CompanyName.Should().Be("Acme Corp");
        doc.Location.Should().Be("Da Nang");
        doc.Status.Should().Be("Active");
        doc.UpdatedAt.Should().Be(OccurredAt);
        doc.CreatedAt.Should().Be(OccurredAt);
        cache.LastInvalidated.Should().Be(jobId.ToString());
    }

    [Fact]
    public async Task HandleMessageAsync_JobUpdated_UpsertsDocument()
    {
        var jobId = Guid.NewGuid();
        var (consumer, search, _) = BuildConsumer();
        var payload = CreatedPayload(jobId) with { Title = "Senior .NET Dev v2" };

        var outcome = await consumer.HandleAsync(
            jobId.ToString(),
            Serialize(EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, ToUpdated(payload))));

        outcome.Should().Be(MessageOutcome.Handled);
        search.LastIndexed.Should().NotBeNull();
        search.LastIndexed!.Id.Should().Be(jobId.ToString());
        search.LastIndexed.Title.Should().Be("Senior .NET Dev v2");
    }

    [Fact]
    public async Task HandleMessageAsync_JobDeleted_DeletesDocumentAndInvalidatesCache()
    {
        var jobId = Guid.NewGuid();
        var (consumer, search, cache) = BuildConsumer();

        var outcome = await consumer.HandleAsync(
            jobId.ToString(),
            Serialize(EventEnvelope<JobDeletedEvent>.Create(
                JobEventTypes.Deleted, new JobDeletedEvent(jobId, OccurredAt))));

        outcome.Should().Be(MessageOutcome.Handled);
        search.LastDeleted.Should().Be(jobId.ToString());
        search.LastIndexed.Should().BeNull();
        cache.LastInvalidated.Should().Be(jobId.ToString());
    }

    [Fact]
    public async Task HandleMessageAsync_UnknownEventType_SkipsWithoutSideEffects()
    {
        var (consumer, search, cache) = BuildConsumer();

        var outcome = await consumer.HandleAsync(
            "key", "{\"eventId\":\"" + Guid.NewGuid() + "\",\"eventType\":\"job.archived\",\"version\":1,\"payload\":{\"jobId\":\"" + Guid.NewGuid() + "\"}}");

        outcome.Should().Be(MessageOutcome.Skip);
        search.LastIndexed.Should().BeNull();
        search.LastDeleted.Should().BeNull();
        cache.LastInvalidated.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageAsync_MalformedJson_Skips()
    {
        var (consumer, search, _) = BuildConsumer();

        var outcome = await consumer.HandleAsync("key", "{ not-json");

        outcome.Should().Be(MessageOutcome.Skip);
        search.LastIndexed.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageAsync_PayloadMissingJobId_Skips()
    {
        var (consumer, search, _) = BuildConsumer();

        var outcome = await consumer.HandleAsync(
            "key",
            "{\"eventId\":\"" + Guid.NewGuid() + "\",\"eventType\":\"job.created\",\"version\":1,\"payload\":{}}");

        outcome.Should().Be(MessageOutcome.Skip);
        search.LastIndexed.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageAsync_IndexFailure_ReturnsRetry()
    {
        var jobId = Guid.NewGuid();
        var (consumer, search, cache) = BuildConsumer();
        search.IndexResult = false;

        var outcome = await consumer.HandleAsync(
            jobId.ToString(),
            Serialize(EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, CreatedPayload(jobId))));

        outcome.Should().Be(MessageOutcome.Retry);
        cache.LastInvalidated.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageAsync_DeleteFailure_ReturnsRetry()
    {
        var jobId = Guid.NewGuid();
        var (consumer, search, cache) = BuildConsumer();
        search.DeleteResult = false;

        var outcome = await consumer.HandleAsync(
            jobId.ToString(),
            Serialize(EventEnvelope<JobDeletedEvent>.Create(
                JobEventTypes.Deleted, new JobDeletedEvent(jobId, OccurredAt))));

        outcome.Should().Be(MessageOutcome.Retry);
        cache.LastInvalidated.Should().BeNull();
    }

    [Fact]
    public async Task HandleMessageAsync_KeyMismatch_StillUsesPayloadId()
    {
        var jobId = Guid.NewGuid();
        var (consumer, search, _) = BuildConsumer();

        var outcome = await consumer.HandleAsync(
            Guid.NewGuid().ToString(),
            Serialize(EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, CreatedPayload(jobId))));

        outcome.Should().Be(MessageOutcome.Handled);
        search.LastIndexed.Should().NotBeNull();
        search.LastIndexed!.Id.Should().Be(jobId.ToString());
    }

    [Fact]
    public async Task HandleMessageAsync_ReplayedEvent_IsIdempotentOnDocumentId()
    {
        var jobId = Guid.NewGuid();
        var (consumer, search, _) = BuildConsumer();
        var json = Serialize(EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, CreatedPayload(jobId)));

        var first = await consumer.HandleAsync(jobId.ToString(), json);
        var second = await consumer.HandleAsync(jobId.ToString(), json);

        first.Should().Be(MessageOutcome.Handled);
        second.Should().Be(MessageOutcome.Handled);
        search.IndexCalls.Should().Be(2);
        search.LastIndexed!.Id.Should().Be(jobId.ToString());
    }

    private static string Serialize<T>(EventEnvelope<T> envelope) =>
        JsonSerializer.Serialize(envelope, KafkaJson.Options);

    private static JobCreatedEvent CreatedPayload(Guid jobId) => new(
        jobId, "Senior .NET Dev", "Experienced .NET", Guid.NewGuid(), "Acme Corp", "Da Nang",
        20_000_000m, 40_000_000m, "VND", null, null, "FullTime", "Senior",
        Guid.NewGuid(), "C#", "Insurance", "Active", OccurredAt);

    private static JobUpdatedEvent ToUpdated(JobCreatedEvent source) => new(
        source.JobId, source.Title, source.Description, source.CompanyId, source.CompanyName,
        source.Location, source.SalaryMin, source.SalaryMax, source.Currency, source.CategoryId,
        source.CategoryName, source.EmploymentType, source.ExperienceLevel, source.RecruiterId,
        source.Requirements, source.Benefits, source.Status, source.OccurredAt);

    private static (TestConsumer Consumer, FakeSearchService Search, FakeSearchCache Cache) BuildConsumer()
    {
        var search = new FakeSearchService();
        var cache = new FakeSearchCache();

        var services = new ServiceCollection();
        services.AddSingleton<ISearchService>(search);
        services.AddSingleton<ISearchCache>(cache);
        var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KAFKA_TOPIC_JOB_EVENTS"] = "job-events",
                ["KAFKA_GROUP_ID"] = "search-svc",
            })
            .Build();

        var consumer = new TestConsumer(
            Options.Create(new KafkaOptions()),
            config,
            provider,
            NullLogger<JobEventsConsumer>.Instance);

        return (consumer, search, cache);
    }

    private sealed class TestConsumer : JobEventsConsumer
    {
        public TestConsumer(
            IOptions<KafkaOptions> kafka,
            IConfiguration config,
            IServiceProvider services,
            Microsoft.Extensions.Logging.ILogger<JobEventsConsumer> logger)
            : base(kafka, config, services, logger)
        {
        }

        public Task<MessageOutcome> HandleAsync(string? key, string value) =>
            HandleMessageAsync(key, value, CancellationToken.None);
    }

    private sealed class FakeSearchService : ISearchService
    {
        public int IndexCalls { get; private set; }
        public JobDocument? LastIndexed { get; private set; }
        public string? LastDeleted { get; private set; }
        public bool IndexResult { get; set; } = true;
        public bool DeleteResult { get; set; } = true;

        public Task<bool> IndexJobAsync(JobDocument document, CancellationToken cancellationToken = default)
        {
            IndexCalls++;
            LastIndexed = document;
            return Task.FromResult(IndexResult);
        }

        public Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            LastDeleted = jobId;
            return Task.FromResult(DeleteResult);
        }

        public Task<SearchResult<JobDocument>> SearchJobsAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit = 10, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> BulkIndexJobsAsync(IEnumerable<JobDocument> documents, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSearchCache : ISearchCache
    {
        public string? LastInvalidated { get; private set; }

        public Task InvalidateJobAsync(string jobId, CancellationToken ct = default)
        {
            LastInvalidated = jobId;
            return Task.CompletedTask;
        }

        public Task<SearchResult<JobDocument>?> GetAsync(SearchQuery query, CancellationToken ct = default) =>
            Task.FromResult<SearchResult<JobDocument>?>(null);

        public Task SetAsync(SearchQuery query, SearchResult<JobDocument> result, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
