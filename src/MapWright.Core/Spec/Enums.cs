namespace MapWright.Core.Spec;

public enum PayloadFormat
{
    Json,
    Xml,
}

public enum SystemSide
{
    Source,
    Target,
}

public enum Cardinality
{
    Single,
    Array,
}

public enum MappingType
{
    OneToOne,
    ManyToOne,
    OneToMany,
    Constant,
    Derived,
    Unmapped,
}

public enum TransformationType
{
    Direct,
    Rename,
    TypeCast,
    UnitConversion,
    PeriodConversion,
    Concat,
    Split,
    Aggregate,
    EnumMap,
    Lookup,
    Conditional,
    Default,
    Derived,
}

public enum ConfidenceBand
{
    High,
    Medium,
    Low,
}

public enum EvidenceKind
{
    Sample,
    Schema,
    FieldSpec,
    Documentation,
    Playbook,
    PriorMapping,
    Reviewer,
    NameSimilarity,
}

public enum RiskLevel
{
    None,
    Low,
    Medium,
    High,
}

public enum Sensitivity
{
    None,
    Pii,
    SensitivePii,
    Financial,
    Pci,
}

public enum ReviewStatus
{
    AutoAccepted,
    NeedsReview,
    Approved,
    Rejected,
    Overridden,
}

public enum InputKind
{
    SamplePayload,
    JsonSchema,
    Xsd,
    OpenApi,
    Wsdl,
    MetadataExport,
    FieldSpec,
    Documentation,
}

public enum FindingKind
{
    Conflict,
    Assumption,
}

public enum ValidationOutcome
{
    Pass,
    Fail,
    Skipped,
}
