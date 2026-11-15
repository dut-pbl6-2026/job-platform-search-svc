namespace Search.Infrastructure.Events;

/// <summary>
/// Event types published by job-platform-crawler for scraped external jobs. They share the
/// <c>job-events</c> topic with SharedKernel job events; unknown types are skipped by every consumer.
/// </summary>
public static class ExternalJobEventTypes
{
    /// <summary>External job scraped or re-scraped; upsert the search document.</summary>
    public const string Upserted = "job.external.upserted";
}

/// <summary>
/// Payload of <c>job.external.upserted</c> (crawler-owned contract, golden fixture in
/// job-platform-crawler <c>tests/fixtures/job_external_upserted.v1.json</c>).
/// <see cref="JobId"/> is the document id and Kafka key: sha256 hex of <see cref="SourceUrl"/>,
/// not a Guid, so it cannot use <c>JobCreatedEvent</c>.
/// </summary>
/// <param name="JobId">sha256 hex of <paramref name="SourceUrl"/>; becomes the ES <c>_id</c>.</param>
/// <param name="SourceUrl">Public listing URL of the external job.</param>
/// <param name="Title">Job title (required).</param>
/// <param name="CompanyName">Employer name as scraped.</param>
/// <param name="Location">Normalized location.</param>
/// <param name="SalaryMin">Minimum salary in VND, null when negotiable or unknown.</param>
/// <param name="SalaryMax">Maximum salary in VND, null when negotiable or unknown.</param>
/// <param name="Currency">Currency code, VND when null.</param>
/// <param name="Description">Plain-text description, may be null (list API has none).</param>
/// <param name="Requirements">Plain-text requirements, may be null.</param>
/// <param name="CategoryName">Category as scraped, may be null.</param>
/// <param name="OccurredAt">UTC crawl time.</param>
public sealed record ExternalJobUpsertedPayload(
    string JobId,
    string SourceUrl,
    string Title,
    string? CompanyName,
    string? Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? Currency,
    string? Description,
    string? Requirements,
    string? CategoryName,
    DateTime OccurredAt);
