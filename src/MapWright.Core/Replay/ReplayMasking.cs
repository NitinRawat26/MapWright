using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Replay;

/// <summary>Masks sensitive values before a target payload is written, so replay output can be shared without the real sample data.</summary>
public static class ReplayMasking
{
    /// <summary>
    /// Keeps the last four letters/digits of every value that is personal or card data by the row's risk, whose source or
    /// target name is sensitive (SSN, account number…), or whose target field the target profile marks sensitive.
    /// Business amounts (financial risk) stay readable. Masked values are written as text.
    /// </summary>
    public static IReadOnlyList<TargetValue> Mask(IReadOnlyList<TargetValue> values, MappingDocument mapping, SystemProfile target)
    {
        var sensitiveTargets = target.Fields.Where(f => f.Sensitive).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var rows = mapping.Mappings.ToDictionary(m => m.Id, StringComparer.Ordinal);
        bool Sensitive(TargetValue value) => sensitiveTargets.Contains(value.Path)
            || (rows.TryGetValue(value.MappingId, out var row) && (Personal(row) || sensitiveTargets.Contains(row.Target.Path)));
        return [.. values.Select(v => Sensitive(v) ? v with { Value = SensitiveDataPolicy.Mask(v.Value), DataType = "string" } : v)];
    }

    private static bool Personal(FieldMapping row) =>
        row.Risk.Sensitivity is Sensitivity.Pii or Sensitivity.SensitivePii or Sensitivity.Pci
        || row.Sources.Concat([row.Target]).Any(f => SensitiveDataPolicy.Default.MatchField(f.Name, null) is not null);
}
