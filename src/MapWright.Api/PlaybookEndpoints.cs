using MapWright.Core.Playbooks;
using MapWright.Core.Spec;
using MapWright.Store;
using Microsoft.AspNetCore.Mvc;

namespace MapWright.Api;

public sealed record NewVersionRequest(string? Version, string? Note);

public sealed record StatusChangeRequest(PlaybookStatus Status, string? Note);

public sealed record ValidationResponse(bool Valid, IReadOnlyList<SpecIssue> Issues);

public sealed record TestResponse(bool Passed, IReadOnlyList<PlaybookTestResult> Results);

/// <summary>
/// Playbooks are addressed as /api/playbooks/{kind}/{slug}/{version}, e.g. /api/playbooks/domain/tax-id/1.0.0.
/// Bodies and responses use the same JSON as the files in playbooks/.
/// </summary>
public static class PlaybookEndpoints
{
    private const string Kind = "{kind:regex(^(domain|process)$)}";

    public static void MapPlaybookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/playbooks").WithTags("Playbooks");

        group.MapGet("/", (PlaybookStore store, string? status) => store.List(ParseStatus(status)))
            .WithSummary("List playbook versions, optionally by status.");

        group.MapPost("/", async (HttpRequest request, PlaybookStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                var playbook = store.Create(await Read(request), actor);
                return Json(playbook, StatusCodes.Status201Created, request.HttpContext, Address(playbook));
            })
            .Accepts<Playbook>("application/json")
            .Produces<Playbook>(StatusCodes.Status201Created)
            .WithSummary("Create a new draft playbook.");

        group.MapPost("/validate", async (HttpRequest request, PlaybookStore store) => Validate(store, await Read(request)))
            .Accepts<Playbook>("application/json")
            .WithSummary("Validate an unsaved playbook against the published library.");

        group.MapPost("/test", async (HttpRequest request) => Test(await Read(request)))
            .Accepts<Playbook>("application/json")
            .WithSummary("Run an unsaved playbook's detection tests and rule examples.");

        group.MapGet($"/{Kind}/{{slug}}", (string kind, string slug, PlaybookStore store) => store.Versions($"{kind}/{slug}"))
            .WithSummary("List the versions of a playbook.");

        group.MapGet($"/{Kind}/{{slug}}/history", (string kind, string slug, PlaybookStore store) => store.History($"{kind}/{slug}"))
            .WithSummary("Every change and status transition of a playbook.");

        group.MapGet($"/{Kind}/{{slug}}/{{version}}", (string kind, string slug, string version, PlaybookStore store) =>
                Json(store.Get($"{kind}/{slug}", version)))
            .Produces<Playbook>()
            .WithSummary("Get one playbook version.");

        group.MapPut($"/{Kind}/{{slug}}/{{version}}", async (
                string kind, string slug, string version, HttpRequest request, PlaybookStore store,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                return Json(store.UpdateDraft($"{kind}/{slug}", version, await Read(request), actor));
            })
            .Accepts<Playbook>("application/json")
            .Produces<Playbook>()
            .WithSummary("Replace a draft playbook version.");

        group.MapPost($"/{Kind}/{{slug}}/{{version}}/versions", (
                string kind, string slug, string version, NewVersionRequest body, PlaybookStore store, HttpContext context,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var draft = store.DraftNewVersion($"{kind}/{slug}", version, body.Version, ApiErrors.Actor(user), body.Note);
                return Json(draft, StatusCodes.Status201Created, context, Address(draft));
            })
            .Produces<Playbook>(StatusCodes.Status201Created)
            .WithSummary("Draft a new version from this one (next minor version by default).");

        group.MapPost($"/{Kind}/{{slug}}/{{version}}/status", (
                string kind, string slug, string version, StatusChangeRequest body, PlaybookStore store,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
                Json(store.Transition($"{kind}/{slug}", version, body.Status, ApiErrors.Actor(user), body.Note)))
            .Produces<Playbook>()
            .WithSummary("Move a version through Draft → InReview → Published → Retired.");

        group.MapGet($"/{Kind}/{{slug}}/{{version}}/validate", (string kind, string slug, string version, PlaybookStore store) =>
                Validate(store, store.Get($"{kind}/{slug}", version)))
            .WithSummary("Validate a stored playbook version against the published library.");

        group.MapGet($"/{Kind}/{{slug}}/{{version}}/test", (string kind, string slug, string version, PlaybookStore store) =>
                Test(store.Get($"{kind}/{slug}", version)))
            .WithSummary("Run a stored playbook version's detection tests and rule examples.");
    }

    private static PlaybookStatus? ParseStatus(string? status) =>
        status is null ? null
        : Enum.TryParse<PlaybookStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed
        : throw new StoreException(StoreError.Invalid, $"Unknown status '{status}'; use draft, inReview, published or retired.");

    private static ValidationResponse Validate(PlaybookStore store, Playbook playbook)
    {
        var issues = store.Validate(playbook);
        return new(issues.All(i => i.Severity != IssueSeverity.Error), issues);
    }

    private static TestResponse Test(Playbook playbook)
    {
        var results = PlaybookTestRunner.Run(playbook);
        return new(results.All(r => r.Passed), results);
    }

    private static string Address(Playbook playbook) => $"/api/playbooks/{playbook.Id}/{playbook.Version}";

    private static async Task<Playbook> Read(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return PlaybookSerializer.Deserialize(await reader.ReadToEndAsync(request.HttpContext.RequestAborted));
    }

    private static IResult Json(Playbook playbook, int status = StatusCodes.Status200OK, HttpContext? context = null, string? location = null)
    {
        if (context is not null && location is not null)
        {
            context.Response.Headers.Location = location;
        }

        return Results.Text(PlaybookSerializer.Serialize(playbook), "application/json", statusCode: status);
    }
}
