using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;
using MapWright.Output.Readers;

namespace MapWright.Tests;

public sealed class WsdlRpcTests
{
    private static string Rpc(string file) =>
        Path.Combine(AppContext.BaseDirectory, "samples", "systems", "uw-core", "rpc", file);

    private static ProfileField Field(ContractDocument document, string path) =>
        document.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", document.Fields.Select(f => f.Path))}");

    private static string Service(string style, string parts, string use = "literal") => $"""
        <wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                          xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" targetNamespace="urn:t">
          <wsdl:types>
            <xsd:schema targetNamespace="urn:t">
              <xsd:complexType name="Owner"><xsd:sequence><xsd:element name="Name" type="xsd:string"/></xsd:sequence></xsd:complexType>
              <xsd:element name="Channel" type="xsd:string"/>
            </xsd:schema>
          </wsdl:types>
          <wsdl:message name="SubmitIn">{parts}</wsdl:message>
          <wsdl:portType name="P"><wsdl:operation name="Submit"><wsdl:input message="t:SubmitIn"/></wsdl:operation></wsdl:portType>
          <wsdl:binding name="B" type="t:P">
            <soap:binding style="{style}" transport="http://schemas.xmlsoap.org/soap/http"/>
            <wsdl:operation name="Submit"><soap:operation soapAction="Submit"/><wsdl:input><soap:body use="{use}" namespace="urn:t"/></wsdl:input></wsdl:operation>
          </wsdl:binding>
        </wsdl:definitions>
        """;

    [Fact]
    public void Rpc_operation_is_a_wrapper_named_after_it_with_one_child_per_part()
    {
        var document = WsdlReader.Read("svc.wsdl", Service("rpc", """
            <wsdl:part name="amount" type="xsd:decimal"/><wsdl:part name="owner" type="t:Owner"/><wsdl:part name="channel" element="t:Channel"/>
            """));

        Assert.Equal("RPC operation Submit input <Submit>, one child per message part", document.Root);
        Assert.Equal(["/Submit", "/Submit/amount", "/Submit/owner", "/Submit/owner/Name", "/Submit/Channel"], document.Fields.Select(f => f.Path));
        Assert.Equal((FieldDataType.Decimal, Requirement.Required), (Field(document, "/Submit/amount").DataType, Field(document, "/Submit/amount").Required));
        Assert.Equal(FieldNodeKind.Object, Field(document, "/Submit/owner").Kind);
        Assert.Equal(FieldDataType.String, Field(document, "/Submit/Channel").DataType);
        Assert.DoesNotContain(document.Findings, f => f.Kind == ProfileFindingKind.UnresolvedReference);
    }

    [Fact]
    public void Typed_parts_are_read_as_rpc_even_without_an_rpc_binding()
    {
        var document = WsdlReader.Read("svc.wsdl", Service("document", """<wsdl:part name="id" type="xsd:int"/>"""));

        Assert.Equal(FieldDataType.Integer, Field(document, "/Submit/id").DataType);
    }

    [Fact]
    public void Rpc_operation_without_parts_is_an_empty_wrapper()
    {
        var document = WsdlReader.Read("svc.wsdl", Service("rpc", ""));

        Assert.Equal(["/Submit"], document.Fields.Select(f => f.Path));
    }

    [Fact]
    public void Soap_encoding_is_a_finding()
    {
        var document = WsdlReader.Read("svc.wsdl", Service("rpc", """<wsdl:part name="id" type="xsd:int"/>""", "encoded"));

        Assert.Contains(document.Findings, f => f.Kind == ProfileFindingKind.SchemaSimplified && f.Path == "/Submit" && f.Message.Contains("SOAP encoding", StringComparison.Ordinal));
    }

    [Fact]
    public void Document_literal_operation_is_unchanged()
    {
        var document = WsdlReader.Read("svc.wsdl", Service("document", """<wsdl:part name="body" element="t:Channel"/>"""));

        Assert.Equal("operation Submit input <Channel>", document.Root);
        Assert.Equal(["/Channel"], document.Fields.Select(f => f.Path));
    }

    [Fact]
    public void Rpc_sample_service_profiles_with_its_soap_sample()
    {
        var uploads = new[] { "uw-core-intake-rpc.wsdl", "corp-application-rpc.xml" }
            .Select(f => new InputFile(f, File.ReadAllBytes(Rpc(f))));

        var set = ProfileInputs.Read(uploads, null);
        var profile = ProfileBuilder.Build(new() { System = "UW Core RPC", Samples = set.Samples, Contracts = set.Contracts });

        var contract = Assert.Single(set.Contracts);
        Assert.Equal("RPC operation SubmitApplication input <SubmitApplication>, one child per message part", contract.Root);
        Assert.Equal(Requirement.Required, profile.Fields.Single(f => f.Path == "/SubmitApplication/request/@requestId").Required);
        Assert.Equal(100, profile.Fields.Single(f => f.Path == "/SubmitApplication/request/Merchant/LegalName").MaxLength);
        Assert.Equal("SALES-ALPHA", profile.Fields.Single(f => f.Path == "/SubmitApplication/clientId").SampleValue);
        Assert.DoesNotContain(profile.Findings, f => f.Kind is ProfileFindingKind.UndeclaredField or ProfileFindingKind.UnresolvedReference);
    }
}
