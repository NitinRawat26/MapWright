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

    public IReadOnlyList<RecognisedField> Sources => sources;

    public FieldMapping Map(RecognisedField target, string id)
    {
        var draft = ByConcept(target) ?? Unmapped(target);
        return Finish(id, target, draft);
    }

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
            .ThenByDescending(s => NameSimilarity(s.Field.Name, target.Field.Name))
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
        AddDetectionReview(draft, "Source", s);
        AddDetectionReview(draft, "Target", t);
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

        if (ValueMapFor(target.Detection!) is { } map && source.Values.Count > 0
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
            return new() { Type = TransformationType.TypeCast, Rule = reshape };
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
    }

    private void AddDetectionReview(Draft draft, string side, DetectionResult detection)
    {
        if (!detection.RequiresReview)
        {
            return;
        }

        var rules = _playbooks.GetValueOrDefault(detection.Playbook)?.Domain?.Confidence ?? new();
        draft.Review.Add($"{side} match needs review ({string.Join(", ", detection.Triggers.Where(rules.ReviewTriggers.Contains))}).");
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
            draft.Reasons.Add($"Needs review: {string.Join(" ", draft.Review)}");
        }

        var sensitivity = Sensitivity(target, draft.Sources);

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

    /// <summary>Share of name tokens the two names have in common (0–1).</summary>
    internal static double NameSimilarity(string a, string b)
    {
        var left = NameTokens.Split(a).ToHashSet(StringComparer.Ordinal);
        var right = NameTokens.Split(b).ToHashSet(StringComparer.Ordinal);
        return left.Count + right.Count == 0 ? 0 : (double)left.Intersect(right).Count() / left.Union(right).Count();
    }
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

    public void Loss(RiskLevel level, string note)
    {
        DataLoss = level > DataLoss ? level : DataLoss;
        DataLossNotes.Add(note);
    }
}
