namespace Search.Core.Models;

/// <summary>A job returned by semantic (kNN) search with its cosine similarity to the query.</summary>
public record SemanticJobHit(JobDocument Job, float Score);
