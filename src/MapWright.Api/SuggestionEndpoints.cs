using MapWright.Store;
using Microsoft.AspNetCore.Mvc;

namespace MapWright.Api;

/// <param name="Concept"><c>Concept</c> or <c>Concept.Attribute</c>; defaults to the concept the AI suggested.</param>
public sealed record ApproveSuggestionRequest(string? Concept = null, string? Comment = null);

public sealed record RejectSuggestionRequest(string? Comment = null);

/// <param name="PlaybookId">The domain playbook whose draft now carries the field's name as a vocabulary term.</param>
public sealed record ApprovedSuggestion(Suggestion Suggestion, string PlaybookId, string Version);

/// <summary>The AI suggestions inbox: review what AI said about unrecognised fields and turn approvals into draft playbook changes.</summary>
public static class SuggestionEndpoints
{
    public static void MapSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai", (AiAccess ai, PlaybookStore playbooks) =>
                new { available = ai.Provider is not null, provider = ai.Provider?.Name, maxConfidence = AiAccess.Cap(playbooks.Library()) })
            .WithTags("AI")
            .WithSummary("Whether an AI provider is configured, and the confidence cap applied to its answers.");

        var group = app.MapGroup("/api/suggestions").WithTags("AI");

        group.MapGet("/", (SuggestionStore store, string? status, string? profile) => store.List(ParseStatus(status), profile))
            .WithSummary("List AI suggestions, oldest first; filter by status (pending, approved, rejected) or profile id.");

        group.MapGet("/{id:long}", (long id, SuggestionStore store) => store.Get(id))
            .WithSummary("Get one suggestion.");

        group.MapPost("/{id:long}/approve", (
                long id, ApproveSuggestionRequest? body, SuggestionStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var (suggestion, draft) = store.Approve(id, ApiErrors.Actor(user), body?.Concept, body?.Comment);
                return new ApprovedSuggestion(suggestion, draft.Id, draft.Version);
            })
            .WithSummary("Approve: add the field's name as a vocabulary term to a draft of the concept's domain playbook.");

        group.MapPost("/{id:long}/reject", (
                long id, RejectSuggestionRequest? body, SuggestionStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
                store.Reject(id, ApiErrors.Actor(user), body?.Comment))
            .WithSummary("Reject a suggestion; nothing changes.");
    }

    private static SuggestionStatus? ParseStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null
        : Enum.TryParse<SuggestionStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed
        : throw new StoreException(StoreError.Invalid, $"Unknown status '{status}'; use pending, approved or rejected.");
}
