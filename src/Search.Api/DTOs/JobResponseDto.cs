using Search.Core.Models;

namespace Search.Api.DTOs;

/// <summary>
/// API view of an indexed job. Properties use the default camelCase JSON naming of the API; the
/// Elasticsearch document (<see cref="JobDocument"/>) keeps its snake_case index names.
/// <c>recruiterId</c> is deliberately not exposed: search is anonymous, while the job API hides
/// internal user ids from applicants. The indexed document keeps it for internal use.
/// </summary>
public record JobResponseDto(
    string Id,
    string Title,
    string Description,
    string? CompanyId,
    string CompanyName,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string Currency,
    string? CategoryId,
    string? CategoryName,
    string EmploymentType,
    string? ExperienceLevel,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ExpiresAt,
    string? Requirements,
    string? Benefits,
    IReadOnlyList<string> Skills
)
{
    public static JobResponseDto FromDocument(JobDocument job) => new(
        Id: job.Id,
        Title: job.Title,
        Description: job.Description,
        CompanyId: job.CompanyId,
        CompanyName: job.CompanyName,
        Location: job.Location,
        SalaryMin: job.SalaryMin,
        SalaryMax: job.SalaryMax,
        Currency: job.Currency,
        CategoryId: job.CategoryId,
        CategoryName: job.CategoryName,
        EmploymentType: job.EmploymentType,
        ExperienceLevel: job.ExperienceLevel,
        Status: job.Status,
        CreatedAt: job.CreatedAt,
        UpdatedAt: job.UpdatedAt,
        ExpiresAt: job.ExpiresAt,
        Requirements: job.Requirements,
        Benefits: job.Benefits,
        Skills: job.Skills);
}
