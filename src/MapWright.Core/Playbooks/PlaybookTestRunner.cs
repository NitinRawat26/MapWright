using System.Text.Json;

namespace MapWright.Core.Playbooks;

public enum PlaybookTestKind
{
    Detection,
    Derivation,
    Condition,
    Validation,
}

public sealed record PlaybookTestResult(string Playbook, PlaybookTestKind Kind, string Id, bool Passed, string? Message = null)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")} {Playbook} {Kind.ToString().ToLowerInvariant()} {Id}{(Message is null ? "" : $": {Message}")}";
}

/// <summary>Runs a playbook's own tests and rule examples. A playbook is publishable only when all pass.</summary>
public static class PlaybookTestRunner
{
    private const decimal Tolerance = 0.000001m;

    public static IReadOnlyList<PlaybookTestResult> Run(Playbook playbook)
    {
        if (playbook.Domain is not { } domain)
        {
            return [];
        }

        var results = new List<PlaybookTestResult>();
        var reference = playbook.Reference;

        foreach (var test in domain.Tests)
        {
            var failure = Check(playbook, test);
            results.Add(new(reference, PlaybookTestKind.Detection, test.Id, failure is null, failure));
        }

        foreach (var rule in domain.Derivations.Where(r => r.Expression is not null))
        {
            foreach (var (example, index) in rule.Examples.Select((e, i) => (e, i)))
            {
                results.Add(Example(reference, PlaybookTestKind.Derivation, $"{rule.Id}#{index + 1}", rule.Expression!, example, actual =>
                    actual.Number is { } n && Math.Abs(n - example.Expected.GetDecimal()) <= Tolerance));
            }
        }

        foreach (var rule in domain.Validations)
        {
            foreach (var (example, index) in rule.Examples.Select((e, i) => (e, i)))
            {
                results.Add(Example(reference, PlaybookTestKind.Validation, $"{rule.Id}#{index + 1}", rule.Expression, example, actual =>
                    actual.Bool == (example.Expected.ValueKind == JsonValueKind.True)));
            }
        }

        foreach (var rule in domain.Conditions)
        {
            foreach (var (example, index) in rule.Examples.Select((e, i) => (e, i)))
            {
                var actual = Evaluate(rule, example.Given);
                results.Add(new(reference, PlaybookTestKind.Condition, $"{rule.Id}#{index + 1}", actual == example.Expected,
                    actual == example.Expected ? null : $"expected '{example.Expected ?? "(none)"}', got '{actual ?? "(none)"}'"));
            }
        }

        return results;
    }

    /// <summary>First case whose clauses all hold (codes compared case-insensitively), else the rule's otherwise value.</summary>
    public static string? Evaluate(ConditionalRule rule, IReadOnlyDictionary<string, string> given) =>
        rule.Cases.FirstOrDefault(c => c.When.All(clause =>
            given.TryGetValue(clause.Concept, out var value) && clause.In.Contains(value, StringComparer.OrdinalIgnoreCase)))?.Then
        ?? rule.Otherwise;

    private static string? Check(Playbook playbook, DetectionTest test)
    {
        var result = PlaybookMatcher.Detect(playbook, test.Field);
        if (test.Expect is null)
        {
            return result is null ? null : $"expected no match, got {result.BusinessConcept} ({result.Score})";
        }

        if (result is null)
        {
            var best = PlaybookMatcher.ScoreAll(playbook, test.Field).FirstOrDefault();
            return $"expected {test.Expect}, got no match{(best is null ? "" : $" (best {best.BusinessConcept} at {best.Score})")}";
        }

        var problems = new List<string>();
        if (result.BusinessConcept != test.Expect)
        {
            problems.Add($"expected {test.Expect}, got {result.BusinessConcept}");
        }

        foreach (var (name, value) in test.ExpectQualifiers)
        {
            if (!result.Qualifiers.TryGetValue(name, out var actual) || actual != value)
            {
                problems.Add($"expected {name}={value}, got {(actual is null ? "none" : $"{name}={actual}")}");
            }
        }

        if (test.MinScore is { } min && result.Score < min)
        {
            problems.Add($"score {result.Score} below {min}");
        }

        if (test.ExpectReview is { } review && result.RequiresReview != review)
        {
            problems.Add(review ? "expected review to be required" : $"expected no review, got {string.Join(", ", result.Triggers)}");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static PlaybookTestResult Example(
        string reference,
        PlaybookTestKind kind,
        string id,
        string expression,
        ExpressionExample example,
        Func<ExprValue, bool> passes)
    {
        try
        {
            var inputs = example.Inputs.ToDictionary(p => p.Key, p => ExprValue.FromJson(p.Value), StringComparer.Ordinal);
            var actual = Expressions.Evaluate(Expressions.Parse(expression), inputs);
            return passes(actual)
                ? new(reference, kind, id, true)
                : new(reference, kind, id, false, $"expected {example.Expected}, got {actual}");
        }
        catch (ExpressionException ex)
        {
            return new(reference, kind, id, false, ex.Message);
        }
    }
}
