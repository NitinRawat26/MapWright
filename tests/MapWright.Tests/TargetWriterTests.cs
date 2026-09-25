using System.Text.Json.Nodes;
using System.Xml.Linq;
using MapWright.Core.Profile;
using MapWright.Core.Replay;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class TargetWriterTests
{
    private static readonly SystemProfile Sales = SampleProfiles.Load("sales-alpha");
    private static readonly SystemProfile Uw = SampleProfiles.Load("uw-core");
    private static readonly MappingDocument SalesToUw = SampleProfiles.Map(Sales, Uw);
    private static readonly MappingDocument UwToSales = SampleProfiles.Map(Uw, Sales);

    private static TargetValue Value(string path, string value, string type = "string", params int[] indexes) =>
        new("M001", path, indexes, value, type);

    [Fact]
    public void Json_payload_becomes_uw_core_xml()
    {
        var result = TransformEngine.Run(SalesToUw, SamplePayloads.Load("sales-alpha", "llc-two-owners.json"), Uw);
        var xml = XDocument.Parse(TargetWriter.Write(result.Values, Uw));

        var root = xml.Root!;
        Assert.Equal("UnderwritingRequest", root.Name.LocalName);
        Assert.Equal(["Merchant", "Officers", "Processing", "Settlement"], root.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(
            ["LegalName", "DoingBusinessAs", "TaxId", "OwnershipType", "MCC"],
            root.Element("Merchant")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("EIN", (string?)root.Element("Merchant")!.Element("TaxId")!.Attribute("type"));
        Assert.Equal("900123456", root.Element("Merchant")!.Element("TaxId")!.Element("Number")!.Value);

        var officers = root.Element("Officers")!.Elements("Officer").ToList();
        Assert.Equal(2, officers.Count);
        Assert.Equal(["FullName", "Title", "SSN", "OwnershipPct", "DateOfBirth"], officers[0].Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["Maria Lopez", "Sam Okafor"], officers.Select(o => o.Element("FullName")!.Value));
        Assert.Null(officers[1].Element("Title"));
        Assert.Equal("41666.67", root.Element("Processing")!.Element("MonthlyVolume")!.Value);
    }

    [Fact]
    public void Xml_payload_becomes_sales_alpha_json_with_typed_values()
    {
        var result = TransformEngine.Run(UwToSales, SamplePayloads.Load("uw-core", "corp-application.xml"), Sales);
        var json = JsonNode.Parse(TargetWriter.Write(result.Values, Sales))!.AsObject();

        Assert.Equal(["account", "owners", "processing", "bank"], json.Select(p => p.Key));
        Assert.Equal("CORP", (string?)json["account"]!["entityType"]);
        Assert.Equal("5411", (string?)json["account"]!["mcc"]);
        var owners = json["owners"]!.AsArray();
        Assert.Equal(3, owners.Count);
        Assert.Equal(["firstName", "title", "ownershipPercent", "ssn", "dateOfBirth"], owners[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(0.5m, (decimal)owners[0]!["ownershipPercent"]!);
        Assert.Equal(3000000m, (decimal)json["processing"]!["annualCardVolume"]!);
    }

    [Fact]
    public void A_single_source_element_still_becomes_a_json_list()
    {
        var result = TransformEngine.Run(UwToSales, SamplePayloads.Load("uw-core", "sole-prop-application.xml"), Sales);
        var json = JsonNode.Parse(TargetWriter.Write(result.Values, Sales))!;

        var owner = Assert.Single(json["owners"]!.AsArray())!;
        Assert.Equal("Priya", (string?)owner["firstName"]);
        Assert.Equal("900-44-5566", (string?)owner["ssn"]);
    }

    [Fact]
    public void Xml_elements_follow_profile_order_whatever_the_value_order()
    {
        TargetValue[] values =
        [
            Value("/UnderwritingRequest/Settlement/RoutingNumber", "1"),
            Value("/UnderwritingRequest/Officers/Officer/SSN", "2", "string", 1),
            Value("/UnderwritingRequest/Officers/Officer/FullName", "3", "string", 1),
            Value("/UnderwritingRequest/Merchant/LegalName", "4"),
            Value("/UnderwritingRequest/@requestId", "R-1"),
        ];

        var root = XDocument.Parse(TargetWriter.Write(values, Uw)).Root!;

        Assert.Equal(["Merchant", "Officers", "Settlement"], root.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("R-1", (string?)root.Attribute("requestId"));
        var officers = root.Element("Officers")!.Elements("Officer").ToList();
        Assert.Equal(2, officers.Count);
        Assert.Empty(officers[0].Elements());
        Assert.Equal(["FullName", "SSN"], officers[1].Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Xml_namespace_is_applied_when_given()
    {
        var xml = TargetWriter.Write([Value("/UnderwritingRequest/Merchant/LegalName", "Acme")], Uw, new() { XmlNamespace = "urn:uwcore:intake:4.2" });

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("urn:uwcore:intake:4.2", root.Name.NamespaceName);
        Assert.Equal("urn:uwcore:intake:4.2", root.Elements().Single().Name.NamespaceName);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
    }

    [Fact]
    public void Json_paths_support_quoted_names_nested_lists_and_types()
    {
        TargetValue[] values =
        [
            Value("$['merchant id']", "M-1"),
            Value("$.sites[*].tags[*]", "a", "string", 0, 1),
            Value("$.sites[*].open", "true", "boolean", 0),
            Value("$.sites[*].tills", "3", "integer", 1),
            Value("$.fee", "not-a-number", "decimal"),
        ];

        var json = JsonNode.Parse(TargetWriter.Write(values, Sales))!;

        Assert.Equal("M-1", (string?)json["merchant id"]);
        Assert.Equal(2, json["sites"]!.AsArray().Count);
        Assert.Null(json["sites"]![0]!["tags"]![0]);
        Assert.Equal("a", (string?)json["sites"]![0]!["tags"]![1]);
        Assert.True((bool)json["sites"]![0]!["open"]!);
        Assert.Equal(3, (int)json["sites"]![1]!["tills"]!);
        Assert.Equal("not-a-number", (string?)json["fee"]);
    }

    [Fact]
    public void Unsupported_paths_are_rejected()
    {
        Assert.Throws<TransformException>(() => TargetWriter.WriteJson([Value("$.a..b[0]", "x")], Sales));
        Assert.Throws<TransformException>(() => TargetWriter.Write([Value("/A/B", "x"), Value("/C/D", "y")], Uw));
    }
}
