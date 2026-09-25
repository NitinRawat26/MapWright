using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Playbooks;

/// <summary>What a domain playbook sees of a field: its name, surroundings and observed values.</summary>
public sealed record FieldContext
{
    public required string Name { get; init; }
    public string? Path { get; init; }

    /// <summary>Nearest first: the parent, then the grandparent, …</summary>
    public IReadOnlyList<string> Ancestors { get; init; } = [];
    public IReadOnlyList<string> Children { get; init; } = [];
    public string? Description { get; init; }
    public FieldNodeKind Kind { get; init; } = FieldNodeKind.Value;
    public FieldDataType DataType { get; init; } = FieldDataType.String;
    public Cardinality Cardinality { get; init; } = Cardinality.Single;
    public IReadOnlyList<string> Values { get; init; } = [];
    public IReadOnlyList<string> ValueShapes { get; init; } = [];
    public decimal? MinValue { get; init; }
    public decimal? MaxValue { get; init; }

    public static IReadOnlyList<FieldContext> FromProfile(SystemProfile profile)
    {
        var byPath = profile.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var children = profile.Fields.Where(f => f.ParentPath is not null).ToLookup(f => f.ParentPath!);
        return [.. profile.Fields.Select(field => From(field, byPath, children))];
    }

    private static FieldContext From(ProfileField field, Dictionary<string, ProfileField> byPath, ILookup<string, ProfileField> children)
    {
        var ancestors = new List<string>();
        for (var parent = field.ParentPath; parent is not null && byPath.TryGetValue(parent, out var p); parent = p.ParentPath)
        {
            ancestors.Add(p.Name);
        }

        return new()
        {
            Name = field.Name,
            Path = field.Path,
            Ancestors = ancestors,
            Children = [.. children[field.Path].Select(c => c.Name)],
            Description = field.Description,
            Kind = field.Kind,
            DataType = field.DataType,
            Cardinality = field.Cardinality,
            Values = field.ObservedValues.Count > 0 ? field.ObservedValues : field.AllowedValues,
            ValueShapes = field.ValueShapes,
            MinValue = field.MinValue,
            MaxValue = field.MaxValue,
        };
    }
}
