using Search.Core.Models;

namespace Search.Api.DTOs;

/// <summary>Semantic hits, most similar first. No pagination: the endpoint returns at most `size` (1-20) jobs.</summary>
public record SemanticSearchResponseDto(IReadOnlyList<SemanticJobDto> Items);

public record SemanticJobDto(float Score, JobDocument Job);
