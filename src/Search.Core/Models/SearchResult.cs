namespace Search.Core.Models;

public record SearchResult<T>(
    IReadOnlyList<T> Items,
    long Total,
    int Page,
    int Size,
    int TotalPages,
    string? Message = null
)
{
    public static SearchResult<T> Empty(int page = 0, int size = 20, string message = "No jobs found matching your criteria")
        => new(Array.Empty<T>(), 0, page, size, 0, message);

    public static SearchResult<T> Create(IReadOnlyList<T> items, long total, int page, int size)
    {
        var totalPages = size > 0 ? (int)Math.Ceiling((double)total / size) : 0;
        return new(items, total, page, size, totalPages);
    }
}
