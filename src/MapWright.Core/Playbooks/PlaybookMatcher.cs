using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Playbooks;

/// <summary>How a domain playbook recognised a field.</summary>
public sealed record DetectionResult
{
    public required string Playbook { get; init; }
    public required string Concept { get; init; }

    /// <summary>Null when the field is the concept itself (e.g. the owners list).</summary>
    public string? Attribute { get; init; }
    public required int Score { get; init; }
    public IReadOnlyDictionary<string, string> Qualifiers { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<ReviewTrigger> Triggers { get; init; } = [];
    public bool RequiresReview { get; init; }
    public IReadOnlyList<string> Questions { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>"Concept" or "Concept.Attribute", as recorded in a mapping's businessConcept.</summary>
    public string BusinessConcept => Attribute is null ? Concept : $"{Concept}.{Attribute}";

    /// <summary>Tie-breaker: how many name tokens the winning term covered.</summary>
    internal int MatchedTokens { get; init; }
}

/// <summary>Scores a field against a domain playbook's vocabulary, context and signals.</summary>
public static class PlaybookMatcher
{
    /// <summary>Leaf names that say nothing on their own; the parent name is used with them (TaxId/Number, TaxId/@type).</summary>
    public static IReadOnlyList<string> GenericLeafNames { get; } = [.. SensitiveDataPolicy.GenericLeafNames, "type", "code"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    public static Regex Pattern(string pattern) => RegexCache.GetOrAdd(
        pattern,
        p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));

    /// <summary>The best-scoring concept or attribute, or null when nothing reaches the playbook's match threshold.</summary>
    public static DetectionResult? Detect(Playbook playbook, FieldContext field) =>
        ScoreAll(playbook, field).FirstOrDefault(r => r.Score >= Rules(playbook).MatchThreshold);

    /// <summary>Every candidate with a positive score, best first.</summary>
    public static IReadOnlyList<DetectionResult> ScoreAll(Playbook playbook, FieldContext field)
    {
        var domain = playbook.Domain ?? throw new ArgumentException($"{playbook.Reference} is not a domain playbook.", nameof(playbook));
        var (nameTokens, context) = NameAndContext(field);

        var targets = new List<string?> { null };
        targets.AddRange(domain.Concept.Attributes.Select(a => a.Name));

        return [.. targets
            .Select(t => Score(playbook, domain, field, nameTokens, context, t))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.MatchedTokens)
            .ThenBy(r => r.Attribute is null ? 1 : 0)];
    }

    public static string? ResolveCode(ValueMapDefinition map, string value)
    {
        var key = Compact(value);
        return map.Values.FirstOrDefault(v =>
            Compact(v.Code) == key
            || (v.Label is not null && Compact(v.Label) == key)
            || v.Aliases.Any(a => Compact(a) == key))?.Code;
    }

    internal static string Compact(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static ConfidenceRules Rules(Playbook playbook) => playbook.Domain?.Confidence ?? new();

    private static (IReadOnlyList<string> Name, IReadOnlyList<string> Context) NameAndContext(FieldContext field)
    {
        if (field.Ancestors.Count > 0 && GenericLeafNames.Contains(field.Name.TrimStart('@'), StringComparer.OrdinalIgnoreCase))
        {
            var name = field.Name == "text()" ? [] : NameTokens.Split(field.Name);
            return ([.. NameTokens.Split(field.Ancestors[0]), .. name], [.. field.Ancestors.Skip(1)]);
        }

        return (NameTokens.Split(field.Name), field.Ancestors);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DetectionResult Score(
        Playbook playbook,
        DomainDefinition domain,
        FieldContext field,
        IReadOnlyList<string> nameTokens,
        IReadOnlyList<string> context,
        string? target)
    {
        var rules = domain.Confidence;
        var score = 0;
        var evidence = new List<string>();
        var triggers = new List<ReviewTrigger>();

        var attribute = target is null ? null : domain.Concept.Attributes.First(a => Same(a.Name, target));
        if (attribute is null ? field.Kind != FieldNodeKind.Object : field.Kind == FieldNodeKind.Object && attribute.DataType != FieldDataType.Object)
        {
            return Empty(playbook, domain, target);
        }

        var terms = new List<VocabularyTerm> { new() { Term = target ?? domain.Concept.Name, AppliesTo = target } };
        terms.AddRange(domain.Vocabulary.Where(v => Same(v.AppliesTo, target)));

        var best = terms
            .Select(t => (Term: t, Tokens: NameTokens.Match(NameTokens.Split(t.Term), nameTokens), Points: Points(rules, t.Relation)))
            .Where(m => m.Tokens > 0)
            .OrderByDescending(m => m.Points)
            .ThenByDescending(m => m.Tokens)
            .FirstOrDefault();
        if (best.Tokens > 0)
        {
            score += best.Points;
            evidence.Add($"Name matches {best.Term.Relation.ToString().ToLowerInvariant()} term '{best.Term.Term}' (+{best.Points}).");
            if (best.Term.Relation != TermRelation.Equivalent)
            {
                triggers.Add(ReviewTrigger.NonEquivalentTerm);
            }
        }

        if (target is not null)
        {
            var conceptTerms = domain.Vocabulary.Where(v => v.AppliesTo is null).Select(v => v.Term).Prepend(domain.Concept.Name);
            var ancestor = context.FirstOrDefault(a => conceptTerms.Any(t => NameTokens.Match(NameTokens.Split(t), NameTokens.Split(a)) > 0));
            if (ancestor is not null)
            {
                score += rules.ContextPoints;
                evidence.Add($"Ancestor '{ancestor}' names the {domain.Concept.Name} concept (+{rules.ContextPoints}).");
            }
            else if (attribute!.RequiresContext)
            {
                return Empty(playbook, domain, target);
            }
        }

        foreach (var signal in domain.Signals.Where(s => Same(s.AppliesTo, target)))
        {
            if (Matches(signal, field, domain))
            {
                score += signal.Weight;
                evidence.Add($"Signal {signal.Id}{(signal.Note is null ? "" : $" ({signal.Note})")} ({signal.Weight:+#;-#;0}).");
            }
        }

        var warnings = new List<string>();
        var qualifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (attribute is not null)
        {
            if (attribute.Sensitivity != Sensitivity.None)
            {
                triggers.Add(ReviewTrigger.SensitiveAttribute);
            }

            var description = field.Description is null ? [] : NameTokens.Split(field.Description);
            foreach (var qualifier in domain.Qualifiers.Where(q => q.AppliesTo.Count == 0 || q.AppliesTo.Any(a => Same(a, target))))
            {
                ResolveQualifier(qualifier, field, [.. nameTokens, .. description], qualifiers, evidence, warnings, triggers);
            }
        }

        var triggered = triggers.Distinct().ToList();
        return new()
        {
            Playbook = playbook.Reference,
            Concept = domain.Concept.Name,
            Attribute = target,
            Score = Math.Clamp(score, 0, 100),
            Qualifiers = qualifiers,
            Evidence = evidence,
            Triggers = triggered,
            RequiresReview = triggered.Any(rules.ReviewTriggers.Contains),
            Questions = [.. domain.ReviewGuidance
                .Where(q => Same(q.AppliesTo, target) && (q.When is null || triggered.Contains(q.When.Value)))
                .Select(q => q.Question)],
            Warnings = [.. warnings, .. domain.Risks.Where(r => r.AppliesTo is null || Same(r.AppliesTo, target)).Select(r => r.Text)],
            MatchedTokens = best.Tokens,
        };
    }

    private static DetectionResult Empty(Playbook playbook, DomainDefinition domain, string? target) =>
        new() { Playbook = playbook.Reference, Concept = domain.Concept.Name, Attribute = target, Score = 0 };

    private static int Points(ConfidenceRules rules, TermRelation relation) => relation switch
    {
        TermRelation.Equivalent => rules.EquivalentTermPoints,
        TermRelation.Narrower => rules.NarrowerTermPoints,
        TermRelation.Broader => rules.BroaderTermPoints,
        _ => rules.RelatedTermPoints,
    };

    private static void ResolveQualifier(
        Qualifier qualifier,
        FieldContext field,
        IReadOnlyList<string> tokens,
        Dictionary<string, string> qualifiers,
        List<string> evidence,
        List<string> warnings,
        List<ReviewTrigger> triggers)
    {
        var byName = qualifier.Values.FirstOrDefault(v => v.Terms.Any(t => NameTokens.Match(NameTokens.Split(t), tokens) > 0));
        var byValues = field is { MinValue: { } min, MaxValue: { } max }
            ? qualifier.Values.FirstOrDefault(v => v.MinValue is not null && v.MaxValue is not null && min >= v.MinValue && max <= v.MaxValue)
            : null;

        if (byName is not null && byValues is not null && byName != byValues)
        {
            qualifiers[qualifier.Name] = byValues.Value;
            evidence.Add($"{qualifier.Name}={byValues.Value} from observed values {field.MinValue}–{field.MaxValue}.");
            warnings.Add($"Name says {qualifier.Name} '{byName.Value}' but observed values {field.MinValue}–{field.MaxValue} look like '{byValues.Value}'.");
            triggers.Add(ReviewTrigger.QualifierConflict);
        }
        else if ((byName ?? byValues) is { } found)
        {
            qualifiers[qualifier.Name] = found.Value;
            evidence.Add(byName is not null
                ? $"{qualifier.Name}={found.Value} from the name."
                : $"{qualifier.Name}={found.Value} from observed values {field.MinValue}–{field.MaxValue}.");
        }
        else if (qualifier.Default is { } assumed)
        {
            qualifiers[qualifier.Name] = assumed;
            evidence.Add($"{qualifier.Name}={assumed} assumed (playbook default).");
            triggers.Add(ReviewTrigger.AssumedQualifier);
        }
    }

    private static bool Matches(DetectionSignal signal, FieldContext field, DomainDefinition domain)
    {
        var pattern = signal.Pattern ?? "";
        return signal.Kind switch
        {
            SignalKind.NamePattern => Pattern(pattern).IsMatch(field.Name),
            SignalKind.AncestorNamePattern => field.Ancestors.Any(Pattern(pattern).IsMatch),
            SignalKind.ChildNamePattern => field.Children.Any(Pattern(pattern).IsMatch),
            SignalKind.DescriptionPattern => field.Description is { } d && Pattern(pattern).IsMatch(d),
            SignalKind.ValuePattern => field.Values.Count > 0 && field.Values.All(Pattern(pattern).IsMatch),
            SignalKind.ValueShape => field.ValueShapes.Count > 0 && field.ValueShapes.All(Pattern(pattern).IsMatch),
            SignalKind.ValueRange => field is { MinValue: { } min, MaxValue: { } max }
                && (signal.MinValue is null || min >= signal.MinValue)
                && (signal.MaxValue is null || max <= signal.MaxValue),
            SignalKind.ValueInMap => domain.ValueMaps.FirstOrDefault(m => m.Id == pattern) is { } map
                && field.Values.Count > 0
                && field.Values.Count(v => ResolveCode(map, v) is not null) * 2 >= field.Values.Count,
            SignalKind.DataType => pattern.Split('|').Any(t => Enum.TryParse<FieldDataType>(t.Trim(), ignoreCase: true, out var type) && type == field.DataType),
            SignalKind.Cardinality => Enum.TryParse<Cardinality>(pattern, ignoreCase: true, out var cardinality) && cardinality == field.Cardinality,
            _ => false,
        };
    }
}
