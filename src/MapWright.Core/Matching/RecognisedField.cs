using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Core.Matching;

/// <summary>A value field of a profile and how the domain playbooks recognised it.</summary>
public sealed record RecognisedField
{
    public required ProfileField Field { get; init; }
    public required FieldContext Context { get; init; }
    public DetectionResult? Detection { get; init; }

    /// <summary>The field or one of its ancestors is a list, so the field can hold several values per payload.</summary>
    public bool Repeats { get; init; }

    public string Path => Field.Path;

    public IReadOnlyList<string> Values => [.. Field.AllowedValues.Concat(Field.ObservedValues).Distinct(StringComparer.Ordinal)];

    /// <summary>Recognises every value field of a profile, in profile order.</summary>
    public static IReadOnlyList<RecognisedField> From(SystemProfile profile, PlaybookLibrary library)
    {
        var byPath = profile.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        return [.. profile.Fields
            .Zip(FieldContext.FromProfile(profile))
            .Where(p => p.First.Kind == FieldNodeKind.Value)
            .Select(p => new RecognisedField
            {
                Field = p.First,
                Context = p.Second,
                Detection = library.Detect(p.Second),
                Repeats = Repeating(p.First, byPath),
            })];
    }

    private static bool Repeating(ProfileField field, Dictionary<string, ProfileField> byPath)
    {
        for (ProfileField? current = field; current is not null; current = current.ParentPath is { } parent ? byPath.GetValueOrDefault(parent) : null)
        {
            if (current.Cardinality == Cardinality.Array)
            {
                return true;
            }
        }

        return false;
    }
}
