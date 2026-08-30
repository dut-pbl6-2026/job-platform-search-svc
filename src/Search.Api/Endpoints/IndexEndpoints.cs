using Microsoft.AspNetCore.Mvc;
using Search.Api.DTOs;
using Search.Core.Interfaces;

namespace Search.Api.Endpoints;

public static class IndexEndpoints
{
    public static void MapIndexEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search")
            .WithTags("Indexing");

        // POST /api/search/index
        group.MapPost("/index", async (
            [FromBody] JobSyncDto dto,
            [FromServices] ISearchService searchService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Title))
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid document",
                    Detail = "Id and Title are required fields."
                });
            }

            var document = dto.ToDocument();
            var success = await searchService.IndexJobAsync(document, cancellationToken);

            if (!success)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Indexing Failed",
                    detail: "Could not index the job document into Elasticsearch."
                );
            }

            return Results.Ok(new { message = "Job indexed successfully", id = dto.Id });
        })
        .WithName("IndexJob")
        .WithSummary("Index or update a job document in Elasticsearch")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        // POST /api/search/bulk-index
        group.MapPost("/bulk-index", async (
            [FromBody] List<JobSyncDto> dtos,
            [FromServices] ISearchService searchService,
            CancellationToken cancellationToken) =>
        {
            if (dtos == null || dtos.Count == 0)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Empty batch",
                    Detail = "List of job documents must not be empty."
                });
            }

            const int maxBulkSize = 1000;
            if (dtos.Count > maxBulkSize)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Batch too large",
                    Detail = $"Maximum {maxBulkSize} documents per bulk-index request."
                });
            }

            var documents = dtos.Select(d => d.ToDocument());
            var count = await searchService.BulkIndexJobsAsync(documents, cancellationToken);

            return Results.Ok(new BulkSyncResponseDto(
                TotalRequested: dtos.Count,
                TotalIndexed: count,
                Message: $"Successfully indexed {count}/{dtos.Count} jobs."
            ));
        })
        .WithName("BulkIndexJobs")
        .WithSummary("Bulk index multiple job documents in Elasticsearch")
        .Produces<BulkSyncResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        // DELETE /api/search/index/{id}
        group.MapDelete("/index/{id}", async (
            [FromRoute] string id,
            [FromServices] ISearchService searchService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid Id",
                    Detail = "Job ID parameter is required."
                });
            }

            var success = await searchService.DeleteJobAsync(id, cancellationToken);

            if (!success)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Deletion Failed",
                    detail: "Could not delete the job document from Elasticsearch."
                );
            }

            return Results.Ok(new { message = "Job deleted from index successfully", id });
        })
        .WithName("DeleteJobIndex")
        .WithSummary("Delete a job document from Elasticsearch index by ID")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError);
    }
}
