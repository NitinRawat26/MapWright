using MapWright.Core.Playbooks;

namespace MapWright.Tests;

public sealed class PlaybookYamlTests
{
    private const string Minimal = """
        # A comment the store keeps.
        specVersion: 1.0
        id: process/review-only
        name: Review only   # trailing comments are fine
        kind: process
        version: 1.0.0
        status: draft
        changeNotes:
          - version: 1.0.0
            date: 2026-09-25
            author: ana
            description: |
              First line.
              Second line.
        process:
          steps:
            - id: S1
              name: Review
              kind: review
        """;

    [Theory]
    [MemberData(nameof(StarterPlaybooks.Files), MemberType = typeof(StarterPlaybooks))]
    public void Starter_playbooks_survive_yaml_and_json_round_trips(string file)
    {
        var text = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, file));
        var playbook = PlaybookSerializer.Deserialize(text);
        var json = PlaybookSerializer.Serialize(playbook);

        Assert.Equal(json, PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(PlaybookSerializer.SerializeYaml(playbook))));
        Assert.Equal(json, PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(PlaybookYaml.ToJson(text))));
        Assert.Equal(json, PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(PlaybookYaml.FromJson(PlaybookYaml.ToJson(text)))));
    }

    [Fact]
    public void Unquoted_numbers_and_dates_are_read_as_text_and_written_quoted()
    {
        var playbook = PlaybookSerializer.Deserialize(Minimal);

        Assert.Equal(("1.0", "1.0.0", PlaybookStatus.Draft), (playbook.SpecVersion, playbook.Version, playbook.Status));
        Assert.Equal("First line.\nSecond line.\n", playbook.ChangeNotes[0].Description);
        var yaml = PlaybookSerializer.SerializeYaml(playbook);
        Assert.Contains("specVersion: \"1.0\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    date: \"2026-09-25\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    description: |\n      First line.\n      Second line.\n", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name: [unclosed", "Invalid YAML at line")]
    [InlineData("a: 1\n---\nb: 2", "one YAML document")]
    [InlineData("base: &a { x: 1 }\ncopy: *a", "aliases are not supported")]
    [InlineData("name: !!binary abc", "tag")]
    [InlineData("", "empty")]
    public void Unsupported_or_malformed_yaml_is_rejected(string yaml, string message)
    {
        var ex = Assert.Throws<PlaybookException>(() => PlaybookSerializer.Deserialize(yaml));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_name_the_yaml_line()
    {
        var yaml = Minimal.Replace("kind: review", "kind: approve", StringComparison.Ordinal);

        var ex = Assert.Throws<PlaybookException>(() => PlaybookSerializer.Deserialize(yaml));

        Assert.Contains("'$.process.steps[0].kind' (line 19)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_made_outside_the_yaml_keep_its_comments()
    {
        var before = PlaybookSerializer.Deserialize(Minimal);
        var after = before with
        {
            Version = "1.1.0",
            Status = PlaybookStatus.InReview,
            ChangeNotes = [.. before.ChangeNotes, new PlaybookChange { Version = "1.1.0", Date = new DateOnly(2026, 9, 26), Author = "ben", Description = "Second." }],
        };

        var updated = PlaybookYaml.Update(Minimal, before, after);

        Assert.NotNull(updated);
        Assert.Equal(PlaybookSerializer.Serialize(after), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(updated)));
        Assert.Contains("# A comment the store keeps.", updated, StringComparison.Ordinal);
        Assert.Contains("# trailing comments are fine", updated, StringComparison.Ordinal);
        Assert.Contains("version: \"1.1.0\"\n", updated, StringComparison.Ordinal);
        Assert.Contains("status: inReview\n", updated, StringComparison.Ordinal);
        Assert.Contains("  - version: \"1.1.0\"\n    date: \"2026-09-26\"\n    author: ben\n    description: Second.\n", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Terms_appended_to_a_starter_playbook_keep_its_comments()
    {
        var yaml = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.yaml"));
        var before = PlaybookSerializer.Deserialize(yaml);
        var after = before with { Domain = before.Domain! with { Vocabulary = [.. before.Domain.Vocabulary, new VocabularyTerm { Term = "vat number", AppliesTo = "TaxId" }] } };

        var updated = PlaybookYaml.Update(yaml, before, after);

        Assert.NotNull(updated);
        Assert.Equal(PlaybookSerializer.Serialize(after), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(updated)));
        Assert.Equal(yaml.Split('#').Length, updated.Split('#').Length);
        Assert.Contains("    - term: vat number\n      appliesTo: TaxId\n", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Added_keys_and_rewritten_block_text_keep_the_other_comments()
    {
        var before = PlaybookSerializer.Deserialize(Minimal);
        var after = before with
        {
            Owner = "Underwriting",
            ChangeNotes = [before.ChangeNotes[0] with { Description = "Only line." }],
        };

        var updated = PlaybookYaml.Update(Minimal, before, after);

        Assert.NotNull(updated);
        Assert.Equal(PlaybookSerializer.Serialize(after), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(updated)));
        Assert.Contains("# A comment the store keeps.", updated, StringComparison.Ordinal);
        Assert.Contains("name: Review only   # trailing comments are fine\n", updated, StringComparison.Ordinal);
        Assert.Contains("    description: Only line.\nprocess:\n", updated, StringComparison.Ordinal);
        Assert.EndsWith("      kind: review\nowner: Underwriting", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Removed_and_inserted_list_items_and_keys_keep_the_other_comments()
    {
        var yaml = File.ReadAllText(Path.Combine(StarterPlaybooks.Directory, "domain", "tax-id.yaml"));
        var before = PlaybookSerializer.Deserialize(yaml);
        var terms = before.Domain!.Vocabulary;
        var after = before with
        {
            Owner = null,
            Domain = before.Domain with { Vocabulary = [terms[1], new VocabularyTerm { Term = "vat number", AppliesTo = "TaxId" }, .. terms.Skip(2)] },
        };

        var updated = PlaybookYaml.Update(yaml, before, after);

        Assert.NotNull(updated);
        Assert.Equal(PlaybookSerializer.Serialize(after), PlaybookSerializer.Serialize(PlaybookSerializer.Deserialize(updated)));
        Assert.StartsWith("# Domain playbook: Tax ID.\n", updated, StringComparison.Ordinal);
        Assert.Contains("  # Names systems use for the concept or one of its attributes", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("owner:", updated, StringComparison.Ordinal);
        Assert.Contains("    - term: vat number\n      appliesTo: TaxId\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain($"term: {terms[0].Term}\n", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Changes_that_cannot_be_applied_in_place_return_null()
    {
        var before = PlaybookSerializer.Deserialize(Minimal);

        Assert.Null(PlaybookYaml.Update(PlaybookSerializer.Serialize(before), before, before with { Owner = "Underwriting" }));
        Assert.Null(PlaybookYaml.Update(null, before, before with { Status = PlaybookStatus.Published }));
        Assert.Same(Minimal, PlaybookYaml.Update(Minimal, before, before));
    }

    [Fact]
    public void Yaml_files_are_saved_and_loaded_as_yaml()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapwright-{Guid.NewGuid():N}.yml");
        try
        {
            var playbook = StarterPlaybooks.Get("domain/channel-mix");
            PlaybookSerializer.Save(playbook, path);

            var file = PlaybookSerializer.Read(path);
            Assert.Equal(PlaybookFormat.Yaml, PlaybookSerializer.Detect(File.ReadAllText(path)));
            Assert.NotNull(file.Yaml);
            Assert.Equal(PlaybookSerializer.Serialize(playbook), PlaybookSerializer.Serialize(file.Playbook));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
