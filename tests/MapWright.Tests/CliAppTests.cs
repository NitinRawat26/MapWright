using MapWright.Cli;
using MapWright.Core.Matching;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;

namespace MapWright.Tests;

public sealed class CliAppTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mapwright-tests-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private int Run(params string[] args) => CliApp.Run(args, _out, _err);

    [Fact]
    public void Validate_sample_succeeds()
    {
        Assert.Equal(CliApp.Success, Run("validate", TestSpecs.SamplePath));
        Assert.Contains("valid (18 mappings", _out.ToString());
    }

    [Fact]
    public void Render_writes_all_formats()
    {
        Assert.Equal(CliApp.Success, Run("render", TestSpecs.SamplePath, "--out", _dir));

        foreach (var ext in new[] { "xlsx", "csv", "html", "pdf" })
        {
            Assert.True(new FileInfo(Path.Combine(_dir, $"sales-alpha__uw-core.{ext}")).Length > 0);
        }
    }

    [Fact]
    public void Render_honours_format_filter()
    {
        Assert.Equal(CliApp.Success, Run("render", TestSpecs.SamplePath, "--out", _dir, "--format", "csv"));

        Assert.Equal(["sales-alpha__uw-core.csv"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Invalid_spec_is_not_rendered()
    {
        var bad = TestSpecs.Minimal(TestSpecs.OneToOne("M1", "$.a", "/B/A", confidence: 150));
        var path = Path.Combine(_dir, "bad.json");
        MappingSpecSerializer.Save(bad, path);

        Assert.Equal(CliApp.InvalidSpec, Run("render", path, "--out", _dir));
        Assert.Contains("MW013", _err.ToString());
        Assert.Equal(["bad.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Malformed_json_reports_error()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal(CliApp.InvalidSpec, Run("validate", path));
        Assert.Contains("Invalid mapping spec", _err.ToString());
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("render")]
    [InlineData("render", "x.json", "--format", "docx")]
    [InlineData("render", "x.json", "--bogus")]
    [InlineData("profile", "x.json")]
    [InlineData("profile", "--system", "S")]
    [InlineData("profile", "x.json", "--system", "S", "--bogus")]
    [InlineData("playbook")]
    [InlineData("playbook", "frobnicate")]
    [InlineData("playbook", "validate")]
    [InlineData("playbook", "test", "--bogus")]
    [InlineData("playbook", "detect")]
    [InlineData("playbook", "detect", "p.json", "--bogus")]
    [InlineData("playbook", "convert", "p.json")]
    [InlineData("playbook", "convert", "p.json", "--to", "xml")]
    [InlineData("playbook", "convert", "--to", "yaml")]
    [InlineData("map")]
    [InlineData("map", "a.json")]
    [InlineData("map", "a.json", "b.json", "c.json")]
    [InlineData("map", "a.json", "b.json", "--bogus")]
    public void Usage_errors(params string[] args)
    {
        Assert.Equal(CliApp.UsageError, Run(args));
    }

    [Fact]
    public void Missing_spec_file_is_reported()
    {
        Assert.Equal(CliApp.InvalidSpec, Run("validate", Path.Combine(_dir, "nope.json")));
        Assert.Contains("Spec not found", _err.ToString());
    }
    private static string SalesSamples => Path.Combine(AppContext.BaseDirectory, "samples", "systems", "sales-alpha", "samples");

    [Fact]
    public void Profile_writes_a_valid_profile_from_a_directory()
    {
        var outPath = Path.Combine(_dir, "nested", "profile.json");

        Assert.Equal(CliApp.Success, Run("profile", SalesSamples, "--system", "SalesAlpha CRM", "--version", "2026.3", "--out", outPath));

        var profile = ProfileSerializer.Load(outPath);
        Assert.Equal(("SalesAlpha CRM", "2026.3", 3), (profile.System, profile.Version, profile.Inputs.Count));
        Assert.Empty(SystemProfileValidator.Validate(profile));
        Assert.Contains("5 sensitive field(s) masked", _out.ToString());
    }

    [Fact]
    public void Profile_prints_to_stdout_without_out_and_can_drop_values()
    {
        var sample = Path.Combine(_dir, "a.xml");
        File.WriteAllText(sample, "<R><Name>Blue</Name></R>");

        Assert.Equal(CliApp.Success, Run("profile", sample, "--system", "S", "--no-values"));

        var profile = ProfileSerializer.Deserialize(_out.ToString());
        Assert.All(profile.Fields, f => Assert.Null(f.SampleValue));
    }

    [Fact]
    public void Profile_reports_missing_and_mixed_samples()
    {
        Assert.Equal(CliApp.InvalidInput, Run("profile", Path.Combine(_dir, "nope.json"), "--system", "S"));
        Assert.Contains("Sample not found", _err.ToString());

        Assert.Equal(CliApp.InvalidInput, Run("profile", _dir, "--system", "S"));
        Assert.Contains("No .json or .xml samples", _err.ToString());

        File.WriteAllText(Path.Combine(_dir, "a.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "b.xml"), "<R/>");
        Assert.Equal(CliApp.InvalidInput, Run("profile", _dir, "--system", "S"));
        Assert.Contains("share a format", _err.ToString());
    }

    [Fact]
    public void Playbook_validate_and_test_pass_for_the_starter_set()
    {
        Assert.Equal(CliApp.Success, Run("playbook", "validate", StarterPlaybooks.Directory));
        Assert.Contains("6 playbook(s) valid (0 warning(s))", _out.ToString());

        Assert.Equal(CliApp.Success, Run("playbook", "test", StarterPlaybooks.Directory));
        Assert.Matches(@"(\d+) of \1 playbook test\(s\) passed", _out.ToString());
    }

    [Fact]
    public void Playbook_convert_writes_the_other_format_and_keeps_the_playbook()
    {
        var yaml = Path.Combine(_dir, "tax-id.yaml");
        File.Copy(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.yaml"), yaml);
        var json = Path.Combine(_dir, "json");

        Assert.Equal(CliApp.Success, Run("playbook", "convert", yaml, "--to", "json", "--out", json));
        var converted = File.ReadAllText(Path.Combine(json, "tax-id.json"));
        Assert.StartsWith("{", converted, StringComparison.Ordinal);
        Assert.Equal(PlaybookSerializer.Serialize(PlaybookSerializer.Load(yaml)), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(converted)));

        File.Delete(yaml);
        Assert.Equal(CliApp.Success, Run("playbook", "convert", json, "--to", "yaml"));
        Assert.Contains("Remove the original files", _out.ToString());
        var back = Path.Combine(json, "tax-id.yaml");
        Assert.Equal(PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(converted)), PlaybookSerializer.Serialize(PlaybookSerializer.Load(back)));

        Assert.Equal(CliApp.Success, Run("playbook", "convert", back, "--to", "yaml"));
        Assert.Contains("already YAML; skipped", _out.ToString());
        Assert.Equal(CliApp.InvalidInput, Run("playbook", "convert", Path.Combine(json, "tax-id.json"), "--to", "yaml"));
        Assert.Contains("already exists", _err.ToString());
    }

    [Fact]
    public void Playbook_errors_and_failing_tests_are_reported()
    {
        var principals = StarterPlaybooks.Get("domain/principals");
        var invalid = Path.Combine(_dir, "invalid.json");
        PlaybookSerializer.Save(principals with { Version = "one" }, invalid);

        Assert.Equal(CliApp.InvalidInput, Run("playbook", "validate", invalid));
        Assert.Contains("PB003", _err.ToString());

        var failing = Path.Combine(_dir, "failing.json");
        var domain = principals.Domain!;
        PlaybookSerializer.Save(principals with { Status = PlaybookStatus.Draft, Domain = domain with { Tests = [domain.Tests[0] with { Expect = null }] } }, failing);
        Assert.Equal(CliApp.InvalidInput, Run("playbook", "test", failing));
        Assert.Contains("FAIL domain/principals@1.0.0 detection PRN-T-01", _err.ToString());

        Assert.Equal(CliApp.InvalidInput, Run("playbook", "validate", Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void Playbook_detect_lists_recognised_and_remaining_fields()
    {
        var profile = Path.Combine(AppContext.BaseDirectory, "samples", "systems", "uw-core", "profile.json");

        Assert.Equal(CliApp.Success, Run("playbook", "detect", profile, "--playbooks", StarterPlaybooks.Directory));

        var output = _out.ToString();
        Assert.Contains("/UnderwritingRequest/Officers/Officer  Principal 65% review", output);
        Assert.Contains("/UnderwritingRequest/Processing/MonthlyVolume  ProcessingVolume.CardVolume [period=monthly]", output);
        Assert.Contains("/UnderwritingRequest/Merchant/MCC  -", output);
        Assert.Contains("14 of 24 field(s) recognised; 10 remaining.", output);
    }

    [Fact]
    public void Playbook_detect_reports_a_missing_profile()
    {
        Assert.Equal(CliApp.InvalidInput, Run("playbook", "detect", Path.Combine(_dir, "nope.json"), "--playbooks", StarterPlaybooks.Directory));
    }

    private static string SystemProfile(string system) => Path.Combine(AppContext.BaseDirectory, "samples", "systems", system, "profile.json");

    [Fact]
    public void Map_writes_a_valid_mapping_spec_and_a_summary()
    {
        var path = Path.Combine(_dir, "out", "mapping.json");

        Assert.Equal(CliApp.Success, Run("map", SystemProfile("sales-alpha"), SystemProfile("uw-core"), "--playbooks", StarterPlaybooks.Directory, "--out", path, "--id", "sa-uw", "--title", "SA to UW"));

        var document = MappingSpecSerializer.Load(path);
        Assert.Equal("sa-uw", document.Id);
        Assert.Equal("SA to UW", document.Title);
        Assert.DoesNotContain(MappingSpecValidator.Validate(document), i => i.Severity == IssueSeverity.Error);
        Assert.Contains($"Wrote {path}: 17 of 23 target field(s) mapped", _out.ToString());
        Assert.Contains("M017 /UnderwritingRequest/Processing/MonthlyVolume <- $.processing.annualCardVolume  PeriodConversion 95% AutoAccepted", _out.ToString());
        Assert.Equal(CliApp.Success, Run("render", path, "--out", _dir, "--format", "csv"));
    }

    [Fact]
    public void Map_without_out_prints_the_spec()
    {
        Assert.Equal(CliApp.Success, Run("map", SystemProfile("uw-core"), SystemProfile("sales-alpha"), "--playbooks", StarterPlaybooks.Directory));

        var document = MappingSpecSerializer.Deserialize(_out.ToString());
        Assert.Equal("UW Core", document.Source.Name);
        Assert.Equal("SalesAlpha CRM", document.Target.Name);
    }

    [Fact]
    public void Map_reports_a_missing_profile()
    {
        Assert.Equal(CliApp.InvalidInput, Run("map", Path.Combine(_dir, "nope.json"), SystemProfile("uw-core"), "--playbooks", StarterPlaybooks.Directory));
    }

    [Fact]
    public void Generated_sample_mapping_is_up_to_date()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "mappings", "sales-alpha__uw-core", "generated-mapping.json");
        var sample = MappingSpecSerializer.Load(path);

        var regenerated = MappingGenerator.Generate(
            ProfileSerializer.Load(SystemProfile("sales-alpha")),
            ProfileSerializer.Load(SystemProfile("uw-core")),
            StarterPlaybooks.Library(),
            new() { CreatedAt = sample.CreatedAt });

        Assert.Equal(MappingSpecSerializer.Serialize(sample), MappingSpecSerializer.Serialize(regenerated));
    }

    private static string MappingSample(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "samples", "mappings", "sales-alpha__uw-core", .. parts]);

    [Fact]
    public void Replay_writes_target_payloads_and_records_validation_runs()
    {
        var mapping = Path.Combine(_dir, "mapping.json");
        File.Copy(MappingSample("generated-mapping.json"), mapping);
        var outDir = Path.Combine(_dir, "replay");

        Assert.Equal(CliApp.Success, Run(
            "replay", mapping, SalesSamples, "--target", SystemProfile("uw-core"), "--playbooks", StarterPlaybooks.Directory,
            "--out", outDir, "--record"));

        Assert.Equal(["corp-three-owners.xml", "llc-two-owners.xml", "sole-prop.xml"], Directory.GetFiles(outDir).Select(Path.GetFileName).Order());
        Assert.Contains($"V002 llc-two-owners.json -> {Path.Combine(outDir, "llc-two-owners.xml")}: 21 passed, 4 failed, 3 skipped.", _out.ToString());
        Assert.Contains("  FAIL M002 /UnderwritingRequest/@requestId: Unmapped: no source field.", _out.ToString());
        Assert.Contains("Recorded 3 validation run(s)", _out.ToString());

        var recorded = MappingSpecSerializer.Load(mapping);
        Assert.Equal(["V001", "V002", "V003"], recorded.ValidationRuns.Select(r => r.Id));
        Assert.Equal("sole-prop.json", recorded.ValidationRuns[2].SamplePayload);
        Assert.DoesNotContain(MappingSpecValidator.Validate(recorded), i => i.Severity == IssueSeverity.Error);
        Assert.Equal(CliApp.Success, Run("render", mapping, "--out", _dir, "--format", "csv"));
    }

    [Fact]
    public void Replay_mask_writes_payloads_without_the_real_sensitive_values()
    {
        var outDir = Path.Combine(_dir, "masked");

        Assert.Equal(CliApp.Success, Run(
            "replay", MappingSample("generated-mapping.json"), Path.Combine(SalesSamples, "sole-prop.json"), "--target", SystemProfile("uw-core"),
            "--playbooks", StarterPlaybooks.Directory, "--out", outDir, "--mask"));

        var payload = File.ReadAllText(Path.Combine(outDir, "sole-prop.xml"));
        Assert.Contains("<SSN>*****5566</SSN>", payload);
        Assert.DoesNotContain("900445566", payload);
    }

    [Fact]
    public void Replay_strict_fails_when_a_check_fails_and_leaves_the_mapping_unchanged()
    {
        var mapping = Path.Combine(_dir, "mapping.json");
        File.Copy(MappingSample("generated-mapping.json"), mapping);
        var before = File.ReadAllText(mapping);

        Assert.Equal(CliApp.InvalidInput, Run(
            "replay", mapping, Path.Combine(SalesSamples, "sole-prop.json"), "--target", SystemProfile("uw-core"),
            "--playbooks", StarterPlaybooks.Directory, "--out", _dir, "--strict"));

        Assert.Equal(before, File.ReadAllText(mapping));
        Assert.True(File.Exists(Path.Combine(_dir, "sole-prop.xml")));
    }

    [Fact]
    public void Replay_reports_usage_and_input_errors()
    {
        var mapping = MappingSample("generated-mapping.json");

        Assert.Equal(CliApp.UsageError, Run("replay", mapping, SalesSamples));
        Assert.Contains("replay expects --target", _err.ToString());
        Assert.Equal(CliApp.UsageError, Run("replay", mapping, "--target", SystemProfile("uw-core")));

        Assert.Equal(CliApp.InvalidInput, Run("replay", mapping, SalesSamples, "--target", SystemProfile("sales-alpha"), "--out", _dir));
        Assert.Contains("Target profile is Json but the mapping's target is Xml.", _err.ToString());

        var uwSamples = Path.Combine(AppContext.BaseDirectory, "samples", "systems", "uw-core", "samples");
        Assert.Equal(CliApp.InvalidInput, Run(
            "replay", mapping, uwSamples, "--target", SystemProfile("uw-core"), "--playbooks", StarterPlaybooks.Directory, "--out", _dir));
        Assert.Contains("sample is Xml but the mapping's source is Json.", _err.ToString());

        Assert.Equal(CliApp.InvalidInput, Run("replay", mapping, Path.Combine(_dir, "nope.json"), "--target", SystemProfile("uw-core")));
        Assert.Contains("Sample not found", _err.ToString());
    }

    [Fact]
    public void Replay_sample_outputs_are_up_to_date()
    {
        Assert.Equal(CliApp.Success, Run(
            "replay", MappingSample("generated-mapping.json"), SalesSamples, "--target", SystemProfile("uw-core"),
            "--playbooks", StarterPlaybooks.Directory, "--xml-namespace", "urn:uwcore:intake:4.2", "--out", _dir));

        foreach (var name in new[] { "corp-three-owners.xml", "llc-two-owners.xml", "sole-prop.xml" })
        {
            Assert.Equal(File.ReadAllText(MappingSample("replay", name)), File.ReadAllText(Path.Combine(_dir, name)));
        }
    }
}
