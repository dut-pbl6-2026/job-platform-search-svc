using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Search.Core.Interfaces;
using Search.Core.Models;
using Search.Infrastructure.Configuration;
using Search.Infrastructure.Services;

namespace Search.Infrastructure.Workers;

/// <summary>
/// Kafka job-events consumer (PBL6-19). Runs in HTTP-sync standby mode when the
/// broker client is unavailable; index writes currently arrive via
/// POST /api/search/index from job-svc. Idempotent via ES _id = job_id.
/// </summary>
public class JobEventsConsumer : BackgroundService
{
    private readonly ILogger<JobEventsConsumer> _logger;
    private readonly KafkaOptions _kafka;
    private readonly IServiceProvider _services;

    public JobEventsConsumer(
        ILogger<JobEventsConsumer> logger,
        IOptions<KafkaOptions> kafka,
        IServiceProvider services)
    {
        _logger = logger;
        _kafka = kafka.Value;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_kafka.BootstrapServers))
        {
            _logger.LogInformation(
                "Kafka not configured (KAFKA_BOOTSTRAP_SERVERS empty). JobEventsConsumer running in HTTP-sync standby mode on topic {Topic}.",
                _kafka.Topic);
        }
        else
        {
            // NOTE: Confluent.Kafka package deferred (offline CI cache). When added,
            // consume job.created|updated|deleted from Topic/GroupId and call
            // HandleIndexAsync/HandleDeleteAsync below.
            _logger.LogWarning(
                "Kafka configured ({Bootstrap}) but broker client not bundled yet. Falling back to HTTP sync. Topic={Topic} Group={Group}.",
                _kafka.BootstrapServers, _kafka.Topic, _kafka.GroupId);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(
            _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
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
}
