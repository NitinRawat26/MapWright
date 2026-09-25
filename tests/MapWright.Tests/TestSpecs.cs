using MapWright.Core.Spec;

namespace MapWright.Tests;

internal static class TestSpecs
{
    public static string SamplePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "samples", "mappings", "sales-alpha__uw-core", "mapping.json");

    public static MappingDocument Sample() => MappingSpecSerializer.Load(SamplePath);

    public static FieldDescriptor Field(string name, string path, bool required = false, string? sample = null) =>
        new() { Name = name, Path = path, DataType = "string", Required = required, SampleValue = sample };

    public static FieldMapping OneToOne(string id, string sourcePath, string targetPath, int confidence = 95) => new()
    {
        Id = id,
        Type = MappingType.OneToOne,
        Sources = [Field(sourcePath.Split('.').Last(), sourcePath)],
        Target = Field(targetPath.Split('/').Last(), targetPath, required: true),
        ConfidencePercent = confidence,
        Reasoning = "test",
        Review = new() { Status = ReviewStatus.NeedsReview },
    };

    public static MappingDocument Minimal(params FieldMapping[] mappings) => new()
    {
        SpecVersion = MappingDocument.CurrentSpecVersion,
        Id = "a__b",
        Title = "A → B",
        Version = "0.1.0",
        CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        Source = new() { Name = "A", Version = "1", Format = PayloadFormat.Json },
        Target = new() { Name = "B", Version = "1", Format = PayloadFormat.Xml },
        Mappings = mappings.Length > 0 ? mappings : [OneToOne("M1", "$.a", "/B/A")],
    };
}
