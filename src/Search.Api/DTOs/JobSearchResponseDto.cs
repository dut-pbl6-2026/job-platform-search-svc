using Search.Core.Models;

namespace Search.Api.DTOs;

public record JobSearchResponseDto(
    IReadOnlyList<JobResponseDto> Items,
    long Total,
    int Page,
    int Size,
    int TotalPages,
    string? Message = null
)
{
    public static JobSearchResponseDto FromResult(SearchResult<JobDocument> result) => new(
        Items: result.Items.Select(JobResponseDto.FromDocument).ToList(),
        Total: result.Total,
        Page: result.Page,
        Size: result.Size,
        TotalPages: result.TotalPages,
        Message: result.Message);
}
