using System.Text.Json;
using System.Text.RegularExpressions;
using MapWright.Core.Spec;

namespace MapWright.Core.Playbooks;

/// <summary>Checks a playbook, or a set of playbooks, for errors an editor must fix before publishing.</summary>
public static partial class PlaybookValidator
{
    private static readonly string[] OutputFormats = ["json", "xlsx", "csv", "html"];

    public static IReadOnlyList<SpecIssue> Validate(Playbook playbook)
    {
        var issues = new Issues(playbook.Reference);

        if (playbook.SpecVersion != Playbook.CurrentSpecVersion)
        {
            issues.Error("PB001", "specVersion", $"Unsupported playbook version '{playbook.SpecVersion}'; expected '{Playbook.CurrentSpecVersion}'.");
        }

        var prefix = playbook.Kind == PlaybookKind.Domain ? "domain/" : "process/";
        if (!IdPattern().IsMatch(playbook.Id) || !playbook.Id.StartsWith(prefix, StringComparison.Ordinal))
        {
            issues.Error("PB002", "id", $"Id must look like '{prefix}<lower-case-slug>'.");
        }

        if (!SemVer().IsMatch(playbook.Version))
        {
            issues.Error("PB003", "version", "Version must be MAJOR.MINOR.PATCH, e.g. 1.0.0.");
        }

        if (string.IsNullOrWhiteSpace(playbook.Name))
        {
            issues.Error("PB004", "name", "Name is required.");
        }

        if (playbook.Status == PlaybookStatus.Published && !playbook.ChangeNotes.Any(c => c.Version == playbook.Version))
        {
            issues.Warn("PB006", "changeNotes", $"No change note for published version {playbook.Version}.");
        }

        switch (playbook.Kind, playbook.Domain, playbook.Process)
        {
            case (PlaybookKind.Domain, { } domain, null):
                ValidateDomain(playbook, domain, issues);
                break;
            case (PlaybookKind.Process, null, { } process):
                ValidateProcess(process, issues);
                break;
            default:
                issues.Error("PB005", "kind", $"A {playbook.Kind.ToString().ToLowerInvariant()} playbook needs exactly one '{playbook.Kind.ToString().ToLowerInvariant()}' section.");
                break;
        }

        return issues.List;
    }

    /// <summary>Validates each playbook and the references between them.</summary>
    public static IReadOnlyList<SpecIssue> Validate(IReadOnlyList<Playbook> playbooks)
    {
        var all = playbooks.SelectMany(Validate).ToList();
        var set = new Issues("library");

        foreach (var duplicate in playbooks.GroupBy(p => p.Reference).Where(g => g.Count() > 1))
        {
            set.Error("PB040", duplicate.Key, "Playbook version defined more than once.");
        }

        foreach (var published in playbooks.Where(p => p.Status == PlaybookStatus.Published).GroupBy(p => p.Id).Where(g => g.Count() > 1))
        {
            set.Error("PB041", published.Key, $"Only one version may be published; found {string.Join(", ", published.Select(p => p.Version))}.");
        }

        var domains = playbooks.Where(p => p.Domain is not null).ToList();
        var attributeOwners = new Dictionary<string, Playbook>(StringComparer.OrdinalIgnoreCase);
        foreach (var playbook in domains.GroupBy(p => p.Id).Select(g => g.Last()))
        {
            foreach (var attribute in playbook.Domain!.Concept.Attributes)
            {
                var key = $"{playbook.Domain.Concept.Name}.{attribute.Name}";
                if (!attributeOwners.TryAdd(key, playbook) && attributeOwners[key].Id != playbook.Id)
                {
                    set.Error("PB046", key, $"Defined by both {attributeOwners[key].Id} and {playbook.Id}.");
                }
            }
        }

        var domainIds = domains.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var playbook in playbooks)
        {
            foreach (var step in playbook.Process?.Steps ?? [])
            {
                foreach (var use in step.Uses.Where(u => !domainIds.Contains(u)))
                {
                    set.Error("PB042", $"{playbook.Reference} steps[{step.Id}]", $"Uses unknown domain playbook '{use}'.");
                }
            }

            foreach (var rule in playbook.Domain?.Conditions ?? [])
            {
                foreach (var clause in rule.Cases.SelectMany(c => c.When))
                {
                    var at = $"{playbook.Reference} conditions[{rule.Id}]";
                    if (!attributeOwners.TryGetValue(clause.Concept, out var owner))
                    {
                        set.Error("PB043", at, $"'{clause.Concept}' is not an attribute of any domain playbook.");
                        continue;
                    }

                    var attribute = clause.Concept[(clause.Concept.IndexOf('.', StringComparison.Ordinal) + 1)..];
                    if (owner.Domain!.ValueMaps.FirstOrDefault(m => string.Equals(m.Attribute, attribute, StringComparison.OrdinalIgnoreCase)) is { } map)
                    {
                        foreach (var value in clause.In.Where(v => !map.Values.Any(c => c.Code == v)))
                        {
                            set.Error("PB044", at, $"'{value}' is not a code of {owner.Id} value map '{map.Id}'.");
                        }
                    }
                }
            }
        }

        return [.. all, .. set.List];
    }

    private static void ValidateDomain(Playbook playbook, DomainDefinition domain, Issues issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Id(string section, string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
            {
                issues.Error("PB009", section, $"Rule id '{id}' is missing or used more than once.");
            }
        }

        if (!PascalName().IsMatch(domain.Concept.Name))
        {
            issues.Error("PB010", "concept.name", "Concept name must be PascalCase, e.g. Principal.");
        }

        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (domain.Concept.Attributes.Count == 0)
        {
            issues.Error("PB010", "concept.attributes", "A concept needs at least one attribute.");
        }

        foreach (var attribute in domain.Concept.Attributes)
        {
            if (!PascalName().IsMatch(attribute.Name) || !attributes.Add(attribute.Name))
            {
                issues.Error("PB010", $"concept.attributes[{attribute.Name}]", "Attribute names must be unique and PascalCase.");
            }
        }

        void Attribute(string at, string? name, bool allowConcept = true)
        {
            if (name is null ? !allowConcept : !attributes.Contains(name))
            {
                issues.Error("PB011", at, name is null ? "An attribute is required." : $"Unknown attribute '{name}'.");
            }
        }

        var terms = new Dictionary<string, VocabularyTerm>(StringComparer.Ordinal);
        foreach (var term in domain.Vocabulary)
        {
            var at = $"vocabulary['{term.Term}']";
            Attribute(at, term.AppliesTo);
            var key = string.Concat(NameTokens.Split(term.Term));
            if (key.Length == 0)
            {
                issues.Error("PB012", at, "Term has no letters or digits.");
            }
            else if (!terms.TryAdd(key, term))
            {
                var other = terms[key];
                if (string.Equals(other.AppliesTo, term.AppliesTo, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Warn("PB012", at, "Duplicate term.");
                }
                else if (other.Relation == TermRelation.Equivalent && term.Relation == TermRelation.Equivalent)
                {
                    issues.Error("PB012", at, $"Equivalent term for both '{other.AppliesTo ?? domain.Concept.Name}' and '{term.AppliesTo ?? domain.Concept.Name}'.");
                }
            }
        }

        var qualifiers = new Dictionary<string, Qualifier>(StringComparer.Ordinal);
        foreach (var qualifier in domain.Qualifiers)
        {
            var at = $"qualifiers[{qualifier.Name}]";
            if (!qualifiers.TryAdd(qualifier.Name, qualifier))
            {
                issues.Error("PB013", at, "Duplicate qualifier.");
            }

            foreach (var applies in qualifier.AppliesTo)
            {
                Attribute(at, applies);
            }

            if (qualifier.Values.Count == 0 || qualifier.Values.GroupBy(v => v.Value).Any(g => g.Count() > 1))
            {
                issues.Error("PB013", at, "Qualifier values must be present and unique.");
            }

            if (qualifier.Default is { } d && !qualifier.Values.Any(v => v.Value == d))
            {
                issues.Error("PB013", at, $"Default '{d}' is not one of the values.");
            }

            foreach (var value in qualifier.Values.Where(v => v.MinValue > v.MaxValue))
            {
                issues.Error("PB013", at, $"Value '{value.Value}' has minValue above maxValue.");
            }
        }

        void Ref(string at, string attribute, IReadOnlyDictionary<string, string> refQualifiers)
        {
            Attribute(at, attribute, allowConcept: false);
            foreach (var (name, value) in refQualifiers)
            {
                if (!qualifiers.TryGetValue(name, out var q) || !q.Values.Any(v => v.Value == value))
                {
                    issues.Error("PB013", at, $"Unknown qualifier {name}={value}.");
                }
            }
        }

        var valueMaps = domain.ValueMaps.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var signal in domain.Signals)
        {
            var at = $"signals[{signal.Id}]";
            Id(at, signal.Id);
            Attribute(at, signal.AppliesTo);
            if (signal.Weight is < -100 or > 100)
            {
                issues.Error("PB014", at, "Weight must be between -100 and 100.");
            }

            switch (signal.Kind)
            {
                case SignalKind.ValueRange when signal.MinValue is null && signal.MaxValue is null:
                    issues.Error("PB014", at, "ValueRange needs minValue and/or maxValue.");
                    break;
                case SignalKind.ValueRange:
                    break;
                case SignalKind.ValueInMap when signal.Pattern is null || !valueMaps.Contains(signal.Pattern):
                    issues.Error("PB014", at, $"Pattern must name a value map of this playbook; got '{signal.Pattern}'.");
                    break;
                case SignalKind.DataType when signal.Pattern is null
                    || signal.Pattern.Split('|').Any(t => !Enum.TryParse<Profile.FieldDataType>(t.Trim(), ignoreCase: true, out _)):
                    issues.Error("PB014", at, "Pattern must be data types separated by '|', e.g. integer|decimal.");
                    break;
                case SignalKind.Cardinality when !Enum.TryParse<Cardinality>(signal.Pattern, ignoreCase: true, out _):
                    issues.Error("PB014", at, "Pattern must be 'array' or 'single'.");
                    break;
                case SignalKind.ValueInMap or SignalKind.DataType or SignalKind.Cardinality:
                    break;
                default:
                    CheckRegex(at, signal.Pattern, issues);
                    break;
            }
        }

        foreach (var rule in domain.Derivations)
        {
            var at = $"derivations[{rule.Id}]";
            Id(at, rule.Id);
            Ref(at, rule.Output.Attribute, rule.Output.Qualifiers);
            foreach (var input in rule.Inputs)
            {
                Ref(at, input.Attribute, input.Qualifiers);
            }

            if (rule.Expression is { } expression)
            {
                CheckExpression(at, expression, rule.Inputs, expectComparison: false, rule.Examples, issues);
            }
            else if (rule.Transformation is TransformationType.PeriodConversion or TransformationType.UnitConversion or TransformationType.Aggregate
                || rule.Examples.Count > 0)
            {
                issues.Error("PB015", at, $"{rule.Transformation} derivations and derivations with examples need an expression.");
            }
        }

        foreach (var rule in domain.Conditions)
        {
            var at = $"conditions[{rule.Id}]";
            Id(at, rule.Id);
            Ref(at, rule.Output.Attribute, rule.Output.Qualifiers);
            if (rule.Cases.Count == 0)
            {
                issues.Error("PB016", at, "A conditional rule needs at least one case.");
            }

            foreach (var clause in rule.Cases.SelectMany(c => c.When))
            {
                if (!ConceptRef().IsMatch(clause.Concept) || clause.In.Count == 0)
                {
                    issues.Error("PB016", at, $"Clause '{clause.Concept}' must reference Concept.Attribute and list at least one value.");
                }
            }

            if (rule.Cases.Any(c => c.When.Count == 0))
            {
                issues.Error("PB016", at, "Every case needs at least one clause.");
            }

            var outputMap = domain.ValueMaps.FirstOrDefault(m => string.Equals(m.Attribute, rule.Output.Attribute, StringComparison.OrdinalIgnoreCase));
            var results = rule.Cases.Select(c => c.Then).Append(rule.Otherwise).OfType<string>();
            foreach (var value in results.Where(v => outputMap is not null && !outputMap.Values.Any(c => c.Code == v)))
            {
                issues.Error("PB016", at, $"'{value}' is not a code of value map '{outputMap!.Id}'.");
            }

            var conceptsUsed = rule.Cases.SelectMany(c => c.When).Select(c => c.Concept).ToHashSet(StringComparer.Ordinal);
            foreach (var example in rule.Examples)
            {
                foreach (var key in example.Given.Keys.Where(k => !conceptsUsed.Contains(k)))
                {
                    issues.Error("PB016", at, $"Example gives '{key}', which no clause uses.");
                }
            }
        }

        foreach (var map in domain.ValueMaps)
        {
            var at = $"valueMaps[{map.Id}]";
            Id(at, map.Id);
            Attribute(at, map.Attribute, allowConcept: false);
            if (map.Values.Count == 0 || map.Values.GroupBy(v => v.Code).Any(g => g.Count() > 1))
            {
                issues.Error("PB017", at, "Codes must be present and unique.");
            }

            var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in map.Values)
            {
                foreach (var spelling in value.Aliases.Append(value.Code).Concat(value.Label is null ? [] : [value.Label]))
                {
                    var key = PlaybookMatcher.Compact(spelling);
                    if (spellings.TryGetValue(key, out var code) && code != value.Code)
                    {
                        issues.Error("PB017", at, $"'{spelling}' is used for both {code} and {value.Code}.");
                    }

                    spellings.TryAdd(key, value.Code);
                }
            }
        }

        foreach (var rule in domain.Validations)
        {
            var at = $"validations[{rule.Id}]";
            Id(at, rule.Id);
            foreach (var input in rule.Inputs)
            {
                Ref(at, input.Attribute, input.Qualifiers);
            }

            CheckExpression(at, rule.Expression, rule.Inputs, expectComparison: true, rule.Examples, issues);
        }

        var c = domain.Confidence;
        if (new[] { c.EquivalentTermPoints, c.NarrowerTermPoints, c.BroaderTermPoints, c.RelatedTermPoints, c.ContextPoints, c.MatchThreshold }.Any(v => v is < 0 or > 100))
        {
            issues.Error("PB019", "confidence", "Points and thresholds must be between 0 and 100.");
        }

        foreach (var risk in domain.Risks)
        {
            Id($"risks[{risk.Id}]", risk.Id);
            Attribute($"risks[{risk.Id}]", risk.AppliesTo);
        }

        foreach (var question in domain.ReviewGuidance)
        {
            Id($"reviewGuidance[{question.Id}]", question.Id);
            Attribute($"reviewGuidance[{question.Id}]", question.AppliesTo);
        }

        foreach (var test in domain.Tests)
        {
            var at = $"tests[{test.Id}]";
            Id(at, test.Id);
            if (test.Expect is { } expect)
            {
                var parts = expect.Split('.');
                if (parts[0] != domain.Concept.Name || parts.Length > 2)
                {
                    issues.Error("PB020", at, $"Expect must be '{domain.Concept.Name}' or '{domain.Concept.Name}.<Attribute>'.");
                }
                else if (parts.Length == 2)
                {
                    Attribute(at, parts[1]);
                }
            }
        }

        if (playbook.Status == PlaybookStatus.Published && domain.Tests.Count == 0)
        {
            issues.Error("PB021", "tests", "A published domain playbook needs at least one test.");
        }
    }

    private static void ValidateProcess(ProcessDefinition process, Issues issues)
    {
        var t = process.Thresholds;
        if (!(0 <= t.RejectBelow && t.RejectBelow <= t.ReviewBelow && t.ReviewBelow <= t.AutoAcceptAt && t.AutoAcceptAt <= 100))
        {
            issues.Error("PB033", "thresholds", "Thresholds must satisfy 0 <= rejectBelow <= reviewBelow <= autoAcceptAt <= 100.");
        }

        if (process.Steps.Count == 0)
        {
            issues.Error("PB030", "steps", "A process needs at least one step.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (step, index) in process.Steps.Select((s, i) => (s, i)))
        {
            var at = $"steps[{step.Id}]";
            if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add(step.Id))
            {
                issues.Error("PB030", at, "Step ids must be present and unique.");
            }

            if (step.Kind == StepKind.AiAssist)
            {
                if (step.MaxConfidence is not { } max || max >= t.AutoAcceptAt || max < 0)
                {
                    issues.Error("PB031", at, $"AI steps need maxConfidence below autoAcceptAt ({t.AutoAcceptAt}) so AI-only suggestions are always reviewed.");
                }

                if (!step.Optional)
                {
                    issues.Error("PB031", at, "AI steps must be optional; the process has to work without AI.");
                }
            }
            else if (step.MaxConfidence is not null)
            {
                issues.Warn("PB031", at, "maxConfidence only applies to AI steps.");
            }

            if (step.Kind == StepKind.Publish
                && (index != process.Steps.Count - 1 || !process.Steps.Take(index).Any(s => s.Kind == StepKind.Review)))
            {
                issues.Error("PB032", at, "Publish must be the last step and come after a review step.");
            }

            if (step.Uses.Count > 0 && step.Kind != StepKind.Match)
            {
                issues.Error("PB035", at, "Only match steps use domain playbooks.");
            }

            if (step.Kind == StepKind.Review && step.Reviewers.Count == 0)
            {
                issues.Error("PB035", at, "Review steps need at least one reviewer role.");
            }

            foreach (var gate in step.Gates)
            {
                if (string.IsNullOrWhiteSpace(gate.Id) || !ids.Add(gate.Id))
                {
                    issues.Error("PB030", $"{at} gates[{gate.Id}]", "Gate ids must be present and unique.");
                }

                if (gate.Metric == GateMetric.RequiredTargetCoverage && gate.Value is < 0 or > 100)
                {
                    issues.Error("PB034", $"{at} gates[{gate.Id}]", "Coverage is a percentage (0–100).");
                }
                else if (gate.Value < 0)
                {
                    issues.Error("PB034", $"{at} gates[{gate.Id}]", "Counts cannot be negative.");
                }
            }
        }

        foreach (var input in process.Inputs.Where(i => i.Kinds.Count == 0 || i.MinCount < 0))
        {
            issues.Error("PB036", $"inputs[{input.Side}]", "Inputs need at least one kind and a non-negative minCount.");
        }

        foreach (var output in process.Outputs.Where(o => !OutputFormats.Contains(o)))
        {
            issues.Error("PB036", "outputs", $"Unknown output '{output}'; use {string.Join(", ", OutputFormats)}.");
        }
    }

    private static void CheckRegex(string at, string? pattern, Issues issues)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            issues.Error("PB014", at, "A regex pattern is required.");
            return;
        }

        try
        {
            _ = PlaybookMatcher.Pattern(pattern);
        }
        catch (ArgumentException ex)
        {
            issues.Error("PB014", at, $"Invalid regex: {ex.Message}");
        }
    }

    private static void CheckExpression(
        string at,
        string expression,
        IReadOnlyList<NamedInput> inputs,
        bool expectComparison,
        IReadOnlyList<ExpressionExample> examples,
        Issues issues)
    {
        var names = inputs.Select(i => i.Name).ToList();
        if (names.Any(n => !IdentifierName().IsMatch(n) || Expressions.Functions.Contains(n)) || names.Distinct().Count() != names.Count)
        {
            issues.Error("PB015", at, "Input names must be unique identifiers and not function names.");
        }

        Expr parsed;
        try
        {
            parsed = Expressions.Parse(expression);
        }
        catch (ExpressionException ex)
        {
            issues.Error("PB015", at, ex.Message);
            return;
        }

        if (parsed.IsComparison != expectComparison)
        {
            issues.Error("PB015", at, expectComparison ? "Validation expressions must be a comparison, e.g. sum(x) <= 100." : "Derivations must compute a number, not a comparison.");
        }

        var used = parsed.Variables().ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in used.Where(v => !names.Contains(v)))
        {
            issues.Error("PB015", at, $"Expression uses '{unknown}', which is not an input.");
        }

        foreach (var unused in names.Where(n => !used.Contains(n)))
        {
            issues.Warn("PB015", at, $"Input '{unused}' is not used by the expression.");
        }

        foreach (var (example, index) in examples.Select((e, i) => (e, i)))
        {
            var missing = used.Where(v => !example.Inputs.ContainsKey(v)).ToList();
            if (missing.Count > 0)
            {
                issues.Error("PB015", $"{at} examples[{index}]", $"Missing input(s): {string.Join(", ", missing)}.");
            }

            var expectedKind = example.Expected.ValueKind;
            if (expectComparison ? expectedKind is not (JsonValueKind.True or JsonValueKind.False) : expectedKind != JsonValueKind.Number)
            {
                issues.Error("PB015", $"{at} examples[{index}]", expectComparison ? "Expected must be true or false." : "Expected must be a number.");
            }
        }
    }

    private sealed class Issues(string playbook)
    {
        public List<SpecIssue> List { get; } = [];

        public void Error(string code, string location, string message) => List.Add(new(IssueSeverity.Error, code, $"{playbook} {location}", message));

        public void Warn(string code, string location, string message) => List.Add(new(IssueSeverity.Warning, code, $"{playbook} {location}", message));
    }

    [GeneratedRegex(@"^(domain|process)/[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex PascalName();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*\.[A-Z][A-Za-z0-9]*$")]
    private static partial Regex ConceptRef();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierName();
}
