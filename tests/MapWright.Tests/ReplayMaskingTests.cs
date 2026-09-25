using System.Text.Json.Nodes;
using System.Xml.Linq;
using MapWright.Core.Profile;
using MapWright.Core.Replay;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class ReplayMaskingTests
{
    private static readonly SystemProfile Sales = SampleProfiles.Load("sales-alpha");
    private static readonly SystemProfile Uw = SampleProfiles.Load("uw-core");
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(Sales, Uw);
    private static readonly MappingDocument UwToSales = SampleProfiles.Map(Uw, Sales);

    [Fact]
    public void Sensitive_values_keep_their_last_four_characters_and_the_rest_is_unchanged()
    {
        var result = TransformEngine.Run(SalesToUw, SamplePayloads.Load("sales-alpha", "llc-two-owners.json"), Uw);
        var masked = ReplayMasking.Mask(result.Values, SalesToUw, Uw);
        var root = XDocument.Parse(TargetWriter.Write(masked, Uw)).Root!;

        Assert.Equal("*****3456", root.Element("Merchant")!.Element("TaxId")!.Element("Number")!.Value);
        Assert.Equal("EIN", (string?)root.Element("Merchant")!.Element("TaxId")!.Attribute("type"));
        var officers = root.Element("Officers")!.Elements("Officer").ToList();
        Assert.All(officers, o => Assert.StartsWith("*****", o.Element("SSN")!.Value));
        Assert.All(officers, o => Assert.Contains('*', o.Element("DateOfBirth")!.Value));
        Assert.Equal(["***** *opez", "*** **afor"], officers.Select(o => o.Element("FullName")!.Value));
        Assert.Equal("Blue Harbor Coffee LLC", root.Element("Merchant")!.Element("LegalName")!.Value);
        Assert.Equal("41666.67", root.Element("Processing")!.Element("MonthlyVolume")!.Value);
        Assert.Contains('*', root.Element("Settlement")!.Element("AccountNumber")!.Value);

        Assert.Equal(result.Values.Select(v => (v.MappingId, v.Path)), masked.Select(v => (v.MappingId, v.Path)));
        var sensitive = Uw.Fields.Where(f => f.Sensitive).Select(f => f.Path).Append("/UnderwritingRequest/Officers/Officer/FullName").ToHashSet();
        Assert.All(result.Values.Zip(masked).Where(p => p.First.Value != p.Second.Value), p => Assert.Contains(p.First.Path, sensitive));
    }

    [Fact]
    public void Masked_json_values_are_written_as_text_without_the_real_values()
    {
        var result = TransformEngine.Run(UwToSales, SamplePayloads.Load("uw-core", "corp-application.xml"), Sales);
        var masked = ReplayMasking.Mask(result.Values, UwToSales, Sales);
        var changed = result.Values.Zip(masked).Where(p => p.First.Value != p.Second.Value).ToList();
        Assert.NotEmpty(changed);
        Assert.All(changed, p => Assert.Equal("string", p.Second.DataType));

        var json = TargetWriter.Write(masked, Sales);
        Assert.NotNull(JsonNode.Parse(json));
        Assert.All(changed, p => Assert.DoesNotContain(p.First.Value, json));
    }
}
