namespace Search.Core.Models;

public record SearchQuery(
    string? Keyword = null,
    string? Location = null,
    string? Category = null,
    string? EmploymentType = null,
    string? ExperienceLevel = null,
    decimal? MinSalary = null,
    decimal? MaxSalary = null,
    int Page = 0,
    int Size = 20,
    string? SortBy = null
)
{
    public int NormalizedPage => Page < 0 ? 0 : Page;

    public int NormalizedSize => Size switch
    {
        < 1 => 20,
        > 100 => 100,
        _ => Size
    };

    public int From => NormalizedPage * NormalizedSize;
}
