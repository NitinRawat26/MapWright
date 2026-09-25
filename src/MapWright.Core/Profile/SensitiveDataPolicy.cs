using System.Text;
using System.Text.RegularExpressions;

namespace MapWright.Core.Profile;

/// <summary>Decides which fields are sensitive and masks their values before they are stored in a profile.</summary>
public sealed partial class SensitiveDataPolicy
{
    public const int VisibleTrailingCharacters = 4;

    public static IReadOnlyList<string> DefaultTerms { get; } =
    [
        "ssn", "sin", "tin", "itin", "ein", "fein", "taxid", "taxidentification", "socialsecurity", "nationalid",
        "passport", "driverlicense", "driverslicense", "accountnumber", "accountno", "acctnumber", "acctno",
        "routing", "aba", "iban", "cardnumber", "pan", "cvv", "cvc", "dob", "dateofbirth", "birthdate",
    ];

    /// <summary>Generic leaf names that inherit sensitivity from their parent (e.g. TaxId/Number).</summary>
    public static IReadOnlyList<string> GenericLeafNames { get; } = ["number", "value", "id", "no", "num", "text()"];

    public static SensitiveDataPolicy Default { get; } = new(DefaultTerms);

    private readonly string[] _terms;

    public SensitiveDataPolicy(IEnumerable<string> terms)
    {
        _terms = [.. terms.Select(Compact).Where(t => t.Length > 0).Distinct()];
    }

    public IReadOnlyList<string> Terms => _terms;

    /// <summary>Returns a human-readable reason when the field name (or a generic leaf's parent name) is sensitive.</summary>
    public string? MatchField(string name, string? parentName)
    {
        if (MatchName(name) is { } term)
        {
            return $"Field name matches sensitive term '{term}'.";
        }

        if (parentName is not null
            && GenericLeafNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            && MatchName(parentName) is { } parentTerm)
        {
            return $"Parent '{parentName}' matches sensitive term '{parentTerm}'.";
        }

        return null;
    }

    public string? MatchName(string name)
    {
        var tokens = Tokens().Matches(name).Select(m => m.Value.ToLowerInvariant()).ToArray();
        var compact = string.Concat(tokens);

        // Short terms must match a whole token so that e.g. "company" does not match "pan".
        return _terms.FirstOrDefault(term => term.Length <= 4 ? tokens.Contains(term) : compact.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>9 digits (SSN/EIN/routing) or 13–19 digits (card numbers), optionally with separators.</summary>
    public static bool LooksLikeIdentifier(string value)
    {
        if (!IdentifierCharacters().IsMatch(value) || DecimalNumber().IsMatch(value))
        {
            return false;
        }

        var digits = value.Count(char.IsAsciiDigit);
        return digits == 9 || digits is >= 13 and <= 19;
    }

    /// <summary>Keeps the last four letters/digits and replaces the rest with '*', preserving separators.</summary>
    public static string Mask(string value)
    {
        var alphanumerics = value.Count(char.IsLetterOrDigit);
        if (alphanumerics <= VisibleTrailingCharacters)
        {
            return value;
        }

        var toHide = alphanumerics - VisibleTrailingCharacters;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) && toHide > 0)
            {
                builder.Append('*');
                toHide--;
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string Compact(string term) =>
        string.Concat(term.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"^[\d\s\-().\/]+$")]
    private static partial Regex IdentifierCharacters();

    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex DecimalNumber();
}
