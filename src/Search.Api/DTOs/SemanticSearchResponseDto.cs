using Search.Core.Models;

namespace Search.Api.DTOs;

/// <summary>Semantic hits, most similar first. No pagination: the endpoint returns at most `size` (1-20) jobs.</summary>
public record SemanticSearchResponseDto(IReadOnlyList<SemanticJobDto> Items);

public record SemanticJobDto(float Score, JobResponseDto Job)
{
    public static SemanticJobDto FromHit(SemanticJobHit hit) => new(hit.Score, JobResponseDto.FromDocument(hit.Job));
}
