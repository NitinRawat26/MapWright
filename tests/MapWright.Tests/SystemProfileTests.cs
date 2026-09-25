using MapWright.Core.Profile;

namespace MapWright.Tests;

public sealed class SystemProfileTests
{
    private static string SystemDir(string system) =>
        Path.Combine(AppContext.BaseDirectory, "samples", "systems", system);

    [Theory]
    [InlineData("sales-alpha", "SalesAlpha CRM", "2026.3", "CRM-based merchant application capture")]
    [InlineData("uw-core", "UW Core", "4.2", "Underwriting intake (SOAP/XML)")]
    public void Committed_sample_profiles_match_their_samples(string system, string name, string version, string description)
    {
        var committed = ProfileSerializer.Load(Path.Combine(SystemDir(system), "profile.json"));
        var samples = Directory.GetFiles(Path.Combine(SystemDir(system), "samples")).Order(StringComparer.Ordinal)
            .Select(f => new SampleInput(Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();

        var rebuilt = ProfileBuilder.Build(new() { System = name, Version = version, Description = description, Samples = samples })
            with
        { CreatedAt = committed.CreatedAt };

        Assert.Equal(ProfileSerializer.Serialize(rebuilt), ProfileSerializer.Serialize(committed));
        Assert.Empty(SystemProfileValidator.Validate(committed));
    }

    [Fact]
    public void Sample_profiles_capture_the_domain_differences()
    {
        var sales = ProfileSerializer.Load(Path.Combine(SystemDir("sales-alpha"), "profile.json"));
        var uw = ProfileSerializer.Load(Path.Combine(SystemDir("uw-core"), "profile.json"));
        ProfileField Sales(string path) => sales.Fields.Single(f => f.Path == path);
        ProfileField Uw(string path) => uw.Fields.Single(f => f.Path == path);

        Assert.Equal((0.2m, 1m), (Sales("$.owners[*].ownershipPercent").MinValue, Sales("$.owners[*].ownershipPercent").MaxValue));
        Assert.Equal(100m, Uw("/UnderwritingRequest/Officers/Officer/OwnershipPct").MaxValue);
        Assert.Equal(Cardinality(sales, "$.owners"), Cardinality(uw, "/UnderwritingRequest/Officers/Officer"));
        Assert.Equal(Requirement.Optional, Uw("/UnderwritingRequest/Merchant/WebsiteUrl").Required);
        Assert.Equal("MM/dd/yyyy", Uw("/UnderwritingRequest/Merchant/EstablishedDate").Format);
        Assert.Equal(["CORP", "LLC", "SOLE_PROP"], Sales("$.account.entityType").ObservedValues);
        Assert.All(sales.Fields.Concat(uw.Fields).Where(f => f.Sensitive), f => Assert.Empty(f.ObservedValues));
    }

    private static Core.Spec.Cardinality Cardinality(SystemProfile profile, string path) =>
        profile.Fields.Single(f => f.Path == path).Cardinality;

    [Fact]
    public void Serializer_round_trips_and_rejects_unknown_properties()
    {
        var profile = ProfileBuilderTests.Build(("a.json", """{ "a": [1, 2] }"""));

        var json = ProfileSerializer.Serialize(profile);
        Assert.Contains("\"kind\": \"samplePayload\"", json);
        Assert.Equal(json, ProfileSerializer.Serialize(ProfileSerializer.Deserialize(json)));

        var ex = Assert.Throws<ProfileException>(() => ProfileSerializer.Deserialize(json.Replace("\"system\":", "\"bogus\": 1, \"system\":")));
        Assert.Contains("Invalid system profile", ex.Message);
    }

    [Fact]
    public void Validator_reports_structural_and_masking_problems()
    {
        var profile = ProfileBuilderTests.Build(("a.json", """{ "a": { "b": 1 }, "ssn": "900-12-3456" }"""));
        var ssn = profile.Fields.Single(f => f.Path == "$.ssn");
        var broken = profile with
        {
            SpecVersion = "9",
            Fields =
            [
                .. profile.Fields.Where(f => f.Path != "$.a"),
                ssn with { SampleValue = "900-12-3456" },
                ssn with { Path = "$.x", ParentPath = "$.ssn", SeenIn = ["missing.json"] },
            ],
        };

        var codes = SystemProfileValidator.Validate(broken).Select(i => i.Code).ToList();

        Assert.Contains("PF001", codes);
        Assert.Contains("PF003", codes);
        Assert.Contains("PF004", codes);
        Assert.Contains("PF005", codes);
        Assert.Contains("PF006", codes);
        Assert.Contains("PF008", codes);
    }
}
