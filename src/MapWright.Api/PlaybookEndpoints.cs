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
/// Bodies are the same YAML or JSON as the files in playbooks/ (JSON when the text starts with '{'). Responses are
/// JSON unless the request asks for YAML with <c>?format=yaml</c> or an <c>Accept</c> header naming YAML; the YAML
/// is the text the version was written in, comments included.
/// </summary>
public static class PlaybookEndpoints
{
    private const string Kind = "{kind:regex(^(domain|process)$)}";

    private const string YamlType = "application/yaml";

    private static readonly string[] BodyTypes = ["application/json", YamlType];

    public static void MapPlaybookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/playbooks").WithTags("Playbooks");
        group.AddEndpointFilter((context, next) =>
        {
            WantsYaml(context.HttpContext.Request);
            return next(context);
        });

        group.MapGet("/", (PlaybookStore store, string? status) => store.List(ParseStatus(status)))
            .WithSummary("List playbook versions, optionally by status.");

        group.MapPost("/", async (HttpRequest request, PlaybookStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                var (body, yaml) = await Read(request);
                var playbook = store.Create(body, actor, yaml);
                return Respond(request.HttpContext, store, playbook, StatusCodes.Status201Created, Address(playbook));
            })
            .Accepts<Playbook>(BodyTypes[0], BodyTypes[1..])
            .Produces<Playbook>(StatusCodes.Status201Created, BodyTypes[0], BodyTypes[1..])
            .WithSummary("Create a new draft playbook from YAML or JSON.");

        group.MapPost("/validate", async (HttpRequest request, PlaybookStore store) => Validate(store, (await Read(request)).Playbook))
            .Accepts<Playbook>(BodyTypes[0], BodyTypes[1..])
            .WithSummary("Validate an unsaved playbook against the published library.");

        group.MapPost("/test", async (HttpRequest request) => Test((await Read(request)).Playbook))
            .Accepts<Playbook>(BodyTypes[0], BodyTypes[1..])
            .WithSummary("Run an unsaved playbook's detection tests and rule examples.");

        group.MapGet($"/{Kind}/{{slug}}", (string kind, string slug, PlaybookStore store) => store.Versions($"{kind}/{slug}"))
            .WithSummary("List the versions of a playbook.");

        group.MapGet($"/{Kind}/{{slug}}/history", (string kind, string slug, PlaybookStore store) => store.History($"{kind}/{slug}"))
            .WithSummary("Every change and status transition of a playbook.");

        group.MapGet($"/{Kind}/{{slug}}/{{version}}", (string kind, string slug, string version, PlaybookStore store, HttpContext context) =>
                Respond(context, store, store.Get($"{kind}/{slug}", version)))
            .Produces<Playbook>(StatusCodes.Status200OK, BodyTypes[0], BodyTypes[1..])
            .WithSummary("Get one playbook version (add ?format=yaml for YAML).");

        group.MapPut($"/{Kind}/{{slug}}/{{version}}", async (
                string kind, string slug, string version, HttpRequest request, PlaybookStore store,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                var (body, yaml) = await Read(request);
                return Respond(request.HttpContext, store, store.UpdateDraft($"{kind}/{slug}", version, body, actor, yaml));
            })
            .Accepts<Playbook>(BodyTypes[0], BodyTypes[1..])
            .Produces<Playbook>(StatusCodes.Status200OK, BodyTypes[0], BodyTypes[1..])
            .WithSummary("Replace a draft playbook version.");

        group.MapPost($"/{Kind}/{{slug}}/{{version}}/versions", (
                string kind, string slug, string version, NewVersionRequest body, PlaybookStore store, HttpContext context,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var draft = store.DraftNewVersion($"{kind}/{slug}", version, body.Version, ApiErrors.Actor(user), body.Note);
                return Respond(context, store, draft, StatusCodes.Status201Created, Address(draft));
            })
            .Produces<Playbook>(StatusCodes.Status201Created, BodyTypes[0], BodyTypes[1..])
            .WithSummary("Draft a new version from this one (next minor version by default).");

        group.MapPost($"/{Kind}/{{slug}}/{{version}}/status", (
                string kind, string slug, string version, StatusChangeRequest body, PlaybookStore store, HttpContext context,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
                Respond(context, store, store.Transition($"{kind}/{slug}", version, body.Status, ApiErrors.Actor(user), body.Note)))
            .Produces<Playbook>(StatusCodes.Status200OK, BodyTypes[0], BodyTypes[1..])
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

    /// <summary>The playbook in the body, and the body itself when it is YAML.</summary>
    private static async Task<(Playbook Playbook, string? Yaml)> Read(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        return (PlaybookSerializer.Deserialize(text), PlaybookSerializer.Detect(text) == PlaybookFormat.Yaml ? text : null);
    }

    private static bool WantsYaml(HttpRequest request) =>
        request.Query["format"].ToString().ToLowerInvariant() switch
        {
            "yaml" or "yml" => true,
            "json" => false,
            "" => request.Headers.Accept.ToString().Contains("yaml", StringComparison.OrdinalIgnoreCase),
            var other => throw new StoreException(StoreError.Invalid, $"Unknown format '{other}'; use json or yaml."),
        };

    private static IResult Respond(HttpContext context, PlaybookStore store, Playbook playbook, int status = StatusCodes.Status200OK, string? location = null)
    {
        var yaml = WantsYaml(context.Request);
        if (location is not null)
        {
            context.Response.Headers.Location = location;
        }

        return yaml
            ? Results.Text(store.GetYaml(playbook.Id, playbook.Version), YamlType, statusCode: status)
            : Results.Text(PlaybookSerializer.Serialize(playbook), "application/json", statusCode: status);
    }
}
