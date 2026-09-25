namespace MapWright.Api;

public sealed class ApiOptions
{
    public const string Section = "MapWright";

    /// <summary>SQLite file; ":memory:" for a throw-away store.</summary>
    public string DatabasePath { get; set; } = "data/mapwright.db";

    /// <summary>Playbook directory imported when the store has no playbooks; relative paths resolve from the app folder.</summary>
    public string? SeedPlaybooks { get; set; } = "playbooks";

    public bool RequireIndependentReview { get; set; } = true;
}
