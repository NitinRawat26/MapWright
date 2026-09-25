using MapWright.Core.Spec;

namespace MapWright.Output.Report;

/// <summary>Renderer-agnostic view of a mapping document: summary plus every tab of the mapping workbook.</summary>
public sealed record MappingReport(
    MappingDocument Document,
    MappingSummary Summary,
    IReadOnlyList<KeyValuePair<string, string>> SummaryItems,
    ReportTable Mapping,
    ReportTable Gaps,
    ReportTable ValueMaps,
    ReportTable Findings,
    ReportTable Validation,
    ReportTable ChangeLog)
{
    public IEnumerable<ReportTable> SupportingTables => [Gaps, ValueMaps, Findings, Validation, ChangeLog];

    public static MappingReport Build(MappingDocument document)
    {
        var summary = MappingSummary.From(document);
        return new MappingReport(
            document,
            summary,
            BuildSummaryItems(document, summary),
            MappingSheet.Build(document),
            BuildGaps(document),
            BuildValueMaps(document),
            BuildFindings(document),
            BuildValidation(document),
            BuildChangeLog(document));
    }

    private static List<KeyValuePair<string, string>> BuildSummaryItems(MappingDocument d, MappingSummary s)
    {
        static KeyValuePair<string, string> Item(string key, string value) => new(key, value);

        return
        [
            Item("Mapping", d.Title),
            Item("Mapping ID", d.Id),
            Item("Mapping Version", d.Version),
            Item("Spec Version", d.SpecVersion),
            Item("Created", Display.Timestamp(d.CreatedAt)),
            Item("Description", d.Description ?? ""),
            Item("Source System", $"{d.Source.Name} v{d.Source.Version} ({d.Source.Format.ToString().ToUpperInvariant()})"),
            Item("Target System", $"{d.Target.Name} v{d.Target.Version} ({d.Target.Format.ToString().ToUpperInvariant()})"),
            Item("Inputs Used", Display.Join(d.Inputs.Select(i => $"[{i.Side}] {i.Name} ({Display.Of(i.Kind)})"))),
            Item("Playbooks Applied", Display.Join(d.Playbooks.Select(p => $"{p.Name}@{p.Version}"))),
            Item("Confidence Bands", $"High >= {d.ConfidencePolicy.HighThreshold}%, Medium >= {d.ConfidencePolicy.MediumThreshold}%, Low below"),
            Item("Target Fields", s.TotalTargetFields.ToString()),
            Item("Mapped", s.MappedTargetFields.ToString()),
            Item("Unmapped", s.UnmappedTargetFields.ToString()),
            Item("Required Target Coverage", $"{Display.Percent(s.RequiredCoveragePercent)} ({s.RequiredTargetFieldsMapped} of {s.RequiredTargetFields})"),
            Item("High / Medium / Low Confidence",
                $"{s.ByConfidenceBand[ConfidenceBand.High]} / {s.ByConfidenceBand[ConfidenceBand.Medium]} / {s.ByConfidenceBand[ConfidenceBand.Low]}"),
            Item("Review Status", string.Join(", ", s.ByReviewStatus.Where(kv => kv.Value > 0).Select(kv => $"{Display.Of(kv.Key)}: {kv.Value}"))),
            Item("Orphan Source Fields", s.OrphanSourceFields.ToString()),
            Item("Conflicts / Assumptions", $"{s.Conflicts} / {s.Assumptions}"),
            Item("Validation (pass / fail / skipped)", $"{s.ValidationPassed} / {s.ValidationFailed} / {s.ValidationSkipped}"),
        ];
    }

    private static ReportTable BuildGaps(MappingDocument d)
    {
        var rows = d.Mappings
            .Where(m => m.Type == MappingType.Unmapped)
            .OrderByDescending(m => m.Target.Required)
            .Select(m => new ReportRow(
            [
                m.Target.Required ? "Unmapped required target" : "Unmapped optional target",
                m.Id,
                m.Target.Name,
                m.Target.Path,
                m.Target.DataType,
                Display.YesNo(m.Target.Required),
                m.SuggestedResolution ?? "",
            ]))
            .Concat(d.OrphanSourceFields.Select(o => new ReportRow(
            [
                "Orphan source field",
                "",
                o.Field.Name,
                o.Field.Path,
                o.Field.DataType,
                Display.YesNo(o.Field.Required),
                o.SuggestedResolution ?? "",
            ])))
            .ToList();

        return new ReportTable(
            "Gaps",
            [
                new("Gap Type", Width: 24), new("Mapping ID", Width: 12), new("Field Name", Width: 24), new("Field Path", Width: 40),
                new("Datatype", Width: 12), new("Required", Width: 10), new("Suggested Resolution", Width: 60),
            ],
            rows);
    }

    private static ReportTable BuildValueMaps(MappingDocument d) => new(
        "Value Maps",
        [
            new("Mapping ID", Width: 12), new("Source Field", Width: 26), new("Target Field", Width: 26),
            new("Source Value", Width: 22), new("Target Value", Width: 22), new("Notes", Width: 50),
        ],
        d.Mappings
            .SelectMany(m => m.Transformation.ValueMap.Select(v => new ReportRow(
            [
                m.Id,
                Display.Join(m.Sources.Select(s => s.Name), ", "),
                m.Target.Name,
                v.SourceValue,
                v.TargetValue,
                v.Notes ?? "",
            ])))
            .ToList());

    private static ReportTable BuildFindings(MappingDocument d) => new(
        "Conflicts & Assumptions",
        [
            new("Finding ID", Width: 12), new("Kind", Width: 12), new("Description", Width: 60), new("Mapping IDs", Width: 16),
            new("Sources", Width: 36), new("Resolution", Width: 50),
        ],
        d.Findings
            .OrderBy(f => f.Kind)
            .Select(f => new ReportRow(
            [
                f.Id,
                Display.Humanize(f.Kind),
                f.Description,
                string.Join(", ", f.MappingIds),
                Display.Join(f.Sources),
                f.Resolution ?? "",
            ]))
            .ToList());

    private static ReportTable BuildValidation(MappingDocument d)
    {
        var targetNames = d.Mappings.ToDictionary(m => m.Id, m => m.Target.Name);
        return new ReportTable(
            "Validation",
            [
                new("Run ID", Width: 10), new("Ran At", Width: 20), new("Sample Payload", Width: 30), new("Mapping ID", Width: 12),
                new("Target Field", Width: 24), new("Outcome", Width: 10), new("Expected", Width: 20), new("Actual", Width: 20),
                new("Message", Width: 50),
            ],
            d.ValidationRuns
                .SelectMany(run => run.Results.Select(r => new ReportRow(
                    [
                        run.Id,
                        Display.Timestamp(run.RanAt),
                        run.SamplePayload,
                        r.MappingId,
                        targetNames.GetValueOrDefault(r.MappingId, ""),
                        Display.Humanize(r.Outcome),
                        r.Expected ?? "",
                        r.Actual ?? "",
                        r.Message ?? "",
                    ],
                    new Dictionary<string, string> { ["outcome"] = r.Outcome.ToString() })))
                .ToList());
    }

    private static ReportTable BuildChangeLog(MappingDocument d) => new(
        "Change Log",
        [new("Version", Width: 12), new("Date", Width: 12), new("Author", Width: 20), new("Description", Width: 80)],
        d.ChangeLog
            .Select(c => new ReportRow([c.Version, Display.Date(c.Date), c.Author, c.Description]))
            .ToList());
}
