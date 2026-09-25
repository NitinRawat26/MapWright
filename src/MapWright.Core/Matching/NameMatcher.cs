using MapWright.Core.Playbooks;
using MapWright.Core.Profile;

namespace MapWright.Core.Matching;

/// <summary>Field-name similarity used when no playbook links a target field to a source field.</summary>
public static class NameMatcher
{
    public const int MinimumScore = 50;
    public const int MaximumConfidence = 75;

    /// <summary>70 for the same name ignoring case and separators, 60 for an abbreviation (dba ~ DoingBusinessAs), up to 69 for shared words.</summary>
    public static int Score(ProfileField source, ProfileField target)
    {
        int score;
        if (PlaybookMatcher.Compact(source.Name) == PlaybookMatcher.Compact(target.Name))
        {
            score = 70;
        }
        else if (Abbreviates(source.Name, target.Name) || Abbreviates(target.Name, source.Name))
        {
            score = 60;
        }
        else
        {
            var similarity = Similarity(source.Name, target.Name);
            score = similarity >= 0.5 ? (int)(40 + (29 * similarity)) : 0;
        }

        return Compatible(source.DataType, target.DataType) ? score : score - 20;
    }

    /// <summary>A token of one name is the initials of the other's words.</summary>
    private static bool Abbreviates(string shortName, string longName)
    {
        var words = NameTokens.Split(longName).ToList();
        if (words.Count < 2)
        {
            return false;
        }

        var initials = string.Concat(words.Select(w => w[0]));
        return initials.Length >= 3 && NameTokens.Split(shortName).Contains(initials, StringComparer.Ordinal);
    }

    private static bool Compatible(FieldDataType a, FieldDataType b)
    {
        static int Family(FieldDataType t) => t switch
        {
            FieldDataType.Integer or FieldDataType.Decimal => 1,
            FieldDataType.Date or FieldDataType.DateTime => 2,
            FieldDataType.Boolean => 3,
            _ => 0,
        };

        return a == b || a == FieldDataType.Unknown || b == FieldDataType.Unknown || a == FieldDataType.String || b == FieldDataType.String || Family(a) == Family(b);
    }

    /// <summary>Share of name tokens the two names have in common (0–1).</summary>
    public static double Similarity(string a, string b)
    {
        var left = NameTokens.Split(a).ToHashSet(StringComparer.Ordinal);
        var right = NameTokens.Split(b).ToHashSet(StringComparer.Ordinal);
        return left.Count + right.Count == 0 ? 0 : (double)left.Intersect(right).Count() / left.Union(right).Count();
    }
}
