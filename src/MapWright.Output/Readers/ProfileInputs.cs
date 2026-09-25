using System.Text;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;

namespace MapWright.Output.Readers;

public sealed record InputFile(string Name, byte[] Content);

/// <summary>Profile inputs sorted by kind. Documents' field tables are already in <see cref="Contracts"/>.</summary>
public sealed record ProfileInputSet(List<SampleInput> Samples, List<ContractDocument> Contracts, List<DocumentContent> Documents)
{
    /// <summary>Documents with no field table MapWright can read by rules.</summary>
    public IEnumerable<DocumentContent> Unread => Documents.Where(d => Contracts.All(c => c.Name != d.Name));

    /// <summary>The format of the system, when a contract or sample already decides it.</summary>
    public PayloadFormat? Format => Contracts.Select(c => (PayloadFormat?)c.Format).FirstOrDefault()
        ?? Samples.Select(s => (PayloadFormat?)(s.Content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<') ? PayloadFormat.Xml : PayloadFormat.Json)).FirstOrDefault();
}

/// <summary>Sorts profile inputs into sample payloads and contracts (schemas, service contracts, field specs).</summary>
public static class ProfileInputs
{
    public static IReadOnlyList<string> Extensions { get; } = [".json", ".xml", ".xsd", ".wsdl", ".csv", ".xlsx", ".yaml", ".yml", .. DocumentReader.Extensions];

    /// <summary>Samples and contracts; a document without a readable field table is an error.</summary>
    public static (List<SampleInput> Samples, List<ContractDocument> Contracts) Split(IEnumerable<InputFile> inputs, string? root)
    {
        var set = Read(inputs, root);
        if (set.Unread.FirstOrDefault() is { } unread)
        {
            throw new ProfileException(NoFieldTable(unread.Name));
        }

        return (set.Samples, set.Contracts);
    }

    public static string NoFieldTable(string name) =>
        $"'{name}' has no field table MapWright can read (a header row with a column such as 'Field Name' or 'Path'). " +
        "Let AI read its text (useAi, or 'Let AI read document text' on the Profiles page), or upload the fields as a CSV or Excel field spec.";

    /// <summary>
    /// Sorts the inputs. A file that another contract refers to (external <c>$ref</c>, <c>xs:import</c>,
    /// <c>xs:include</c>) is read as part of that contract rather than on its own.
    /// </summary>
    public static ProfileInputSet Read(IEnumerable<InputFile> inputs, string? root)
    {
        var samples = new List<SampleInput>();
        var contracts = new List<ContractDocument>();
        var documents = new List<DocumentContent>();
        var all = inputs.ToList();
        var texts = all.Where(IsText).Select(i => (Input: i, Content: Decode(i.Content))).ToList();
        var files = new ContractFiles(texts.Select(t => (t.Input.Name, t.Content)));
        var referenced = files.Referenced();
        foreach (var input in all)
        {
            if (DocumentReader.IsDocument(input.Name))
            {
                var document = DocumentReader.Read(input.Name, input.Content);
                documents.Add(document);
                if (document.Contract() is { } contract)
                {
                    contracts.Add(contract);
                }

                continue;
            }

            if (Path.GetExtension(input.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                contracts.Add(FieldSpecWorkbook.Read(input.Name, input.Content));
                continue;
            }

            if (referenced.Contains(input.Name))
            {
                continue;
            }

            var content = texts.First(t => ReferenceEquals(t.Input, input)).Content;
            if (ContractReader.Detect(input.Name, content) is { } kind)
            {
                contracts.Add(ContractReader.Read(input.Name, content, kind, root, files));
            }
            else
            {
                samples.Add(new(input.Name, content));
            }
        }

        return new(samples, contracts, documents);
    }

    private static bool IsText(InputFile input) =>
        !DocumentReader.IsDocument(input.Name) && !Path.GetExtension(input.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    private static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
