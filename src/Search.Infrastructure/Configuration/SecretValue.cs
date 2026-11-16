using System.Text.RegularExpressions;

namespace Search.Infrastructure.Configuration;

/// <summary>
/// Normalises configured secrets. Template placeholders such as <c>__SET_VIA_INFISICAL__</c>
/// (pattern <c>__[A-Z][A-Z0-9_]*__</c>) are treated as unset: a copied env template must never
/// start a service with a publicly known value as its token.
/// </summary>
public static class SecretValue
{
    private static readonly Regex PlaceholderPattern = new(
        "^__[A-Z][A-Z0-9_]*__$",
        RegexOptions.CultureInvariant);

    /// <summary>Trimmed value, or <c>null</c> when unset, blank or a template placeholder.</summary>
    public static string? Usable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return IsPlaceholder(trimmed) ? null : trimmed;
    }

    /// <summary>True when the value is a <c>__UPPER_SNAKE__</c> template placeholder.</summary>
    public static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value) && PlaceholderPattern.IsMatch(value.Trim());
}
