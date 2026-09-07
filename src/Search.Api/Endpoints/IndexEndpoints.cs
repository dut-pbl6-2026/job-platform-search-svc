using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Search.Api.DTOs;
using Search.Core.Interfaces;

namespace Search.Api.Endpoints;

public static class IndexEndpoints
{
    private const string IndexTokenHeader = "X-Internal-Token";
    private const long MaxBulkBodyBytes = 5_000_000;

    public static void MapIndexEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search")
            .WithTags("Indexing")
            // S-1: service-to-service auth. The gateway routes /api/search/**
            // publicly, so without this anyone could inject/delete index docs.
            // Token from SEARCH_INDEX_TOKEN (or IndexAuth:Token); enforced when
            // set. Unset = local-dev open mode with a loud startup warning.
            .AddEndpointFilter(async (context, next) =>
            {
                var configuration = context.HttpContext.RequestServices
                    .GetRequiredService<IConfiguration>();
                var expected = configuration["SEARCH_INDEX_TOKEN"] ?? configuration["IndexAuth:Token"];
                if (string.IsNullOrWhiteSpace(expected))
                {
                    return await next(context);
                }

                var provided = context.HttpContext.Request.Headers[IndexTokenHeader].ToString() ?? "";
                var match = provided.Length == expected.Trim().Length &&
                    CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(provided),
                        Encoding.UTF8.GetBytes(expected.Trim()));
                if (!match)
                {
                    return Results.Json(
                        new { message = "Unauthorized. Valid X-Internal-Token required." },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                return await next(context);
            });

        // POST /api/search/index
        group.MapPost("/index", async (
            [FromBody] JobSyncDto dto,
            [FromServices] ISearchService searchService,
            [FromServices] ISearchCache cache,
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

            // D-2: orphan cached query results so the fresh doc is visible immediately.
            await cache.InvalidateJobAsync(dto.Id, cancellationToken);
            return Results.Ok(new { message = "Job indexed successfully", id = dto.Id });
        })
        .WithName("IndexJob")
        .WithSummary("Index or update a job document in Elasticsearch")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        // POST /api/search/bulk-index
        group.MapPost("/bulk-index", async (
            HttpContext httpContext,
            [FromBody] List<JobSyncDto> dtos,
            [FromServices] ISearchService searchService,
            [FromServices] ISearchCache cache,
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

            // S-2: count cap alone doesn't bound memory — 1000 huge docs could
            // exhaust the worker. Reject oversized bodies with 413.
            if (httpContext.Request.ContentLength > MaxBulkBodyBytes)
            {
                return Results.Json(
                    new { message = $"Bulk payload exceeds {MaxBulkBodyBytes} bytes." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var documents = dtos.Select(d => d.ToDocument());
            var count = await searchService.BulkIndexJobsAsync(documents, cancellationToken);

            await cache.InvalidateJobAsync($"bulk:{count}", cancellationToken);
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
            [FromServices] ISearchCache cache,
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

            await cache.InvalidateJobAsync(id, cancellationToken);
            return Results.Ok(new { message = "Job deleted from index successfully", id });
        })
        .WithName("DeleteJobIndex")
        .WithSummary("Delete a job document from Elasticsearch index by ID")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError);
    }
}
