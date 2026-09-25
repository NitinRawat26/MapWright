using MapWright.Cli;
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
}
