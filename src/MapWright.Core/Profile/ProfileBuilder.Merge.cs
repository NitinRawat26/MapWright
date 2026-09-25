using MapWright.Core.Profile.Contracts;
using MapWright.Core.Spec;

namespace MapWright.Core.Profile;

public static partial class ProfileBuilder
{
    private sealed record View(ContractDocument? Contract, ProfileField Field)
    {
        public string Input => Contract?.Name ?? "samples";

        /// <summary>Schemas declare every attribute; a field spec only the columns it fills in.</summary>
        public bool Declares(ProfileAttribute attribute) =>
            Contract is not null && (Rank(Contract.Kind) >= 3 || Field.Provenance.Any(p => p.Attribute == attribute));

        public AttributeSource? Source(ProfileAttribute attribute) => Field.Provenance.FirstOrDefault(p => p.Attribute == attribute);
    }

    private static int Rank(InputKind kind) => kind switch
    {
        InputKind.JsonSchema or InputKind.OpenApi or InputKind.Xsd or InputKind.Wsdl => 3,
        InputKind.FieldSpec or InputKind.MetadataExport => 2,
        _ => 1,
    };

    private static SystemProfile Merge(ProfileRequest request, SystemProfile? observed, TimeProvider? time)
    {
        var contracts = request.Contracts.OrderByDescending(c => Rank(c.Kind)).ToList();
        var formats = contracts.Select(c => (c.Name, c.Format))
            .Concat(observed is null ? [] : [("samples", observed.Format)])
            .ToList();
        if (formats.Select(f => f.Format).Distinct().Count() > 1)
        {
            throw new ProfileException(
                $"All inputs of one system must share a format; got {string.Join(" and ", formats.Select(f => $"'{f.Name}' ({f.Format})"))}.");
        }

        var views = new Dictionary<string, List<View>>(StringComparer.Ordinal);
        var order = new List<string>();
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var fields in contracts.Select(c => c.Fields.Select(f => new View(c, f)))
            .Concat(observed is null ? [] : [observed.Fields.Select(f => new View(null, f))]))
        {
            foreach (var view in fields)
            {
                parentOf.TryAdd(view.Field.Path, view.Field.ParentPath);
                if (!views.TryGetValue(view.Field.Path, out var list))
                {
                    views[view.Field.Path] = list = [];
                    Insert(order, view.Field, parentOf);
                }

                list.Add(view);
            }
        }

        var findings = new List<ProfileFinding>();
        var declared = string.Join(", ", contracts.Select(c => c.Name));
        var sampleNames = request.Samples.Select(s => s.Name).ToList();
        var merged = order.Select(path => Combine(views[path], views, findings, declared, sampleNames)).ToList();

        return new()
        {
            SpecVersion = SystemProfile.CurrentSpecVersion,
            System = request.System,
            Version = request.Version,
            Format = formats[0].Format,
            Description = request.Description,
            CreatedAt = (time ?? TimeProvider.System).GetUtcNow(),
            Inputs = [.. (observed?.Inputs ?? []).Concat(request.Contracts.Select(c => new ProfileInput
            {
                Name = c.Name,
                Kind = c.Kind,
                Format = c.Format,
                Sha256 = c.Sha256,
                Notes = c.Root is null ? null : $"Profiled {c.Root}.",
            })).Concat(request.Contracts.SelectMany(c => c.Referenced)).DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            Fields = merged,
            Findings = MergeFindings([.. observed?.Findings ?? [], .. request.Contracts.SelectMany(c => c.Findings), .. findings]),
        };
    }

    /// <summary>Places a field after the last field already listed under the same parent, keeping the first input's order.</summary>
    private static void Insert(List<string> order, ProfileField field, Dictionary<string, string?> parentOf)
    {
        bool Under(string path, string parent)
        {
            for (var current = parentOf.GetValueOrDefault(path); current is not null; current = parentOf.GetValueOrDefault(current))
            {
                if (current == parent)
                {
                    return true;
                }
            }

            return false;
        }

        var index = field.ParentPath is null ? -1 : order.FindLastIndex(p => p == field.ParentPath || Under(p, field.ParentPath));
        order.Insert(index < 0 ? order.Count : index + 1, field.Path);
    }

    private static ProfileField Combine(List<View> views, Dictionary<string, List<View>> all, List<ProfileFinding> findings, string declared, List<string> samples)
    {
        var contracts = views.Where(v => v.Contract is not null).ToList();
        var sample = views.FirstOrDefault(v => v.Contract is null)?.Field;
        var top = views[0];
        var path = top.Field.Path;
        void Report(ProfileFindingKind kind, string message, IEnumerable<string> inputs) =>
            findings.Add(new() { Kind = kind, Path = path, Message = message, Inputs = [.. inputs.Distinct()] });

        if (contracts.Count == 0)
        {
            var parent = sample!.ParentPath;
            if (parent is null || all[parent].Any(v => v.Contract is not null))
            {
                Report(ProfileFindingKind.UndeclaredField, $"Seen in samples but not declared in {declared}.", sample.SeenIn);
            }

            return sample;
        }

        var winners = new Dictionary<ProfileAttribute, View>();
        View Pick(ProfileAttribute attribute, Func<View, bool> has)
        {
            var winner = contracts.FirstOrDefault(v => v.Declares(attribute) && has(v))
                ?? views.FirstOrDefault(v => v.Contract is null && has(v))
                ?? top;
            winners[attribute] = winner;
            return winner;
        }

        var kind = top.Field.Kind;
        if (sample is not null && sample.Kind != kind)
        {
            Report(ProfileFindingKind.KindConflict,
                $"Samples show {(sample.Kind == FieldNodeKind.Object ? "an object" : "a value")} but {top.Input} declares {(kind == FieldNodeKind.Object ? "an object" : "a value")}.",
                [top.Input, .. sample.SeenIn]);
        }

        var typeView = Pick(ProfileAttribute.DataType, v => v.Field.DataType != FieldDataType.Unknown);
        var dataType = typeView.Field.DataType;
        var format = typeView.Field.Format;
        if (sample is not null && typeView.Contract is not null && sample.DataType is not FieldDataType.Unknown)
        {
            if (dataType == FieldDataType.String && sample.DataType is FieldDataType.Date or FieldDataType.DateTime && sample.Format is not null)
            {
                (dataType, format) = (sample.DataType, sample.Format);
                winners[ProfileAttribute.DataType] = winners[ProfileAttribute.Format] = views.First(v => v.Contract is null);
            }
            else if (!Compatible(dataType, sample.DataType))
            {
                Report(ProfileFindingKind.TypeConflict,
                    $"Samples look like {Name(sample.DataType)} but {typeView.Input} declares {Name(dataType)}.", [typeView.Input, .. sample.SeenIn]);
            }
            else if (format is not null && sample.Format is not null && sample.DataType == dataType && sample.Format != format)
            {
                Report(ProfileFindingKind.ContractMismatch,
                    $"Samples use {sample.Format} but {typeView.Input} declares {format}.", [typeView.Input, .. sample.SeenIn]);
            }
        }

        if (format is null && views.FirstOrDefault(v => v.Field.DataType == dataType && v.Field.Format is not null) is { } formatView)
        {
            format = formatView.Field.Format;
            winners[ProfileAttribute.Format] = formatView;
        }
        else
        {
            winners.TryAdd(ProfileAttribute.Format, typeView);
        }

        var cardinalityView = Pick(ProfileAttribute.Cardinality, _ => true);
        var cardinality = cardinalityView.Field.Cardinality;
        if (sample is not null && cardinalityView.Contract is not null && sample.Cardinality == Cardinality.Array && cardinality == Cardinality.Single)
        {
            Report(ProfileFindingKind.ContractMismatch, $"Samples repeat this field but {cardinalityView.Input} declares it once.", [cardinalityView.Input, .. sample.SeenIn]);
        }

        var requiredView = Pick(ProfileAttribute.Required, v => v.Field.Required != Requirement.Unknown);
        var required = requiredView.Field.Required;
        if (required == Requirement.Required && requiredView.Contract is not null && samples.Count > 0)
        {
            if (sample?.Presence is { } presence && (presence.PresentInstances < presence.ParentInstances || presence.NullCount + presence.EmptyCount > 0))
            {
                Report(ProfileFindingKind.ContractMismatch, $"{requiredView.Input} declares it required but it is missing or empty in some samples.", [requiredView.Input, .. sample.SeenIn]);
            }
            else if (sample is null && (top.Field.ParentPath is not { } parent || all[parent].Any(v => v.Contract is null)))
            {
                Report(ProfileFindingKind.ContractMismatch, $"{requiredView.Input} declares it required but no sample contains it.", [requiredView.Input, .. samples]);
            }
        }

        var allowedView = Pick(ProfileAttribute.AllowedValues, v => v.Field.AllowedValues.Count > 0);
        var allowed = allowedView.Field.AllowedValues;
        var sensitiveView = views.FirstOrDefault(v => v.Field.Sensitive);
        var sensitive = sensitiveView is not null;
        if (sample is not null && allowed.Count > 0 && allowedView.Contract is not null && !sample.Sensitive
            && sample.ObservedValues.Where(v => !allowed.Contains(v, StringComparer.Ordinal)).ToList() is { Count: > 0 } outside)
        {
            Report(ProfileFindingKind.ContractMismatch,
                sensitive
                    ? $"Samples contain {outside.Count} value(s) that {allowedView.Input} does not allow."
                    : $"Samples contain {string.Join(", ", outside)}, which {allowedView.Input} does not allow ({string.Join(", ", allowed)}).",
                [allowedView.Input, .. sample.SeenIn]);
        }

        T? Constraint<T>(Func<ProfileField, T?> read)
            where T : struct =>
            contracts.Select(v => read(v.Field)).FirstOrDefault(v => v is not null) ?? (sample is null ? null : read(sample));
        var constraintView = contracts.FirstOrDefault(v => v.Source(ProfileAttribute.Constraints) is not null);
        if (constraintView is not null)
        {
            winners[ProfileAttribute.Constraints] = constraintView;
        }

        var maxLength = Constraint(f => f.MaxLength);
        if (sample?.MaxLength is { } seenLength && contracts.FirstOrDefault(v => v.Field.MaxLength is not null) is { } lengthView && seenLength > lengthView.Field.MaxLength)
        {
            Report(ProfileFindingKind.ContractMismatch,
                $"Samples have values up to {seenLength} characters but {lengthView.Input} allows {lengthView.Field.MaxLength}.", [lengthView.Input, .. sample.SeenIn]);
        }

        if (sensitiveView is not null)
        {
            winners[ProfileAttribute.Sensitivity] = sensitiveView;
        }

        var numeric = dataType is FieldDataType.Integer or FieldDataType.Decimal;
        var unmask = sample is not null && !sample.Sensitive && sensitive;
        return new()
        {
            Path = path,
            Name = top.Field.Name,
            ParentPath = top.Field.ParentPath,
            Kind = kind,
            Cardinality = cardinality,
            DataType = dataType,
            Format = format,
            Required = required,
            MinLength = Constraint(f => f.MinLength),
            MaxLength = maxLength,
            MinValue = unmask ? contracts.Select(v => v.Field.MinValue).FirstOrDefault(v => v is not null) : numeric ? Constraint(f => f.MinValue) : null,
            MaxValue = unmask ? contracts.Select(v => v.Field.MaxValue).FirstOrDefault(v => v is not null) : numeric ? Constraint(f => f.MaxValue) : null,
            MaxScale = dataType == FieldDataType.Decimal ? Constraint(f => f.MaxScale) : null,
            MaxOccurs = cardinality == Cardinality.Array ? cardinalityView.Field.MaxOccurs ?? sample?.MaxOccurs : null,
            AllowedValues = allowed,
            ObservedValues = sample is null || unmask ? [] : sample.ObservedValues,
            ObservedValuesTruncated = sample is not null && !unmask && sample.ObservedValuesTruncated,
            ValueShapes = sample?.ValueShapes ?? [],
            SampleValue = sample?.SampleValue is { } value && unmask ? SensitiveDataPolicy.Mask(value) : sample?.SampleValue,
            Sensitive = sensitive,
            SensitivityReason = sensitiveView?.Field.SensitivityReason,
            Description = contracts.Select(v => v.Field.Description).FirstOrDefault(d => d is not null) ?? sample?.Description,
            Presence = sample?.Presence,
            SeenIn = [.. (sample?.SeenIn ?? []).Concat(contracts.Select(v => v.Input)).Distinct()],
            Provenance = [.. Enum.GetValues<ProfileAttribute>()
                .Select(a => winners.GetValueOrDefault(a)?.Source(a))
                .OfType<AttributeSource>()],
        };
    }

    private static bool Compatible(FieldDataType declared, FieldDataType seen) =>
        declared == seen
        || declared is FieldDataType.String or FieldDataType.Unknown
        || (declared == FieldDataType.Decimal && seen == FieldDataType.Integer);

    private static string Name(FieldDataType type) => type switch
    {
        FieldDataType.DateTime => "a date-time",
        FieldDataType.Integer => "an integer",
        FieldDataType.Object => "an object",
        _ => $"a {type.ToString().ToLowerInvariant()}",
    };
}
