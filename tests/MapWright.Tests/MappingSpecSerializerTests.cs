using MapWright.Core.Spec;

namespace MapWright.Tests;

public class MappingSpecSerializerTests
{
    [Fact]
    public void Sample_spec_loads()
    {
        var doc = TestSpecs.Sample();

        Assert.Equal("sales-alpha__uw-core", doc.Id);
        Assert.Equal(PayloadFormat.Xml, doc.Target.Format);
        Assert.Equal(18, doc.Mappings.Count);
        Assert.Equal(TransformationType.PeriodConversion, doc.Mappings.Single(m => m.Id == "M009").Transformation.Type);
    }

    [Fact]
    public void Round_trip_is_lossless()
    {
        var json = MappingSpecSerializer.Serialize(TestSpecs.Sample());

        var again = MappingSpecSerializer.Serialize(MappingSpecSerializer.Deserialize(json));

        Assert.Equal(json, again);
    }

    [Fact]
    public void Enums_are_written_as_camel_case_strings()
    {
        var json = MappingSpecSerializer.Serialize(TestSpecs.Minimal());

        Assert.Contains("\"type\": \"oneToOne\"", json);
        Assert.Contains("\"status\": \"needsReview\"", json);
    }

    [Fact]
    public void Unknown_property_is_rejected()
    {
        var json = MappingSpecSerializer.Serialize(TestSpecs.Minimal()).Replace("\"title\":", "\"titel\": \"x\", \"title\":");

        var ex = Assert.Throws<MappingSpecException>(() => MappingSpecSerializer.Deserialize(json));
        Assert.Contains("titel", ex.Message);
    }

    [Fact]
    public void Missing_required_property_is_rejected()
    {
        var json = MappingSpecSerializer.Serialize(TestSpecs.Minimal()).Replace("\"reasoning\": \"test\",", "");

        var ex = Assert.Throws<MappingSpecException>(() => MappingSpecSerializer.Deserialize(json));
        Assert.Contains("reasoning", ex.Message);
    }

    [Fact]
    public void Integer_enum_values_are_rejected()
    {
        var json = MappingSpecSerializer.Serialize(TestSpecs.Minimal()).Replace("\"type\": \"oneToOne\"", "\"type\": 0");

        Assert.Throws<MappingSpecException>(() => MappingSpecSerializer.Deserialize(json));
    }
}
