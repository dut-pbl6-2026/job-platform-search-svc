using System.ComponentModel.DataAnnotations;
using Search.Core.Models;

namespace Search.Api.DTOs;

public record JobSyncDto(
    [Required] string Id,
    [Required] string Title,
    [Required] string Description,
    string? CompanyId = null,
    [Required] string CompanyName = "",
    [Required] string Location = "",
    decimal? SalaryMin = null,
    decimal? SalaryMax = null,
    string Currency = "VND",
    string? CategoryId = null,
    string? CategoryName = null,
    string EmploymentType = "FullTime",
    string? ExperienceLevel = null,
    string Status = "Published",
    string? RecruiterId = null,
    DateTime? CreatedAt = null,
    DateTime? UpdatedAt = null,
    DateTime? ExpiresAt = null,
    string? Requirements = null,
    string? Benefits = null
)
{
    public JobDocument ToDocument() => new()
    {
        Id = Id,
        Title = Title,
        Description = Description,
        CompanyId = CompanyId,
        CompanyName = CompanyName,
        Location = Location,
        SalaryMin = SalaryMin,
        SalaryMax = SalaryMax,
        Currency = Currency,
        CategoryId = CategoryId,
        CategoryName = CategoryName,
        EmploymentType = EmploymentType,
        ExperienceLevel = ExperienceLevel,
        Status = Status,
        RecruiterId = RecruiterId,
        CreatedAt = CreatedAt ?? DateTime.UtcNow,
        UpdatedAt = UpdatedAt ?? DateTime.UtcNow,
        ExpiresAt = ExpiresAt,
        Requirements = Requirements,
        Benefits = Benefits
    };
}
