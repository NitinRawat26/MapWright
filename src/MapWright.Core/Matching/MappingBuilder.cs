using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Matching;

/// <summary>Builds one mapping row per target field from the recognised source fields.</summary>
internal sealed class MappingBuilder(IReadOnlyList<RecognisedField> sources, PlaybookLibrary library, ConfidencePolicy policy)
{
    private readonly Dictionary<string, Playbook> _playbooks = library.Active.ToDictionary(p => p.Reference, StringComparer.Ordinal);

    /// <summary>Auto-accept needs both the spec's High band and the process playbook's auto-accept threshold.</summary>
    private readonly int _autoAcceptAt = Math.Max(
        policy.HighThreshold,
        library.Active.Select(p => p.Process).OfType<ProcessDefinition>().Select(p => p.Thresholds.AutoAcceptAt).DefaultIfEmpty(new ProcessThresholds().AutoAcceptAt).Max());

    private readonly List<(FindingKind Kind, string Key, string Description, string MappingId, string? Source)> _findings = [];

    public IReadOnlyList<RecognisedField> Sources => sources;

    public FieldMapping Map(RecognisedField target, string id)
    {
        var draft = ByConcept(target) ?? ByDerivation(target) ?? ByCondition(target) ?? ByName(target) ?? Unmapped(target);
        return Finish(id, target, draft);
    }

    /// <summary>Assumptions and conflicts noted while mapping; findings about the same thing are merged.</summary>
    public IEnumerable<Finding> Findings() => _findings
        .GroupBy(f => (f.Kind, f.Key))
        .Select((g, i) => new Finding
        {
            Id = $"F{i + 1:000}",
            Kind = g.Key.Kind,
            Description = g.First().Description,
            MappingIds = [.. g.Select(f => f.MappingId).Distinct(StringComparer.Ordinal)],
            Sources = [.. g.Select(f => f.Source).OfType<string>().Distinct(StringComparer.Ordinal)],
        });

    // ------------------------------------------------------------ concept pairing

    private Draft? ByConcept(RecognisedField target)
    {
        if (target.Detection is not { } wanted)
        {
            return null;
        }

        var candidates = sources
            .Where(s => s.Detection is { } d && d.BusinessConcept == wanted.BusinessConcept && QualifiersAgree(d, wanted))
            .OrderByDescending(s => s.Detection!.Score)
            .ThenByDescending(s => NameMatcher.Similarity(s.Field.Name, target.Field.Name))
            .ToList();

        return candidates.Count == 0 ? null : Direct(target, candidates[0], candidates.Skip(1));
    }

    private Draft Direct(RecognisedField target, RecognisedField source, IEnumerable<RecognisedField> alternatives)
    {
        var t = target.Detection!;
        var s = source.Detection!;
        var draft = new Draft
        {
            Type = MappingType.OneToOne,
            Sources = [source],
            Confidence = Math.Min(s.Score, t.Score),
            Playbook = t.Playbook,
            Concept = t.BusinessConcept,
        };

        draft.Reasons.Add($"Both fields are recognised as {t.BusinessConcept}{Qualifiers(t)} by {t.Playbook}.");
        foreach (var (key, value, side) in OneSidedQualifiers(s, t))
        {
            draft.Review.Add($"{key}={value} is only known for the {side} field.");
        }

        var others = alternatives.Select(a => a.Path).ToList();
        if (others.Count > 0)
        {
            draft.Reasons.Add($"Other source candidates: {string.Join(", ", others)}.");
        }

        draft.Transformation = Transform(source, target, draft);
        AddDetectionReview(draft, "Source", source);
        AddDetectionReview(draft, "Target", target);
        AddRepeatRisk(draft, source, target);
        draft.Evidence.AddRange(PlaybookEvidence(source, "Source"));
        draft.Evidence.AddRange(PlaybookEvidence(target, "Target"));
        draft.Questions.AddRange(s.Questions);
        draft.Questions.AddRange(t.Questions);
        return draft;
    }

    private Transformation Transform(RecognisedField source, RecognisedField target, Draft draft)
    {
        var from = source.Field;
        var to = target.Field;

        if (target.Detection is { } detection && ValueMapFor(detection) is { } map && source.Values.Count > 0
            && ValueMap(map, source, target, draft) is { Count: > 0 } entries
            && entries.Any(e => e.SourceValue != e.TargetValue))
        {
            return new() { Type = TransformationType.EnumMap, Rule = $"Translate codes through value map {map.Id}", ValueMap = entries };
        }

        if (from.Format is { } sourceFormat && to.Format is { } targetFormat && sourceFormat != targetFormat)
        {
            return new() { Type = TransformationType.TypeCast, Rule = $"Reformat {sourceFormat} → {targetFormat}" };
        }

        if (ShapeChange(source, target) is { } reshape)
        {
            return new()
            {
                Type = TransformationType.TypeCast,
                Rule = reshape,
                Pattern = target.Field.ValueShapes is [var shape] ? shape : null,
            };
        }

        if (from.DataType != to.DataType && from.DataType != FieldDataType.Unknown && to.DataType != FieldDataType.Unknown)
        {
            TypeRisk(draft, source, to.DataType);
            return new() { Type = TransformationType.TypeCast, Rule = $"Convert {MappingGenerator.JsonName(from.DataType)} → {MappingGenerator.JsonName(to.DataType)}" };
        }

        return PlaybookMatcher.Compact(from.Name) == PlaybookMatcher.Compact(to.Name)
            ? new() { Type = TransformationType.Direct, Rule = "Copy" }
            : new() { Type = TransformationType.Rename, Rule = $"Copy {from.Name} to {to.Name}" };
    }

    /// <summary>Digit codes written differently on each side, e.g. SSN 999-99-9999 → 999999999.</summary>
    private static string? ShapeChange(RecognisedField source, RecognisedField target)
    {
        static bool DigitCode(string shape) => shape.Length > 0 && shape.All(c => c == '9' || !char.IsLetterOrDigit(c));

        var from = source.Field.ValueShapes;
        var to = target.Field.ValueShapes;
        if (source.Field.DataType != FieldDataType.String || to.Count == 0 || from.Count == 0 || !from.Concat(to).All(DigitCode))
        {
            return null;
        }

        var changed = from.Where(s => !to.Contains(s, StringComparer.Ordinal)).ToList();
        return changed.Count == 0 ? null : $"Reformat {string.Join(", ", changed)} → {string.Join(" or ", to)}";
    }

    private ValueMapDefinition? ValueMapFor(DetectionResult detection) =>
        detection.Attribute is { } attribute && _playbooks.GetValueOrDefault(detection.Playbook)?.Domain is { } domain
            ? domain.ValueMaps.FirstOrDefault(m => string.Equals(m.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>Source value → playbook code → the target's spelling of that code.</summary>
    private static List<ValueMapEntry> ValueMap(ValueMapDefinition map, RecognisedField source, RecognisedField target, Draft draft)
    {
        var targetSpelling = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in target.Values)
        {
            if (PlaybookMatcher.ResolveCode(map, value) is { } code)
            {
                targetSpelling.TryAdd(code, value);
            }
        }

        var entries = new List<ValueMapEntry>();
        var unknown = new List<string>();
        var unseen = new List<string>();
        foreach (var value in source.Values)
        {
            if (PlaybookMatcher.ResolveCode(map, value) is not { } code)
            {
                unknown.Add(value);
                continue;
            }

            if (targetSpelling.TryGetValue(code, out var spelling))
            {
                entries.Add(new() { SourceValue = value, TargetValue = spelling });
            }
            else
            {
                unseen.Add(value);
                entries.Add(new() { SourceValue = value, TargetValue = code, Notes = $"Playbook code {code}; not seen in target samples, confirm the target spelling." });
            }
        }

        if (unknown.Count > 0)
        {
            draft.Confidence -= 15;
            draft.Review.Add($"Source value(s) {string.Join(", ", unknown)} have no code in value map {map.Id}.");
        }

        if (unseen.Count > 0)
        {
            draft.Review.Add($"Target spelling unknown for source value(s) {string.Join(", ", unseen)}.");
        }

        return entries;
    }

    private static void TypeRisk(Draft draft, RecognisedField source, FieldDataType to)
    {
        var from = source.Field.DataType;
        if (from == FieldDataType.Decimal && to == FieldDataType.Integer)
        {
            draft.Confidence -= 5;
            draft.Loss(RiskLevel.Medium, "Target is an integer; fractional parts are lost.");
        }
        else if (from == FieldDataType.DateTime && to == FieldDataType.Date)
        {
            draft.Loss(RiskLevel.Low, "Target is a date; the time of day is dropped.");
        }
        else if (from == FieldDataType.String && to is FieldDataType.Integer or FieldDataType.Decimal)
        {
            var numeric = source.Field.ObservedValues.Count > 0 && source.Field.ObservedValues.All(v => decimal.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out _));
            draft.Loss(numeric ? RiskLevel.Low : RiskLevel.Medium, numeric ? "Source text holds numbers in every sample." : "Source text may not be numeric.");
        }
    }

    private static void AddRepeatRisk(Draft draft, RecognisedField source, RecognisedField target)
    {
        if (source.Repeats && !target.Repeats)
        {
            draft.Loss(RiskLevel.High, "Source repeats (one value per list item) but the target holds a single value; decide which item to send.");
        }

        if (source.Repeats && target.Repeats)
        {
            AddListPairing(draft, source, target);
        }
    }

    /// <summary>Items of the source list become items of the target list; a related-term list (Officers vs Owners) needs review.</summary>
    private static void AddListPairing(Draft draft, RecognisedField source, RecognisedField target)
    {
        var lists = new[] { source.ListDetection, target.ListDetection }.OfType<DetectionResult>().ToList();
        var concept = lists.Select(l => l.BusinessConcept).FirstOrDefault();
        var description = $"Each item of source list {source.ListPath} becomes an item of target list {target.ListPath}"
            + (concept is null ? "." : $" (both {concept}).");
        var questions = lists.Where(l => l.RequiresReview).SelectMany(l => l.Questions).Distinct(StringComparer.Ordinal).ToList();
        if (lists.Any(l => l.RequiresReview))
        {
            draft.Review.Add($"List pairing {source.ListPath} → {target.ListPath} needs review.");
            draft.Questions.AddRange(questions);
            description += questions.Count == 0 ? "" : $" {string.Join(" ", questions)}";
        }

        draft.Findings.Add((FindingKind.Assumption, $"{source.ListPath}|{target.ListPath}", description, lists.Select(l => l.Playbook).FirstOrDefault()));
    }

    private void AddDetectionReview(Draft draft, string side, RecognisedField field)
    {
        var detection = field.Detection!;
        if (!detection.RequiresReview)
        {
            return;
        }

        var rules = _playbooks.GetValueOrDefault(detection.Playbook)?.Domain?.Confidence ?? new();
        draft.Review.Add($"{side} match needs review ({string.Join(", ", detection.Triggers.Where(rules.ReviewTriggers.Contains))}).");
        if (detection.Triggers.Contains(ReviewTrigger.QualifierConflict))
        {
            draft.Findings.Add((FindingKind.Conflict, $"{side}|{field.Path}",
                $"{side} field {field.Path}: its name and its sample values disagree on a qualifier; read as {detection.BusinessConcept}{Qualifiers(detection)}.",
                detection.Playbook));
        }
    }

    private static IEnumerable<Evidence> PlaybookEvidence(RecognisedField field, string side)
    {
        if (field.Detection is { } detection)
        {
            yield return new() { Kind = EvidenceKind.Playbook, Reference = detection.Playbook, Detail = $"{side} {field.Path}: {string.Join(" ", detection.Evidence)}" };
        }

        if (field.Field.SeenIn.Count > 0)
        {
            yield return new() { Kind = EvidenceKind.Sample, Reference = string.Join(", ", field.Field.SeenIn), Detail = $"{side} {field.Path} observed" };
        }
    }

    // ------------------------------------------------------------ derivation rules

    /// <summary>Applies the first derivation rule whose output is the target and whose inputs the source provides.</summary>
    private Draft? ByDerivation(RecognisedField target)
    {
        if (target.Detection is not { Attribute: not null } wanted || DomainOf(wanted) is not { } domain)
        {
            return null;
        }

        var applicable = domain.Derivations
            .Where(r => Same(r.Output.Attribute, wanted.Attribute)
                && r.Output.Qualifiers.All(q => !wanted.Qualifiers.TryGetValue(q.Key, out var v) || v == q.Value))
            .Select(r => (Rule: r, Inputs: r.Inputs.Select(i => BestSource(domain.Concept.Name, i.Attribute, i.Qualifiers)).ToList()))
            .Where(c => c.Inputs.All(i => i is not null))
            .ToList();
        if (applicable.Count == 0)
        {
            return null;
        }

        var (rule, found) = applicable
            .OrderBy(c => c.Rule.RequiresReview)
            .ThenBy(c => c.Rule.DataLoss)
            .First();
        var inputs = found.Select(i => i!).ToList();

        var draft = new Draft
        {
            Type = inputs.Count > 1 ? MappingType.ManyToOne : rule.Transformation == TransformationType.Derived ? MappingType.Derived : MappingType.OneToOne,
            Sources = inputs,
            Confidence = inputs.Select(i => i.Detection!.Score).Append(wanted.Score).Min(),
            Playbook = wanted.Playbook,
            Concept = wanted.BusinessConcept,
            Transformation = new()
            {
                Type = rule.Transformation,
                Rule = $"{rule.Description} ({rule.Id}: {string.Join(", ", rule.Inputs.Zip(inputs).Select(p => $"{p.First.Name} = {p.Second.Path}"))})",
                Expression = rule.Expression,
                Inputs = rule.Inputs.Zip(inputs).ToDictionary(p => p.First.Name, p => p.Second.Path, StringComparer.Ordinal),
            },
        };

        draft.Reasons.Add($"Target is {wanted.BusinessConcept}{Qualifiers(wanted)}; {wanted.Playbook} derives it with rule {rule.Id} from "
            + $"{string.Join(" and ", inputs.Select(i => $"{i.Detection!.BusinessConcept}{Qualifiers(i.Detection)}"))}.");
        if (rule.Note is { } note)
        {
            draft.Reasons.Add($"Assumption: {note}");
            draft.Findings.Add((FindingKind.Assumption, $"{target.Path}|{rule.Id}", $"{target.Path} uses rule {rule.Id}: {note}", $"{wanted.Playbook} {rule.Id}"));
        }

        var others = applicable.Where(c => c.Rule != rule).Select(c => $"{c.Rule.Id} ({c.Rule.Expression ?? c.Rule.Description})").ToList();
        if (others.Count > 0)
        {
            draft.Reasons.Add($"Rule(s) {string.Join(", ", others)} also apply and can cross-check the result.");
        }

        if (rule.RequiresReview)
        {
            draft.Confidence -= 10;
            draft.Review.Add($"Rule {rule.Id} rests on an assumption.");
        }

        if (rule.DataLoss != RiskLevel.None)
        {
            draft.Confidence -= rule.DataLoss >= RiskLevel.Medium ? 10 : 0;
            draft.Loss(rule.DataLoss, rule.Note ?? $"Rule {rule.Id} loses information.");
        }

        draft.Evidence.Add(new() { Kind = EvidenceKind.Playbook, Reference = wanted.Playbook, Detail = $"Derivation {rule.Id}: {rule.Expression ?? rule.Description}" });
        AddInputs(draft, inputs, target);
        return draft;
    }

    /// <summary>The highest-scoring source recognised as Concept.Attribute with exactly these qualifiers.</summary>
    private RecognisedField? BestSource(string concept, string attribute, IReadOnlyDictionary<string, string> qualifiers) => sources
        .Where(s => s.Detection is { } d && Same(d.Concept, concept) && Same(d.Attribute, attribute)
            && qualifiers.All(q => d.Qualifiers.TryGetValue(q.Key, out var v) && v == q.Value))
        .OrderByDescending(s => s.Detection!.Score)
        .FirstOrDefault();

    // ------------------------------------------------------------ conditional rules

    /// <summary>Applies a conditional rule (e.g. SSN for sole proprietors, otherwise EIN) using the source fields its clauses name.</summary>
    private Draft? ByCondition(RecognisedField target)
    {
        if (target.Detection is not { Attribute: not null } wanted || DomainOf(wanted) is not { } domain)
        {
            return null;
        }

        foreach (var rule in domain.Conditions.Where(r => Same(r.Output.Attribute, wanted.Attribute)))
        {
            var concepts = rule.Cases.SelectMany(c => c.When).Select(w => w.Concept).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var inputs = concepts.Select(c => sources
                    .Where(s => s.Detection is { } d && Same(d.BusinessConcept, c))
                    .OrderByDescending(s => s.Detection!.Score)
                    .FirstOrDefault())
                .ToList();
            if (inputs.Any(i => i is null))
            {
                continue;
            }

            return Conditional(target, rule, [.. inputs.Select(i => i!)]);
        }

        return null;
    }

    private Draft Conditional(RecognisedField target, ConditionalRule rule, IReadOnlyList<RecognisedField> inputs)
    {
        var wanted = target.Detection!;
        var targetMap = ValueMapFor(wanted);
        string Spell(string code) => targetMap is null ? code : target.Values.FirstOrDefault(v => PlaybookMatcher.ResolveCode(targetMap, v) == code) ?? code;

        var cases = rule.Cases.Select(c => $"{string.Join(" and ", c.When.Select(w => Clause(w, inputs)))} → {Spell(c.Then)}").ToList();
        var otherwise = rule.Otherwise is null ? null : Spell(rule.Otherwise);

        var draft = new Draft
        {
            Type = inputs.Count > 1 ? MappingType.ManyToOne : MappingType.Derived,
            Sources = inputs,
            Confidence = inputs.Select(i => i.Detection!.Score).Append(wanted.Score).Min() - 10,
            Playbook = wanted.Playbook,
            Concept = wanted.BusinessConcept,
            Transformation = new()
            {
                Type = TransformationType.Conditional,
                Rule = $"{rule.Description} ({rule.Id})",
                Condition = string.Join("; ", otherwise is null ? cases : [.. cases, $"otherwise → {otherwise}"]),
                DefaultValue = otherwise,
                ValueMap = inputs.Count == 1 ? ConditionTable(rule, inputs[0], Spell) : [],
                Cases = inputs.Count == 1 ? null : [.. rule.Cases.Select(c => new ConditionalCase
                {
                    When = [.. c.When.Select(w => new ConditionalClause { Source = Input(w, inputs).Field.Path, In = [.. Spellings(w, inputs)] })],
                    Then = Spell(c.Then),
                })],
            },
        };

        draft.Reasons.Add($"No source field is {wanted.BusinessConcept}; {wanted.Playbook} infers it with conditional rule {rule.Id} from "
            + $"{string.Join(" and ", inputs.Select(i => i.Detection!.BusinessConcept))}.");
        draft.Review.Add($"Conditional rule {rule.Id} infers the value; confirm the exceptions with the business.");
        draft.Findings.Add((FindingKind.Assumption, $"{target.Path}|{rule.Id}", $"{target.Path} is inferred, not sent: {rule.Description}", $"{wanted.Playbook} {rule.Id}"));
        draft.Evidence.Add(new() { Kind = EvidenceKind.Playbook, Reference = wanted.Playbook, Detail = $"Condition {rule.Id}: {draft.Transformation.Condition}" });
        AddInputs(draft, inputs, target);
        return draft;
    }

    /// <summary>"entityType in (SOLE_PROP)", using the source's own spellings of the clause's codes.</summary>
    private string Clause(ConditionClause clause, IReadOnlyList<RecognisedField> inputs) =>
        $"{Input(clause, inputs).Field.Name} in ({string.Join(", ", Spellings(clause, inputs))})";

    private static RecognisedField Input(ConditionClause clause, IReadOnlyList<RecognisedField> inputs) =>
        inputs.First(i => Same(i.Detection!.BusinessConcept, clause.Concept));

    /// <summary>The source's own spellings of the clause's codes; a code no source value resolves to is kept as it is.</summary>
    private IEnumerable<string> Spellings(ConditionClause clause, IReadOnlyList<RecognisedField> inputs)
    {
        var input = Input(clause, inputs);
        var map = ValueMapFor(input.Detection!);
        return clause.In
            .SelectMany(code => input.Values.Where(v => map is not null && PlaybookMatcher.ResolveCode(map, v) == code).DefaultIfEmpty(code))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>Each observed source value and the target value the rule gives it.</summary>
    private List<ValueMapEntry> ConditionTable(ConditionalRule rule, RecognisedField input, Func<string, string> spell)
    {
        var map = ValueMapFor(input.Detection!);
        var entries = new List<ValueMapEntry>();
        foreach (var value in input.Values)
        {
            var code = map is null ? value : PlaybookMatcher.ResolveCode(map, value) ?? value;
            var match = rule.Cases.FirstOrDefault(c => c.When.All(w => w.In.Contains(code, StringComparer.OrdinalIgnoreCase)));
            if ((match?.Then ?? rule.Otherwise) is { } then)
            {
                entries.Add(new() { SourceValue = value, TargetValue = spell(then), Notes = match is null ? "otherwise" : null });
            }
        }

        return entries;
    }

    private void AddInputs(Draft draft, IReadOnlyList<RecognisedField> inputs, RecognisedField target)
    {
        foreach (var input in inputs)
        {
            AddDetectionReview(draft, "Source", input);
            AddRepeatRisk(draft, input, target);
            draft.Evidence.AddRange(PlaybookEvidence(input, "Source"));
            draft.Questions.AddRange(input.Detection!.Questions);
        }

        AddDetectionReview(draft, "Target", target);
        draft.Evidence.AddRange(PlaybookEvidence(target, "Target"));
        draft.Questions.AddRange(target.Detection!.Questions);
    }

    private DomainDefinition? DomainOf(DetectionResult detection) => _playbooks.GetValueOrDefault(detection.Playbook)?.Domain;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ name-only fallback

    /// <summary>Best field-name match for targets no playbook rule covers; never auto-accepted.</summary>
    private Draft? ByName(RecognisedField target)
    {
        var best = sources
            .Where(s => s.Detection is null || target.Detection is null)
            .Select(s => (Source: s, Score: NameMatcher.Score(s.Field, target.Field)))
            .Where(c => c.Score >= NameMatcher.MinimumScore)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => NameMatcher.Similarity(ParentName(c.Source), ParentName(target)))
            .FirstOrDefault();
        if (best.Source is not { } source)
        {
            return null;
        }

        var draft = new Draft
        {
            Type = MappingType.OneToOne,
            Sources = [source],
            Confidence = Math.Min(best.Score, NameMatcher.MaximumConfidence),
            Playbook = source.Detection?.Playbook ?? target.Detection?.Playbook,
            Concept = source.Detection?.BusinessConcept ?? target.Detection?.BusinessConcept,
        };

        draft.Reasons.Add($"Matched by field name only ({source.Field.Name} ~ {target.Field.Name}); no playbook rule links the two fields.");
        draft.Review.Add("Name-only match; confirm the meaning, then add the terms to a playbook.");
        draft.Transformation = Transform(source, target, draft);
        AddRepeatRisk(draft, source, target);
        draft.Evidence.Add(new() { Kind = EvidenceKind.NameSimilarity, Reference = $"{source.Field.Name} ~ {target.Field.Name}", Detail = $"Name score {best.Score}" });
        draft.Evidence.AddRange(PlaybookEvidence(source, "Source"));
        draft.Evidence.AddRange(PlaybookEvidence(target, "Target"));
        return draft;
    }

    private static string ParentName(RecognisedField field) => field.Context.Ancestors.FirstOrDefault() ?? "";

    // ------------------------------------------------------------ unmapped

    private static Draft Unmapped(RecognisedField target)
    {
        var draft = new Draft { Type = MappingType.Unmapped, Confidence = 0 };
        if (target.Detection is { } t)
        {
            draft.Playbook = t.Playbook;
            draft.Concept = t.BusinessConcept;
            draft.Reasons.Add($"Recognised as {t.BusinessConcept}{Qualifiers(t)} by {t.Playbook}, but no source field provides it.");
            draft.Resolution = $"Ask the source team for {t.BusinessConcept}, or agree a default.";
            draft.Evidence.AddRange(PlaybookEvidence(target, "Target"));
        }
        else
        {
            draft.Reasons.Add("No playbook recognises this field and no source field matches it.");
            draft.Resolution = "Confirm the field's meaning, then add a playbook term, agree a default or ask the source team.";
        }

        if (target.Field.ObservedValues is [var only] && target.Field.SeenIn.Count > 1 && !target.Field.Sensitive)
        {
            draft.Resolution += $" Every target sample holds '{only}'; if it is fixed for this source system, map it as a constant.";
        }

        return draft;
    }

    // ------------------------------------------------------------ finishing

    private FieldMapping Finish(string id, RecognisedField target, Draft draft)
    {
        var confidence = Math.Clamp(draft.Confidence, 0, 100);
        if (draft.DataLoss >= RiskLevel.Medium)
        {
            draft.Review.Add($"Data loss risk is {MappingGenerator.JsonName(draft.DataLoss)}.");
        }

        if (draft.Type != MappingType.Unmapped && draft.Review.Count == 0 && confidence < _autoAcceptAt)
        {
            draft.Review.Add($"Confidence {confidence}% is below auto-accept ({_autoAcceptAt}%).");
        }

        var needsReview = draft.Type == MappingType.Unmapped || draft.Review.Count > 0;
        if (draft.Type != MappingType.Unmapped && draft.Review.Count > 0)
        {
            draft.Reasons.Add($"Needs review: {string.Join(" ", draft.Review.Distinct(StringComparer.Ordinal))}");
        }

        var sensitivity = Sensitivity(target, draft.Sources);

        _findings.AddRange(draft.Findings.Select(f => (f.Kind, f.Key, f.Description, id, f.Source)));
        var questions = draft.Questions.Distinct(StringComparer.Ordinal).ToList();
        return new()
        {
            Id = id,
            Type = draft.Type,
            Sources = [.. draft.Sources.Select(s => Masked(MappingGenerator.Describe(s.Field, s.Repeats), sensitivity))],
            Target = Masked(MappingGenerator.Describe(target.Field, target.Repeats), sensitivity),
            BusinessConcept = draft.Concept,
            DomainPlaybook = draft.Playbook,
            Transformation = draft.Transformation,
            ConfidencePercent = confidence,
            Reasoning = string.Join(" ", draft.Reasons),
            Evidence = draft.Evidence,
            Risk = new()
            {
                DataLoss = draft.DataLoss,
                DataLossNote = draft.DataLossNotes.Count == 0 ? null : string.Join(" ", draft.DataLossNotes),
                Sensitivity = sensitivity,
                TargetValidationRules = TargetRules(target),
            },
            Review = new()
            {
                Status = needsReview ? ReviewStatus.NeedsReview : ReviewStatus.AutoAccepted,
                OpenQuestion = questions.Count == 0 ? null : string.Join(" ", questions),
            },
            SuggestedResolution = draft.Resolution,
        };
    }

    /// <summary>Playbook-sensitive attributes (e.g. financial volumes) may be unmasked in the profile; the spec shows at most the last 4 characters.</summary>
    private static FieldDescriptor Masked(FieldDescriptor field, Sensitivity sensitivity) =>
        sensitivity != Spec.Sensitivity.None && field.SampleValue is { } sample
            && sample.Count(char.IsAsciiDigit) > MappingSpecValidator.MaxVisibleDigitsInSensitiveSample
            ? field with { SampleValue = SensitiveDataPolicy.Mask(sample) }
            : field;

    private Sensitivity Sensitivity(RecognisedField target, IEnumerable<RecognisedField> sources)
    {
        var levels = sources.Append(target)
            .Select(f => AttributeFor(f.Detection)?.Sensitivity is { } s and not Spec.Sensitivity.None
                ? s
                : f.Field.Sensitive ? Spec.Sensitivity.SensitivePii : Spec.Sensitivity.None)
            .ToList();
        return levels.FirstOrDefault(l => l != Spec.Sensitivity.None);
    }

    private IReadOnlyList<string> TargetRules(RecognisedField target)
    {
        var rules = new List<string>();
        if (target.Field.Required is Requirement.Required)
        {
            rules.Add("Required");
        }
        else if (target.Field.Required is Requirement.LikelyRequired)
        {
            rules.Add("Likely required (present in every sample)");
        }

        if (target.Field.AllowedValues.Count > 0)
        {
            rules.Add($"One of: {string.Join(", ", target.Field.AllowedValues)}");
        }

        if (target.Detection is { Attribute: { } attribute } detection && _playbooks.GetValueOrDefault(detection.Playbook)?.Domain is { } domain)
        {
            rules.AddRange(domain.Validations
                .Where(v => v.Inputs.Any(i => string.Equals(i.Attribute, attribute, StringComparison.OrdinalIgnoreCase)))
                .Select(v => $"{v.Id}: {v.Description}"));
        }

        return rules;
    }

    private ConceptAttribute? AttributeFor(DetectionResult? detection) =>
        detection is { Attribute: { } attribute } && _playbooks.GetValueOrDefault(detection.Playbook)?.Domain is { } domain
            ? domain.Concept.Attributes.FirstOrDefault(a => string.Equals(a.Name, attribute, StringComparison.OrdinalIgnoreCase))
            : null;

    // ------------------------------------------------------------ helpers

    /// <summary>Qualifiers known on both sides must agree (period=annual never pairs with period=monthly).</summary>
    internal static bool QualifiersAgree(DetectionResult a, DetectionResult b) =>
        a.Qualifiers.All(q => !b.Qualifiers.TryGetValue(q.Key, out var other) || other == q.Value);

    private static IEnumerable<(string Key, string Value, string Side)> OneSidedQualifiers(DetectionResult source, DetectionResult target) =>
        source.Qualifiers.Where(q => !target.Qualifiers.ContainsKey(q.Key)).Select(q => (q.Key, q.Value, "source"))
            .Concat(target.Qualifiers.Where(q => !source.Qualifiers.ContainsKey(q.Key)).Select(q => (q.Key, q.Value, "target")));

    internal static string Qualifiers(DetectionResult detection) =>
        detection.Qualifiers.Count == 0 ? "" : $" [{string.Join(", ", detection.Qualifiers.Select(q => $"{q.Key}={q.Value}"))}]";

}

/// <summary>A mapping row under construction.</summary>
internal sealed class Draft
{
    public required MappingType Type { get; set; }
    public IReadOnlyList<RecognisedField> Sources { get; set; } = [];
    public Transformation Transformation { get; set; } = new() { Type = TransformationType.Direct };
    public int Confidence { get; set; }
    public string? Playbook { get; set; }
    public string? Concept { get; set; }
    public string? Resolution { get; set; }
    public RiskLevel DataLoss { get; private set; } = RiskLevel.None;
    public List<string> DataLossNotes { get; } = [];
    public List<string> Reasons { get; } = [];
    public List<string> Review { get; } = [];
    public List<string> Questions { get; } = [];
    public List<Evidence> Evidence { get; } = [];
    public List<(FindingKind Kind, string Key, string Description, string? Source)> Findings { get; } = [];

    public void Loss(RiskLevel level, string note)
    {
        DataLoss = level > DataLoss ? level : DataLoss;
        DataLossNotes.Add(note);
    }
}
