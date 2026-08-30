using Search.Core.Models;

namespace Search.Api.DTOs;

public record JobSearchResponseDto(
    IReadOnlyList<JobDocument> Items,
    long Total,
    int Page,
    int Size,
    int TotalPages,
    string? Message = null
);
