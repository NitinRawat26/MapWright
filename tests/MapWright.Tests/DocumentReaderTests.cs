using System.Net;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MapWright.Ai;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;
using MapWright.Output.Readers;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MapWright.Tests;

public sealed class DocumentReaderTests
{
    private static string Docs(string file) => Path.Combine(AppContext.BaseDirectory, "samples", "systems", "sales-beta", "docs", file);

    private static DocumentContent Sample(string extension)
    {
        var path = Docs($"sales-beta-interface-spec.{extension}");
        return DocumentReader.Read(Path.GetFileName(path), File.ReadAllBytes(path));
    }

    private static ProfileField Field(ContractDocument document, string path) =>
        document.Fields.SingleOrDefault(f => f.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"No field '{path}'. Fields: {string.Join(", ", document.Fields.Select(f => f.Path))}");

    private static byte[] Word(params string[][]? table)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var body = new W.Body();
            document.AddMainDocumentPart().Document = new W.Document(body);
            W.Paragraph P(string text) => new(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            body.Append(P("Merchant fields"), P("The merchant.legalName is the registered name; call 555 010 4477 or mail ops@example.com."));
            if (table is not null)
            {
                body.Append(new W.Table(table.Select(row => new W.TableRow(row.Select(cell => new W.TableCell(P(cell)))))));
            }
        }

        return stream.ToArray();
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    public void Reads_the_field_tables_of_a_specification_by_rules(string extension)
    {
        var document = Sample(extension);
        var contract = document.Contract() ?? throw new Xunit.Sdk.XunitException("No field table found.");

        Assert.Equal(InputKind.Documentation, contract.Kind);
        Assert.Equal(PayloadFormat.Json, contract.Format);
        Assert.Equal(11, contract.Fields.Count(f => f.Kind == FieldNodeKind.Value));
        Assert.Equal(FieldDataType.String, Field(contract, "$.merchant.legalName").DataType);
        Assert.Equal(Requirement.Required, Field(contract, "$.merchant.legalName").Required);
        Assert.Equal(100, Field(contract, "$.merchant.legalName").MaxLength);
        Assert.Equal(Requirement.Optional, Field(contract, "$.merchant.tradingName").Required);
        Assert.Equal(["CORP", "LLC", "SOLE"], Field(contract, "$.merchant.businessType").AllowedValues);
        Assert.Equal("Legal structure of the business", Field(contract, "$.merchant.businessType").Description);
        Assert.Equal(FieldDataType.Decimal, Field(contract, "$.merchant.annualCardVolume").DataType);
        Assert.Equal(FieldDataType.Date, Field(contract, "$.owners[*].dateOfBirth").DataType);
        Assert.Equal(Cardinality.Array, Field(contract, "$.owners").Cardinality);
        Assert.Equal("$.applicationRef", Field(contract, "$.applicationRef").Path);
        Assert.Contains("channel.cardPresentPercent", document.Text.Replace("\n", " ", StringComparison.Ordinal));
        Assert.DoesNotContain("Page 1", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_and_Word_versions_of_a_specification_give_the_same_fields()
    {
        static string Describe(ContractDocument contract) => string.Join("\n", contract.Fields.Select(f =>
            $"{f.Path} {f.DataType} {f.Required} {f.MaxLength} {string.Join("|", f.AllowedValues)} {f.Description}"));

        Assert.Equal(Describe(Sample("docx").Contract()!), Describe(Sample("pdf").Contract()!));
    }

    [Fact]
    public void A_table_caption_becomes_the_parent_of_plain_field_names()
    {
        IReadOnlyList<IReadOnlyList<string>> merchant = [["Field", "Type"], ["legalName", "string"], ["taxId", "string"]];
        IReadOnlyList<IReadOnlyList<string>> mixed = [["Field", "Type"], ["applicationId", "string"], ["owner.name", "string"]];

        var contract = FieldSpecReader.Read(
            "spec.docx",
            [new FieldTable(merchant, DocumentReader.Group("4.2 Merchant details"), "Table 1"), new FieldTable(mixed, "other", "Table 2")],
            "sha",
            InputKind.Documentation);

        Assert.Equal("merchantDetails", DocumentReader.Group("4.2 Merchant details"));
        Assert.Equal("owners", DocumentReader.Group("Table 3: Owners"));
        Assert.Null(DocumentReader.Group("1 Introduction to the merchant onboarding request fields and codes"));
        Assert.Contains(contract.Fields, f => f.Path == "$.merchantDetails.legalName");
        Assert.Contains(contract.Fields, f => f.Path == "$.applicationId");
        Assert.Contains(contract.Fields, f => f.Path == "$.owner.name");
    }

    [Fact]
    public void A_field_a_document_lists_twice_is_a_finding_not_an_error()
    {
        IReadOnlyList<IReadOnlyList<string>> first = [["Path", "Type"], ["a.b", "string"]];
        IReadOnlyList<IReadOnlyList<string>> second = [["Path", "Type"], ["a.b", "integer"]];

        var contract = FieldSpecReader.Read("spec.pdf", [new FieldTable(first, Label: "Table 1"), new FieldTable(second, Label: "Table 2")], "sha", InputKind.Documentation);

        Assert.Equal(FieldDataType.String, Field(contract, "$.a.b").DataType);
        Assert.Contains(contract.Findings, f => f.Path == "$.a.b" && f.Message.StartsWith("Table 2 row 2", StringComparison.Ordinal));
        var error = Assert.Throws<ProfileException>(() =>
            FieldSpecReader.Read("spec.csv", [new FieldTable(first, Label: "Table 1"), new FieldTable(second, Label: "Table 2")], "sha", InputKind.FieldSpec));
        Assert.Contains("(Table 1 row 2, Table 2 row 2)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_without_a_field_table_asks_for_AI_or_a_field_spec()
    {
        var error = Assert.Throws<ProfileException>(() => ProfileInputs.Split([new("notes.docx", Word(null))], null));
        Assert.Contains("'notes.docx' has no field table", error.Message, StringComparison.Ordinal);
        Assert.Contains("Let AI read", error.Message, StringComparison.Ordinal);

        var set = ProfileInputs.Read([new("notes.docx", Word(["Colour", "Size"], ["red", "L"]))], null);
        Assert.Empty(set.Contracts);
        Assert.Contains("Colour | Size", set.Documents.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_documents_are_reported()
    {
        Assert.Throws<ProfileException>(() => DocumentReader.Read("broken.pdf", "not a pdf"u8.ToArray()));
        Assert.Throws<ProfileException>(() => DocumentReader.Read("broken.docx", "not a docx"u8.ToArray()));
        Assert.Contains(".pdf", ProfileInputs.Extensions);
        Assert.Contains(".docx", ProfileInputs.Extensions);
    }

    private static FakeProvider Reader(string answer) => new("fake", _ => answer);

    [Fact]
    public async Task AI_reads_fields_from_text_masked_capped_and_marked_for_review()
    {
        var ai = Reader("""
            {"fields": [
              {"path": "channel.cardPresentPercent", "type": "integer", "required": "yes", "description": "Card-present share", "evidence": "It is mandatory.", "confidence": 95},
              {"path": "channel.ecommercePercent", "type": "integer", "required": "no", "allowedValues": ["0", "100"], "confidence": 40},
              {"path": "merchant.legalName", "type": "string", "confidence": 90},
              {"path": "not a path", "confidence": 50}
            ]}
            """);
        var document = Sample("pdf");

        var result = await new AiDocumentReader(ai).ReadAsync(document.Name, document.Sha256, document.Text, PayloadFormat.Json, ["$.merchant.legalName"]);

        var prompt = JsonNode.Parse(Assert.Single(ai.Prompts).Input)!;
        var sent = prompt["text"]!.GetValue<string>();
        Assert.DoesNotContain("onboarding@salesbeta.example", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("555 010 4477", sent, StringComparison.Ordinal);
        Assert.Contains("[email]", sent, StringComparison.Ordinal);
        Assert.Contains("### ### ####", sent, StringComparison.Ordinal);
        Assert.Contains("0 to 100", sent, StringComparison.Ordinal);

        var contract = result.Contract ?? throw new Xunit.Sdk.XunitException(string.Join("; ", result.Warnings));
        Assert.Equal("sales-beta-interface-spec.pdf (read by AI)", contract.Name);
        Assert.Equal(InputKind.Documentation, contract.Kind);
        Assert.Equal(FieldDataType.Integer, Field(contract, "$.channel.cardPresentPercent").DataType);
        Assert.Equal(Requirement.Required, Field(contract, "$.channel.cardPresentPercent").Required);
        Assert.DoesNotContain(contract.Fields, f => f.Path == "$.merchant.legalName");
        var review = contract.Findings.Where(f => f.Kind == ProfileFindingKind.AiExtracted).ToList();
        Assert.Equal(["$.channel.cardPresentPercent", "$.channel.ecommercePercent"], review.Select(f => f.Path));
        Assert.Contains("70% confidence, capped at 70%", review[0].Message, StringComparison.Ordinal);
        Assert.Contains("Evidence: \"It is mandatory.\"", review[0].Message, StringComparison.Ordinal);
        Assert.Contains("40% confidence", review[1].Message, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("'not a path'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AI_is_not_asked_when_there_is_no_text_and_bad_answers_become_warnings()
    {
        var ai = Reader("""{"fields": [{"path": "a.b", "confidence": 50}, {"path": "/A/B", "confidence": 50}]}""");

        var empty = await new AiDocumentReader(ai).ReadAsync("x.pdf", "sha", "  \n ", null, []);
        Assert.Null(empty.Contract);
        Assert.Empty(ai.Prompts);

        var mixed = await new AiDocumentReader(ai).ReadAsync("x.pdf", "sha", "Some text", null, []);
        Assert.Null(mixed.Contract);
        Assert.Contains(mixed.Warnings, w => w.Contains("mixes JSON paths", StringComparison.Ordinal));

        var unreadable = await new AiDocumentReader(Reader("not json")).ReadAsync("x.pdf", "sha", "Some text", null, []);
        Assert.Null(unreadable.Contract);
        Assert.Contains(unreadable.Warnings, w => w.Contains("unreadable answer", StringComparison.Ordinal));

        var chunks = await new AiDocumentReader(ai, chunkSize: 20, maxChunks: 2).ReadAsync("x.pdf", "sha", "line one of text\nline two of text\nline three of text", PayloadFormat.Json, []);
        Assert.Contains(chunks.Warnings, w => w.Contains("read only its first 40", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_API_reads_documents_by_rules_and_with_AI_on_request()
    {
        var ai = Reader("""{"fields": [{"path": "channel.cardPresentPercent", "type": "integer", "required": "yes", "confidence": 60}]}""");
        using var api = new ApiFactory(ai);
        var ana = api.As("ana");
        var pdf = Docs("sales-beta-interface-spec.pdf");

        var rules = await ana.Upload("/api/profiles", [pdf], ("system", "Sales Beta"));
        Assert.Equal(HttpStatusCode.Created, rules.StatusCode);
        Assert.DoesNotContain("cardPresentPercent", await rules.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(ai.Prompts);

        var withAi = await ana.Upload("/api/profiles", [pdf], ("system", "Sales Beta"), ("replace", "true"), ("useAi", "true"));
        Assert.Equal(HttpStatusCode.Created, withAi.StatusCode);
        var profile = await withAi.Node();
        Assert.Contains(profile["fields"]!.AsArray(), f => f!["path"].Text() == "$.channel.cardPresentPercent");
        Assert.Contains(profile["findings"]!.AsArray(), f => f!["kind"].Text() == "aiExtracted" && f["path"].Text() == "$.channel.cardPresentPercent");
        Assert.Contains(profile["inputs"]!.AsArray(), i => i!["name"].Text() == "sales-beta-interface-spec.pdf (read by AI)");

        var notes = Path.Combine(Path.GetTempPath(), $"notes-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(notes, Word(null));
        try
        {
            var noTable = await ana.Upload("/api/profiles", [notes], ("system", "Notes"));
            Assert.Equal(HttpStatusCode.BadRequest, noTable.StatusCode);
            Assert.Contains("has no field table", (await noTable.Node())["detail"].Text(), StringComparison.Ordinal);

            using var noAi = new ApiFactory();
            var unavailable = await noAi.As("ana").Upload("/api/profiles", [notes], ("system", "Notes"), ("useAi", "true"));
            Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        }
        finally
        {
            File.Delete(notes);
        }
    }
}
