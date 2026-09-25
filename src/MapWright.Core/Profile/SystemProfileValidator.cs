using MapWright.Core.Spec;

namespace MapWright.Core.Profile;

/// <summary>Semantic checks on a system profile that JSON deserialization alone cannot enforce.</summary>
public static class SystemProfileValidator
{
    public static IReadOnlyList<SpecIssue> Validate(SystemProfile profile)
    {
        var issues = new List<SpecIssue>();
        void Error(string code, string location, string message) => issues.Add(new(IssueSeverity.Error, code, location, message));
        void Warn(string code, string location, string message) => issues.Add(new(IssueSeverity.Warning, code, location, message));

        if (profile.SpecVersion != SystemProfile.CurrentSpecVersion)
        {
            Error("PF001", "specVersion", $"Unsupported profile version '{profile.SpecVersion}'; expected '{SystemProfile.CurrentSpecVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(profile.System))
        {
            Error("PF002", "system", "System name is required.");
        }

        var byPath = new Dictionary<string, ProfileField>(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            if (!byPath.TryAdd(field.Path, field))
            {
                Error("PF003", field.Path, "Duplicate field path.");
            }
        }

        var inputNames = profile.Inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in profile.Fields)
        {
            var at = field.Path;
            if (field.ParentPath is { } parentPath)
            {
                if (!byPath.TryGetValue(parentPath, out var parent))
                {
                    Error("PF004", at, $"Parent '{parentPath}' is not a field of this profile.");
                }
                else if (parent.Kind != FieldNodeKind.Object)
                {
                    Error("PF005", at, $"Parent '{parentPath}' is not an object.");
                }
            }

            if (field.Sensitive)
            {
                var visible = new[] { field.SampleValue }.Concat(field.ObservedValues)
                    .Where(v => v is not null && v.Count(char.IsAsciiDigit) > SensitiveDataPolicy.VisibleTrailingCharacters);
                if (visible.Any())
                {
                    Error("PF006", at, "Sensitive field stores unmasked values; keep at most the last 4 digits.");
                }
            }

            if (field.Presence is { } presence && presence.PresentInstances > presence.ParentInstances)
            {
                Error("PF007", at, "Present in more instances than its parent has.");
            }

            foreach (var input in field.SeenIn.Where(i => !inputNames.Contains(i)))
            {
                Warn("PF008", at, $"References unknown input '{input}'.");
            }
        }

        return issues;
    }
}
