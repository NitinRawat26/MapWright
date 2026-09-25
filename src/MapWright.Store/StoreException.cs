using MapWright.Core.Spec;

namespace MapWright.Store;

public enum StoreError
{
    NotFound,
    Conflict,
    Invalid,
}

public sealed class StoreException(StoreError error, string message, IReadOnlyList<SpecIssue>? issues = null) : Exception(message)
{
    public StoreError Error { get; } = error;

    /// <summary>Validation issues or failed tests behind an <see cref="StoreError.Invalid"/> error.</summary>
    public IReadOnlyList<SpecIssue> Issues { get; } = issues ?? [];

    public static StoreException NotFound(string what) => new(StoreError.NotFound, $"{what} not found.");
}
