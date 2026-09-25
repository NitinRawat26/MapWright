using ClosedXML.Excel;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Profile.Samples;
using MapWright.Core.Spec;
using MapWright.Output.Readers;

namespace MapWright.Tests;

public sealed class DataDictionaryTests
{
    private static string Gamma(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "samples", "systems", "sales-gamma", .. parts]);

    private static ProfileField Field(ContractDocument document, string path) =>
        document.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", document.Fields.Select(f => f.Path))}");

    private static byte[] Workbook(params (string Name, string[][] Rows)[] sheets)
    {
        using var workbook = new XLWorkbook();
        foreach (var (name, rows) in sheets)
        {
            var sheet = workbook.AddWorksheet(name);
            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    sheet.Cell(r + 1, c + 1).Value = rows[r][c];
                }
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Reads_data_dictionary_column_names_business_names_and_nullable()
    {
        var doc = FieldSpecReader.ReadCsv("dd.csv", """
            Field Name,Business Name,Data Type,Nullable,Domain Values,Field Description,Personal Data
            legalName,Legal Name,VARCHAR(100),N,,,N
            Trading Name,,VARCHAR(100),Y,,Doing-business-as name,N
            TAX ID (EIN),,CHAR(9),NOT NULL,,,Y
            status,,VARCHAR(1),,A = Active; I = Inactive,,
            """);

        Assert.Equal(["$", "$.legalName", "$.tradingName", "$.taxIdEin", "$.status"], doc.Fields.Select(f => f.Path));
        var legal = Field(doc, "$.legalName");
        Assert.Equal((FieldDataType.String, 100, Requirement.Required, "Legal Name"), (legal.DataType, legal.MaxLength, legal.Required, legal.Description));
        Assert.Contains("nullable 'N'", legal.Provenance.Single(p => p.Attribute == ProfileAttribute.Required).Detail);
        Assert.Equal((Requirement.Optional, "Doing-business-as name"), (Field(doc, "$.tradingName").Required, Field(doc, "$.tradingName").Description));
        Assert.Equal((Requirement.Required, true), (Field(doc, "$.taxIdEin").Required, Field(doc, "$.taxIdEin").Sensitive));
        Assert.Equal(Requirement.Unknown, Field(doc, "$.status").Required);
        Assert.Equal(["A", "I"], Field(doc, "$.status").AllowedValues);
        Assert.Contains(doc.Findings, f => f.Path == "$.tradingName" && f.Message.Contains("'Trading Name' is a name, not a path; read as 'tradingName'"));
    }

    [Fact]
    public void A_technical_name_column_wins_over_the_field_name()
    {
        var doc = FieldSpecReader.ReadCsv("dd.csv", "Field Name,API Name,Type\nLegal name,merchant.legalName,string\n");
        Assert.Equal("$.merchant.legalName", Assert.Single(doc.Fields, f => f.Kind == FieldNodeKind.Value).Path);
        Assert.DoesNotContain(doc.Findings, f => f.Message.Contains("is a name"));
    }

    [Fact]
    public void Parent_columns_place_plain_names_under_their_object_or_array()
    {
        var doc = FieldSpecReader.ReadCsv("dd.csv", """
            Entity;Attribute Name;Type;Mandatory
            Merchant Details;legalName;string;Y
            owners[];name;string;Y
            owners[].address;city;string;N
            ;applicationId;string;Y
            merchant;$.merchant.mcc;string;Y
            """);

        Assert.Equal(["$", "$.merchantDetails", "$.merchantDetails.legalName", "$.owners", "$.owners[*].name", "$.owners[*].address", "$.owners[*].address.city", "$.applicationId", "$.merchant", "$.merchant.mcc"],
            doc.Fields.Select(f => f.Path));
        Assert.Equal(Cardinality.Array, Field(doc, "$.owners").Cardinality);

        var xml = FieldSpecReader.ReadCsv("dd.csv", "Parent Path,Element Name,Type\n/Request/Merchant,Name,AN(40)\n/Request/Merchant/,Id,N(9)\n");
        Assert.Equal(["/Request", "/Request/Merchant", "/Request/Merchant/Name", "/Request/Merchant/Id"], xml.Fields.Select(f => f.Path));
        Assert.Equal(40, Field(xml, "/Request/Merchant/Name").MaxLength);
    }

    [Fact]
    public void Existing_field_specs_are_read_as_before()
    {
        Assert.Contains("no path column", Assert.Throws<ProfileException>(() => FieldSpecReader.ReadCsv("x.csv", "name,type\na,string")).Message);
        var doc = FieldSpecReader.ReadCsv("x.csv", "path,type\nmerchant.legalName,string\nowners[].name,string");
        Assert.Equal(["$", "$.merchant", "$.merchant.legalName", "$.owners", "$.owners[*].name"], doc.Fields.Select(f => f.Path));
        Assert.Empty(doc.Findings);
    }

    [Theory]
    [InlineData("Legal Name", "legalName")]
    [InlineData("TAX ID (EIN)", "taxIdEin")]
    [InlineData("date of birth", "dateOfBirth")]
    [InlineData("MCC", "mcc")]
    [InlineData("2nd line", null)]
    [InlineData("--", null)]
    public void Names_become_camel_case_identifiers(string name, string? expected) =>
        Assert.Equal(expected, FieldSpecReader.Identifier(name));

    [Fact]
    public void Every_worksheet_with_a_path_column_is_read_and_named_sheets_are_parents()
    {
        var content = Workbook(
            ("Cover", [["Data dictionary v2"]]),
            ("Merchant", [["Field Name", "Type"], ["legalName", "string"], ["Doing Business As", "string"]]),
            ("Owners", [["Parent", "Field Name", "Type"], ["owners[]", "name", "string"], ["", "ownerCount", "integer"]]),
            ("Codes", [["Code", "Meaning"], ["C", "Corporation"]]));

        var doc = FieldSpecWorkbook.Read("dd.xlsx", content);

        Assert.Equal(InputKind.FieldSpec, doc.Kind);
        Assert.Equal(["$", "$.merchant", "$.merchant.legalName", "$.merchant.doingBusinessAs", "$.owners", "$.owners[*].name", "$.owners.ownerCount"],
            doc.Fields.Select(f => f.Path));
        Assert.Contains("Sheet 'Owners' row 2", Field(doc, "$.owners[*].name").Provenance.Single(p => p.Attribute == ProfileAttribute.DataType).Detail);
        Assert.Contains(doc.Findings, f => f.Message.StartsWith("Sheet 'Merchant' row 3: 'Doing Business As'", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_sheet_names_are_not_parents_and_duplicates_across_sheets_are_errors()
    {
        var plain = FieldSpecWorkbook.Read("dd.xlsx", Workbook(("Sheet1", [["Field", "Type"], ["a", "string"]]), ("Sheet2", [["Field", "Type"], ["b", "string"]])));
        Assert.Equal(["$", "$.a", "$.b"], plain.Fields.Select(f => f.Path));

        var error = Assert.Throws<ProfileException>(() => FieldSpecWorkbook.Read("dd.xlsx", Workbook(("Sheet1", [["Path"], ["a"]]), ("Sheet2", [["Path"], ["a"]]))));
        Assert.Contains("'$.a' more than once (Sheet 'Sheet1' row 2, Sheet 'Sheet2' row 2)", error.Message);

        var single = FieldSpecWorkbook.Read("dd.xlsx", Workbook(("Merchant", [["Field Name"], ["legalName"]])));
        Assert.Equal(["$", "$.legalName"], single.Fields.Select(f => f.Path));
    }

    [Fact]
    public void The_sample_dictionary_gives_the_same_fields_as_excel_and_csv_and_builds_a_profile()
    {
        static string Describe(ContractDocument contract) => string.Join("\n", contract.Fields.Select(f =>
            $"{f.Path} {f.DataType} {f.Required} {f.MaxLength} {f.MaxScale} {f.Cardinality} {f.Sensitive} {string.Join("|", f.AllowedValues)} {f.Description}"));

        var excel = FieldSpecWorkbook.Read("dd.xlsx", File.ReadAllBytes(Gamma("dictionary", "sales-gamma-data-dictionary.xlsx")));
        var csv = FieldSpecReader.ReadCsv("dd.csv", File.ReadAllText(Gamma("dictionary", "sales-gamma-data-dictionary.csv")));
        Assert.Equal(Describe(excel), Describe(csv));
        Assert.Equal(11, excel.Fields.Count(f => f.Kind == FieldNodeKind.Value));
        Assert.Equal(["C", "L", "S"], Field(excel, "$.merchant.entityType").AllowedValues);
        Assert.Equal((FieldDataType.Decimal, 2), (Field(excel, "$.merchant.monthlyCardVolume").DataType, Field(excel, "$.merchant.monthlyCardVolume").MaxScale));
        Assert.Equal(Cardinality.Array, Field(excel, "$.owners").Cardinality);
        Assert.Equal(60, Field(excel, "$.owners[*].homeAddress.line1").MaxLength);

        var set = ProfileInputs.Read(
            [
                new("sales-gamma-data-dictionary.xlsx", File.ReadAllBytes(Gamma("dictionary", "sales-gamma-data-dictionary.xlsx"))),
                new("llc-one-owner.json", File.ReadAllBytes(Gamma("samples", "llc-one-owner.json"))),
            ],
            null);
        var profile = ProfileBuilder.Build(new() { System = "Sales Gamma", Samples = set.Samples, Contracts = set.Contracts });
        Assert.DoesNotContain(profile.Findings, f => f.Kind == ProfileFindingKind.UndeclaredField);
        Assert.Equal(Requirement.Required, profile.Fields.Single(f => f.Path == "$.merchant.legalName").Required);
        Assert.True(profile.Fields.Single(f => f.Path == "$.merchant.taxId").Sensitive);
    }
}
