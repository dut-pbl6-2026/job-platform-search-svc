using Search.Core.Models;

namespace Search.Core.Interfaces;

public interface ISearchService
{
    Task<SearchResult<JobDocument>> SearchJobsAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit = 10, CancellationToken cancellationToken = default);
    /// <summary>Current indexed document, or null when it does not exist. Throws when ES fails.</summary>
    Task<JobDocument?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<bool> IndexJobAsync(JobDocument document, CancellationToken cancellationToken = default);
    Task<int> BulkIndexJobsAsync(IEnumerable<JobDocument> documents, CancellationToken cancellationToken = default);
    Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken = default);
}
