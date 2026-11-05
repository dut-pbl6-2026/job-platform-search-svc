namespace Search.Core.Models;

public record SearchQuery(
    string? Keyword = null,
    string? Location = null,
    string? Category = null,
    string? EmploymentType = null,
    string? ExperienceLevel = null,
    decimal? MinSalary = null,
    decimal? MaxSalary = null,
    string? Skills = null,
    int Page = 0,
    int Size = 20,
    string? SortBy = null,
    string? SkillOp = null
)
{
    /// <summary>Accepted values of <see cref="SkillOp"/> (api-conventions §4). Default is OR.</summary>
    public const string SkillOpAnd = "and";
    public const string SkillOpOr = "or";

    /// <summary>True when the job must have every requested skill (skillOp=and).</summary>
    public bool MatchAllSkills =>
        string.Equals(SkillOp?.Trim(), SkillOpAnd, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidSkillOp(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), SkillOpAnd, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), SkillOpOr, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Multi-value filters are comma-separated ("FullTime,PartTime"): values are
    /// trimmed, empties dropped, duplicates removed (case-insensitive), order kept.
    /// </summary>
    public static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public IReadOnlyList<string> EmploymentTypes => SplitCsv(EmploymentType);
    public IReadOnlyList<string> ExperienceLevels => SplitCsv(ExperienceLevel);
    public IReadOnlyList<string> SkillList => SplitCsv(Skills);

    public int NormalizedPage => Page < 0 ? 0 : Page;

    public int NormalizedSize => Size switch
    {
        < 1 => 20,
        > 100 => 100,
        _ => Size
    };

    public int From => NormalizedPage * NormalizedSize;
}
