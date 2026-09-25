using System.Text;

namespace MapWright.Core.Playbooks;

/// <summary>Splits identifiers and phrases into comparable tokens: "annualCardVolume", "annual_card_volume" and "Annual Card Volume" all give [annual, card, volume].</summary>
public static class NameTokens
{
    public static IReadOnlyList<string> Split(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(Stem(current.ToString().ToLowerInvariant()));
                current.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var previous = text[i - 1];
                var lowerToUpper = char.IsUpper(c) && (char.IsLower(previous) || char.IsDigit(previous));
                var acronymEnd = char.IsUpper(c) && char.IsUpper(previous) && i + 1 < text.Length && char.IsLower(text[i + 1]);
                var letterDigit = char.IsDigit(c) != char.IsDigit(previous);
                if (lowerToUpper || acronymEnd || letterDigit)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        return tokens;
    }

    /// <summary>Length of the longest token window of <paramref name="field"/> equal to <paramref name="term"/> (0 when absent); compares joined text so "date of birth" matches "dateOfBirth" and "DOB" only matches "dob".</summary>
    public static int Match(IReadOnlyList<string> term, IReadOnlyList<string> field)
    {
        if (term.Count == 0)
        {
            return 0;
        }

        var joinedTerm = string.Concat(term);
        for (var length = field.Count; length >= 1; length--)
        {
            for (var start = 0; start + length <= field.Count; start++)
            {
                if (string.Concat(field.Skip(start).Take(length)) == joinedTerm)
                {
                    return term.Count;
                }
            }
        }

        return 0;
    }

    private static string Stem(string token) =>
        token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal) ? token[..^1] : token;
}
