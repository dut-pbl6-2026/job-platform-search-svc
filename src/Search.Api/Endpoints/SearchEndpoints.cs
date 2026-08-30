using Microsoft.AspNetCore.Mvc;
using Search.Api.DTOs;
using Search.Core.Interfaces;
using Search.Core.Models;

namespace Search.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search")
            .WithTags("Search");

        // GET /api/search/jobs (SRS SEARCH-01-02 -> SEARCH-01-05)
        group.MapGet("/jobs", async (
            [FromQuery] string? q,
            [FromQuery] string? location,
            [FromQuery] string? category,
            [FromQuery] string? employmentType,
            [FromQuery] string? experienceLevel,
            [FromQuery] decimal? minSalary,
            [FromQuery] decimal? maxSalary,
            [FromQuery] int page = 0,
            [FromQuery] int size = 20,
            [FromQuery] string? sortBy = null,
            [FromServices] ISearchService searchService = null!,
            CancellationToken cancellationToken = default) =>
        {
            if (page < 0)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid pagination",
                    Detail = "Page index must be greater than or equal to 0."
                });
            }

            if (size < 1 || size > 100)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid page size",
                    Detail = "Page size must be between 1 and 100."
                });
            }

            var searchQuery = new SearchQuery(
                Keyword: q,
                Location: location,
                Category: category,
                EmploymentType: employmentType,
                ExperienceLevel: experienceLevel,
                MinSalary: minSalary,
                MaxSalary: maxSalary,
                Page: page,
                Size: size,
                SortBy: sortBy
            );

            var result = await searchService.SearchJobsAsync(searchQuery, cancellationToken);

            var response = new JobSearchResponseDto(
                Items: result.Items,
                Total: result.Total,
                Page: result.Page,
                Size: result.Size,
                TotalPages: result.TotalPages,
                Message: result.Message
            );

            return Results.Ok(response);
        })
        .WithName("SearchJobs")
        .WithSummary("Search jobs with full-text keywords, location and filters")
        .Produces<JobSearchResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        // GET /api/search/suggest (SRS 3.4.3)
        group.MapGet("/suggest", async (
            [FromQuery] string? q,
            [FromQuery] int limit = 10,
            [FromServices] ISearchService searchService = null!,
            CancellationToken cancellationToken = default) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Missing query parameter",
                    Detail = "Query parameter 'q' is required for suggestions."
                });
            }

            var suggestions = await searchService.SuggestAsync(q, limit, cancellationToken);
            return Results.Ok(suggestions);
        })
        .WithName("SuggestJobs")
        .WithSummary("Get autocomplete suggestions for jobs")
        .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
