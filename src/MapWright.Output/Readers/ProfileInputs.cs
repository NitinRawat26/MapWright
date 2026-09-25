using System.Text;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Contracts;

namespace MapWright.Output.Readers;

public sealed record InputFile(string Name, byte[] Content);

/// <summary>Sorts profile inputs into sample payloads and contracts (schemas, service contracts, field specs).</summary>
public static class ProfileInputs
{
    public static IReadOnlyList<string> Extensions { get; } = [".json", ".xml", ".xsd", ".wsdl", ".csv", ".xlsx", ".yaml", ".yml"];

    public static (List<SampleInput> Samples, List<ContractDocument> Contracts) Split(IEnumerable<InputFile> inputs, string? root)
    {
        var samples = new List<SampleInput>();
        var contracts = new List<ContractDocument>();
        foreach (var input in inputs)
        {
            if (Path.GetExtension(input.Name).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                contracts.Add(FieldSpecWorkbook.Read(input.Name, input.Content));
                continue;
            }

            using var reader = new StreamReader(new MemoryStream(input.Content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            if (ContractReader.Detect(input.Name, content) is { } kind)
            {
                contracts.Add(ContractReader.Read(input.Name, content, kind, root));
            }
            else
            {
                samples.Add(new(input.Name, content));
            }
        }

        return (samples, contracts);
    }
}
