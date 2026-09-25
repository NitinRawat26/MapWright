using MapWright.Cli;
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

        foreach (var ext in new[] { "xlsx", "csv", "html" })
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
    [InlineData("render", "x.json", "--format", "pdf")]
    [InlineData("render", "x.json", "--bogus")]
    [InlineData("profile", "x.json")]
    [InlineData("profile", "--system", "S")]
    [InlineData("profile", "x.json", "--system", "S", "--bogus")]
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
}
