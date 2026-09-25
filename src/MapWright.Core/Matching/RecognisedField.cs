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

    /// <summary>The nearest list (the field itself or an ancestor) and how the playbooks recognised it, e.g. $.owners → Principal.</summary>
    public string? ListPath { get; init; }
    public DetectionResult? ListDetection { get; init; }

    public string Path => Field.Path;

    public IReadOnlyList<string> Values => [.. Field.AllowedValues.Concat(Field.ObservedValues).Distinct(StringComparer.Ordinal)];

    /// <summary>Recognises every value field of a profile, in profile order.</summary>
    public static IReadOnlyList<RecognisedField> From(SystemProfile profile, PlaybookLibrary library)
    {
        var byPath = profile.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var contexts = profile.Fields.Zip(FieldContext.FromProfile(profile)).ToDictionary(p => p.First.Path, p => p.Second, StringComparer.Ordinal);
        return [.. profile.Fields
            .Where(f => f.Kind == FieldNodeKind.Value)
            .Select(f =>
            {
                var list = NearestList(f, byPath);
                return new RecognisedField
                {
                    Field = f,
                    Context = contexts[f.Path],
                    Detection = library.Detect(contexts[f.Path]),
                    Repeats = list is not null,
                    ListPath = list?.Path,
                    ListDetection = list is null ? null : library.Detect(contexts[list.Path]),
                };
            })];
    }

    private static ProfileField? NearestList(ProfileField field, Dictionary<string, ProfileField> byPath)
    {
        for (ProfileField? current = field; current is not null; current = current.ParentPath is { } parent ? byPath.GetValueOrDefault(parent) : null)
        {
            if (current.Cardinality == Cardinality.Array)
            {
                return current;
            }
        }

        return null;
    }
}
