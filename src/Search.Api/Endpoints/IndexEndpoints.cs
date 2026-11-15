using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Search.Api.DTOs;
using Search.Core.Interfaces;
using Search.Core.Models;

namespace Search.Api.Endpoints;

public static class IndexEndpoints
{
    public const string IndexTokenConfigKey = "SEARCH_INDEX_TOKEN";
    private const string IndexTokenHeader = "X-Internal-Token";
    private const long MaxBulkBodyBytes = 5_000_000;

    // Trimmed token, or null when unset. Program.cs uses the same value to fail startup.
    public static string? ConfiguredIndexToken(IConfiguration configuration) =>
        configuration[IndexTokenConfigKey]?.Trim() is { Length: > 0 } token ? token : null;

    public static void MapIndexEndpoints(this IEndpointRouteBuilder app)
    {
        // The gateway routes /api/search/** publicly, so the token is the only barrier to index
        // writes. An unset token is accepted only in Development; everywhere else it is rejected.
        var group = app.MapGroup("/api/search")
            .WithTags("Indexing")
            .AddEndpointFilter(async (context, next) =>
            {
                var services = context.HttpContext.RequestServices;
                var decision = IndexTokenPolicy.Evaluate(
                    services.GetRequiredService<IHostEnvironment>().IsDevelopment(),
                    ConfiguredIndexToken(services.GetRequiredService<IConfiguration>()),
                    context.HttpContext.Request.Headers[IndexTokenHeader].ToString());

                return decision switch
                {
                    IndexTokenDecision.Allowed => await next(context),
                    IndexTokenDecision.Unauthorized => Results.Json(
                        new { message = "Unauthorized. Valid X-Internal-Token required." },
                        statusCode: StatusCodes.Status401Unauthorized),
                    _ => Results.Json(
                        new { message = "Index endpoints are disabled: SEARCH_INDEX_TOKEN is not configured." },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                };
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

            // job-svc's HTTP fallback replaces the whole document: keep the original created_at
            // and skills when the payload does not carry them.
            var createdAtKnown = dto.CreatedAt.HasValue;
            var skillsKnown = dto.Skills is not null;
            var existing = createdAtKnown && skillsKnown
                ? null
                : await searchService.GetJobAsync(dto.Id, cancellationToken);
            var document = JobDocumentMerge.KeepStableFields(dto.ToDocument(), existing, createdAtKnown, skillsKnown);
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
        // The body is read here instead of bound by the framework: binding deserializes before any
        // handler code runs, so a size check placed after it cannot prevent the work.
        group.MapPost("/bulk-index", async (
            HttpContext httpContext,
            [FromServices] ISearchService searchService,
            [FromServices] ISearchCache cache,
            CancellationToken cancellationToken) =>
        {
            var body = await ReadBoundedBodyAsync(httpContext.Request, MaxBulkBodyBytes, cancellationToken);
            if (body is null)
            {
                return Results.Json(
                    new { message = $"Bulk payload exceeds {MaxBulkBodyBytes} bytes." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            List<JobSyncDto>? dtos;
            try
            {
                dtos = JsonSerializer.Deserialize<List<JobSyncDto>>(body, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid JSON",
                    Detail = "Request body must be a JSON array of job documents."
                });
            }

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

            await cache.InvalidateJobAsync($"bulk:{count}", cancellationToken);
            return Results.Ok(new BulkSyncResponseDto(
                TotalRequested: dtos.Count,
                TotalIndexed: count,
                Message: $"Successfully indexed {count}/{dtos.Count} jobs."
            ));
        })
        .WithName("BulkIndexJobs")
        .WithSummary("Bulk index multiple job documents in Elasticsearch")
        .Accepts<List<JobSyncDto>>("application/json")
        .Produces<BulkSyncResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

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

    // Reads at most limit bytes whatever Content-Length says (absent for chunked bodies).
    // Returns null when the body is larger.
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

public enum IndexTokenDecision
{
    Allowed,
    Unauthorized,
    Unavailable,
}

public static class IndexTokenPolicy
{
    // An unset token is accepted only in Development. Otherwise the endpoints stay closed
    // (and Program.cs refuses to start), so a missing env var can never open index writes.
    public static IndexTokenDecision Evaluate(bool isDevelopment, string? expected, string provided)
    {
        if (expected is null)
        {
            return isDevelopment ? IndexTokenDecision.Allowed : IndexTokenDecision.Unavailable;
        }

        // Hashing both sides gives equal-length inputs, so FixedTimeEquals does not leak the
        // expected token's length (same approach as job-svc InternalStatsEndpoints).
        var match = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
        return match ? IndexTokenDecision.Allowed : IndexTokenDecision.Unauthorized;
    }
}
