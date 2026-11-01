using Search.Api.Endpoints;
using Search.Api.Health;
using Search.Api.Observability;
using Search.Infrastructure.Extensions;
using Search.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// MAINT-06: Serilog structured logging + correlation id (PBL6-49)
builder.AddObservability();

// Elasticsearch Client & Initializer Registration (Zero hardcoding rule)
builder.Services.AddElasticsearchInfrastructure(builder.Configuration);
builder.Services.AddSearchHealthChecks();

// REL-07: ProblemDetails for RFC 7807 error responses
builder.Services.AddProblemDetails();

// MAINT-03: OpenAPI 3.0
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Search Service API", Version = "v0.1.0" });
});

var app = builder.Build();

app.UseObservability();

// REL-07: UseExceptionHandler maps unhandled exceptions -> ProblemDetails JSON
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// REL-06: Health check endpoint per service (6-nfr.md:REL-06, 8-system-architecture.md)
app.MapSearchHealth();

app.MapGet("/", () => Results.Ok(new { service = "search", version = "0.1.0" }))
   .ExcludeFromDescription();

// Search endpoints (SRS SEARCH-01)
app.MapSearchEndpoints();

// Indexing endpoints (HTTP Sync)
app.MapIndexEndpoints();

// S-1: loud signal when index auth is off — with the gateway routing
// /api/search/** publicly, an unset token leaves index writes open.
if (string.IsNullOrWhiteSpace(app.Configuration["SEARCH_INDEX_TOKEN"] ?? app.Configuration["IndexAuth:Token"]))
{
    app.Logger.LogWarning(
        "SEARCH_INDEX_TOKEN is not set. Index endpoints accept unauthenticated writes — set the token in non-dev environments.");
}

// Ensure Elasticsearch index exists on startup
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var initializer = scope.ServiceProvider.GetRequiredService<ElasticsearchInitializer>();

    try
    {
        await initializer.EnsureIndexCreatedAsync();
        logger.LogInformation("Elasticsearch index initialization verified.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not initialize Elasticsearch index on startup. Ensure ES container is running.");
        if (!app.Environment.IsDevelopment())
        {
            throw; // Fail-fast in staging/production
        }
    }
}

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
