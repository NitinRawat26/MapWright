using System.Text.Json;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Playbooks;

/// <summary>
/// Versioned, editable knowledge the engine applies. A domain playbook describes how to recognise and
/// transform one business concept; a process playbook describes the steps, gates and reviewers of a workflow.
/// </summary>
public sealed record Playbook
{
    public const string CurrentSpecVersion = "1.0";

    public required string SpecVersion { get; init; }

    /// <summary>"domain/&lt;slug&gt;" or "process/&lt;slug&gt;".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required PlaybookKind Kind { get; init; }

    /// <summary>Semantic version, e.g. "1.2.0". Mappings reference playbooks as "id@version".</summary>
    public required string Version { get; init; }
    public required PlaybookStatus Status { get; init; }
    public string? Owner { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<PlaybookChange> ChangeNotes { get; init; } = [];

    public DomainDefinition? Domain { get; init; }
    public ProcessDefinition? Process { get; init; }

    public string Reference => $"{Id}@{Version}";
}

public sealed record PlaybookChange
{
    public required string Version { get; init; }
    public required DateOnly Date { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
}

public enum PlaybookKind
{
    Domain,
    Process,
}

public enum PlaybookStatus
{
    Draft,
    InReview,
    Published,
    Retired,
    Abandoned,
}

// ---------------------------------------------------------------- domain playbooks

public sealed record DomainDefinition
{
    public required ConceptModel Concept { get; init; }
    public IReadOnlyList<VocabularyTerm> Vocabulary { get; init; } = [];
    public IReadOnlyList<Qualifier> Qualifiers { get; init; } = [];
    public IReadOnlyList<DetectionSignal> Signals { get; init; } = [];
    public IReadOnlyList<DerivationRule> Derivations { get; init; } = [];
    public IReadOnlyList<ConditionalRule> Conditions { get; init; } = [];
    public IReadOnlyList<ValueMapDefinition> ValueMaps { get; init; } = [];
    public IReadOnlyList<ValidationRuleDefinition> Validations { get; init; } = [];
    public ConfidenceRules Confidence { get; init; } = new();
    public IReadOnlyList<RiskWarning> Risks { get; init; } = [];
    public IReadOnlyList<ReviewQuestion> ReviewGuidance { get; init; } = [];

    /// <summary>Free-text guidance handed to the optional AI step for fields the rules do not recognise.</summary>
    public string? AiGuidance { get; init; }
    public IReadOnlyList<DetectionTest> Tests { get; init; } = [];
}

/// <summary>The business concept (e.g. Principal) and its attributes (e.g. Ssn, OwnershipPercent).</summary>
public sealed record ConceptModel
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Array when a system normally sends a list (e.g. several principals).</summary>
    public Cardinality Cardinality { get; init; } = Cardinality.Single;
    public required IReadOnlyList<ConceptAttribute> Attributes { get; init; }
}

public sealed record ConceptAttribute
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public FieldDataType DataType { get; init; } = FieldDataType.String;
    public Sensitivity Sensitivity { get; init; } = Sensitivity.None;

    /// <summary>Generic names (FirstName, Title, Email) only match under an ancestor that names the concept.</summary>
    public bool RequiresContext { get; init; }
}

public enum TermRelation
{
    /// <summary>Same meaning (Owners ≈ Principals).</summary>
    Equivalent,

    /// <summary>A more specific form (EIN is a kind of tax id).</summary>
    Narrower,

    /// <summary>A more general form (Volume for card volume).</summary>
    Broader,

    /// <summary>Overlapping but not the same (Officers are not always owners).</summary>
    Related,
}

public sealed record VocabularyTerm
{
    public required string Term { get; init; }

    /// <summary>Attribute name, or null for the concept itself (e.g. "Owners" names the principal collection).</summary>
    public string? AppliesTo { get; init; }
    public TermRelation Relation { get; init; } = TermRelation.Equivalent;
    public string? Note { get; init; }
}

/// <summary>A dimension that changes a value's meaning, e.g. period (monthly/annual) or unit (percent/fraction).</summary>
public sealed record Qualifier
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Attributes the qualifier applies to; empty means all.</summary>
    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public required IReadOnlyList<QualifierValue> Values { get; init; }

    /// <summary>Assumed when no term is found; the assumption is reported for review.</summary>
    public string? Default { get; init; }
}

public sealed record QualifierValue
{
    public required string Value { get; init; }
    public IReadOnlyList<string> Terms { get; init; } = [];

    /// <summary>Observed numeric values inside this range also indicate the qualifier (e.g. 0–1 means a fraction).</summary>
    public decimal? MinValue { get; init; }
    public decimal? MaxValue { get; init; }
}

public enum SignalKind
{
    /// <summary>Regex over the field name.</summary>
    NamePattern,

    /// <summary>Regex over any ancestor name.</summary>
    AncestorNamePattern,

    /// <summary>Regex over any child name (for object and list fields).</summary>
    ChildNamePattern,

    /// <summary>Regex over the field description.</summary>
    DescriptionPattern,

    /// <summary>Regex every observed value must match.</summary>
    ValuePattern,

    /// <summary>Regex every value shape (9 = digit, A/a = letter) must match; works on masked fields.</summary>
    ValueShape,

    /// <summary>Observed numbers fall within [minValue, maxValue].</summary>
    ValueRange,

    /// <summary>At least half the observed values are codes of the named value map.</summary>
    ValueInMap,

    /// <summary>Data type is one of the '|'-separated types in pattern.</summary>
    DataType,

    /// <summary>Field is a list (pattern "array") or a single value (pattern "single").</summary>
    Cardinality,
}

public sealed record DetectionSignal
{
    public required string Id { get; init; }
    public required SignalKind Kind { get; init; }

    /// <summary>Attribute name, or null for the concept itself.</summary>
    public string? AppliesTo { get; init; }
    public string? Pattern { get; init; }
    public decimal? MinValue { get; init; }
    public decimal? MaxValue { get; init; }

    /// <summary>Points added when the signal matches; negative values penalise.</summary>
    public required int Weight { get; init; }
    public string? Note { get; init; }
}

/// <summary>A reference to a concept attribute with qualifiers, e.g. CardVolume with period=annual.</summary>
public sealed record AttributeRef
{
    public required string Attribute { get; init; }
    public IReadOnlyDictionary<string, string> Qualifiers { get; init; } = new Dictionary<string, string>();
}

public sealed record NamedInput
{
    /// <summary>Variable name used in the expression.</summary>
    public required string Name { get; init; }
    public required string Attribute { get; init; }
    public IReadOnlyDictionary<string, string> Qualifiers { get; init; } = new Dictionary<string, string>();
}

/// <summary>How one variant of a concept is computed from others, e.g. monthly = annual / 12.</summary>
public sealed record DerivationRule
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required AttributeRef Output { get; init; }
    public required IReadOnlyList<NamedInput> Inputs { get; init; }

    /// <summary>
    /// Arithmetic over the input names: + - * / ( ) and sum, count, min, max, abs, round.
    /// Required for numeric transformations; text transformations (Concat, Split, Lookup) describe the rule instead.
    /// </summary>
    public string? Expression { get; init; }
    public required TransformationType Transformation { get; init; }
    public RiskLevel DataLoss { get; init; } = RiskLevel.None;

    /// <summary>The result rests on an assumption (e.g. splitting CNP into MOTO/ECOMM) and must be reviewed.</summary>
    public bool RequiresReview { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<ExpressionExample> Examples { get; init; } = [];
}

public sealed record ExpressionExample
{
    /// <summary>Input name → number or array of numbers.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Inputs { get; init; }

    /// <summary>Expected number (derivations) or true/false (validations).</summary>
    public required JsonElement Expected { get; init; }
}

/// <summary>A value decided by other data, e.g. tax-id type is SSN for sole proprietors and EIN otherwise.</summary>
public sealed record ConditionalRule
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required AttributeRef Output { get; init; }
    public required IReadOnlyList<ConditionCase> Cases { get; init; }
    public string? Otherwise { get; init; }
    public IReadOnlyList<ConditionExample> Examples { get; init; } = [];
}

public sealed record ConditionCase
{
    /// <summary>All clauses must hold.</summary>
    public required IReadOnlyList<ConditionClause> When { get; init; }
    public required string Then { get; init; }
}

public sealed record ConditionClause
{
    /// <summary>"Concept.Attribute", possibly from another playbook (e.g. "LegalEntity.EntityType").</summary>
    public required string Concept { get; init; }
    public required IReadOnlyList<string> In { get; init; }
}

public sealed record ConditionExample
{
    public required IReadOnlyDictionary<string, string> Given { get; init; }
    public string? Expected { get; init; }
}

/// <summary>Canonical codes for an enumerated attribute and the spellings systems use for them.</summary>
public sealed record ValueMapDefinition
{
    public required string Id { get; init; }
    public required string Attribute { get; init; }
    public required IReadOnlyList<CanonicalValue> Values { get; init; }
}

public sealed record CanonicalValue
{
    public required string Code { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record ValidationRuleDefinition
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<NamedInput> Inputs { get; init; }

    /// <summary>A comparison, e.g. "sum(ownership) &lt;= 100" or "abs(cp + moto + ecomm - 100) &lt;= 0.5".</summary>
    public required string Expression { get; init; }
    public IssueSeverity Severity { get; init; } = IssueSeverity.Error;
    public IReadOnlyList<ExpressionExample> Examples { get; init; } = [];
}

/// <summary>
/// Why a match needs a human. Detection raises the term, qualifier and sensitivity triggers;
/// LossyDerivation and ConditionalValue are raised when a mapping uses such a rule.
/// </summary>
public enum ReviewTrigger
{
    /// <summary>The name matched only a related, broader or narrower term.</summary>
    NonEquivalentTerm,

    /// <summary>A qualifier was assumed from its default rather than detected.</summary>
    AssumedQualifier,

    /// <summary>The name and the observed values disagree on a qualifier (e.g. "percent" but values 0–1).</summary>
    QualifierConflict,

    /// <summary>The attribute holds sensitive data.</summary>
    SensitiveAttribute,

    /// <summary>The attribute is produced by a derivation that loses data or rests on an assumption.</summary>
    LossyDerivation,

    /// <summary>The attribute is produced by a conditional rule.</summary>
    ConditionalValue,
}

public sealed record ConfidenceRules
{
    public int EquivalentTermPoints { get; init; } = 60;
    public int NarrowerTermPoints { get; init; } = 45;
    public int BroaderTermPoints { get; init; } = 40;
    public int RelatedTermPoints { get; init; } = 35;

    /// <summary>Added to an attribute match when an ancestor names the concept (ssn under owners).</summary>
    public int ContextPoints { get; init; } = 25;

    /// <summary>Scores below this are not reported as matches.</summary>
    public int MatchThreshold { get; init; } = 50;

    /// <summary>Matches that hit any of these are never auto-accepted.</summary>
    public IReadOnlyList<ReviewTrigger> ReviewTriggers { get; init; } =
        [ReviewTrigger.NonEquivalentTerm, ReviewTrigger.AssumedQualifier, ReviewTrigger.QualifierConflict, ReviewTrigger.LossyDerivation, ReviewTrigger.ConditionalValue];
}

public sealed record RiskWarning
{
    public required string Id { get; init; }
    public string? AppliesTo { get; init; }
    public required RiskLevel Level { get; init; }
    public required string Text { get; init; }
}

public sealed record ReviewQuestion
{
    public required string Id { get; init; }
    public string? AppliesTo { get; init; }

    /// <summary>Only ask when this trigger fired; null means always ask for this attribute.</summary>
    public ReviewTrigger? When { get; init; }
    public required string Question { get; init; }
}

/// <summary>A field the playbook must (or must not) recognise. A playbook is only publishable when its tests pass.</summary>
public sealed record DetectionTest
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public required FieldContext Field { get; init; }

    /// <summary>Expected "Concept" or "Concept.Attribute"; null means the playbook must not match.</summary>
    public string? Expect { get; init; }
    public IReadOnlyDictionary<string, string> ExpectQualifiers { get; init; } = new Dictionary<string, string>();
    public int? MinScore { get; init; }
    public bool? ExpectReview { get; init; }
}

// ---------------------------------------------------------------- process playbooks

public sealed record ProcessDefinition
{
    public IReadOnlyList<ProcessInput> Inputs { get; init; } = [];
    public required IReadOnlyList<ProcessStep> Steps { get; init; }
    public ProcessThresholds Thresholds { get; init; } = new();
    public IReadOnlyList<string> Outputs { get; init; } = [];
}

public sealed record ProcessInput
{
    public required SystemSide Side { get; init; }
    public required IReadOnlyList<InputKind> Kinds { get; init; }
    public int MinCount { get; init; } = 1;
    public string? Note { get; init; }
}

public enum StepKind
{
    Ingest,
    Profile,
    Match,
    AiAssist,
    Validate,
    Review,
    Publish,
}

public sealed record ProcessStep
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required StepKind Kind { get; init; }
    public string? Description { get; init; }

    /// <summary>Domain playbook ids applied by a Match step; empty means all published domain playbooks.</summary>
    public IReadOnlyList<string> Uses { get; init; } = [];
    public IReadOnlyList<ProcessGate> Gates { get; init; } = [];
    public IReadOnlyList<string> Reviewers { get; init; } = [];

    /// <summary>AiAssist steps: the highest confidence an AI-only suggestion may receive.</summary>
    public int? MaxConfidence { get; init; }
    public bool Optional { get; init; }
}

public enum GateMetric
{
    /// <summary>Percent of required target fields that have a mapping.</summary>
    RequiredTargetCoverage,
    LowConfidenceMappings,
    UnresolvedConflicts,
    FailedValidations,
    OpenQuestions,
    UnreviewedSensitiveMappings,
}

public enum GateOperator
{
    Lt,
    Le,
    Eq,
    Ge,
    Gt,
}

public enum GateAction
{
    Stop,
    RequireReview,
    Warn,
}

/// <summary>Fires when "metric operator value" holds, e.g. RequiredTargetCoverage Lt 100 → Stop.</summary>
public sealed record ProcessGate
{
    public required string Id { get; init; }
    public required GateMetric Metric { get; init; }
    public required GateOperator Operator { get; init; }
    public required decimal Value { get; init; }
    public required GateAction Action { get; init; }
    public string? Message { get; init; }
}

public sealed record ProcessThresholds
{
    public int AutoAcceptAt { get; init; } = 90;
    public int ReviewBelow { get; init; } = 90;
    public int RejectBelow { get; init; } = 30;
}
