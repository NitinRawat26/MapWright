using MapWright.Core.Spec;

namespace MapWright.Core.Profile.Samples;

public enum SampleNodeType
{
    Object,
    Array,
    Value,
}

public enum ScalarKind
{
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>Format-neutral tree of one sample payload. XML repeated siblings are grouped into an Array node.</summary>
public sealed class SampleNode
{
    public required string Name { get; init; }
    public required SampleNodeType Type { get; init; }
    public bool IsAttribute { get; init; }
    public bool IsText { get; init; }
    public List<SampleNode> Children { get; init; } = [];
    public string? Value { get; init; }
    public ScalarKind Scalar { get; init; }
}

public sealed record SampleDocument(string Name, PayloadFormat Format, SampleNode Root, IReadOnlyList<ProfileFinding> Findings);

public static class SampleReader
{
    public static PayloadFormat DetectFormat(string name, string content)
    {
        var first = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').FirstOrDefault();
        return first switch
        {
            '<' => PayloadFormat.Xml,
            '{' or '[' => PayloadFormat.Json,
            _ => throw new ProfileException($"Sample '{name}' is neither JSON nor XML."),
        };
    }

    public static SampleDocument Read(string name, string content, bool unwrapSoapEnvelope = true) =>
        DetectFormat(name, content) switch
        {
            PayloadFormat.Xml => XmlSampleReader.Read(name, content, unwrapSoapEnvelope),
            _ => JsonSampleReader.Read(name, content),
        };
}
