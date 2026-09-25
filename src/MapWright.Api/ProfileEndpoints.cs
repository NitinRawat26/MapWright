using MapWright.Ai;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;
using MapWright.Output.Readers;
using MapWright.Store;
using Microsoft.AspNetCore.Mvc;

namespace MapWright.Api;

/// <param name="UseAi">Consent to send the remaining fields' masked metadata to the configured AI provider.</param>
public sealed record DetectRequest(bool UseAi = false);

/// <param name="Suggestions">AI answers, filed in the suggestions inbox for review.</param>
/// <param name="Remaining">Fields neither the playbooks nor the AI resolved.</param>
public sealed record DetectResponse(
    string System, IReadOnlyList<PlaybookMatch> Recognised, IReadOnlyList<Suggestion> Suggestions, IReadOnlyList<string> Remaining, IReadOnlyList<string> Warnings);

/// <summary>System profiles, built from uploaded samples and contracts or imported as profile JSON.</summary>
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profiles").WithTags("Profiles");

        group.MapGet("/", (ProfileStore store) => store.List())
            .WithSummary("List stored system profiles.");

        group.MapPost("/", async (
                IFormFileCollection files,
                [FromForm] string? system,
                [FromForm] string? id,
                [FromForm] string? version,
                [FromForm] string? description,
                [FromForm] string? root,
                [FromForm] bool? noValues,
                [FromForm] bool? replace,
                ProfileStore store,
                HttpContext context,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                if (string.IsNullOrWhiteSpace(system))
                {
                    throw new StoreException(StoreError.Invalid, "A 'system' name is required.");
                }

                var profileId = string.IsNullOrWhiteSpace(id) ? MapWrightDatabase.Slug(system) : id.Trim();
                if (replace != true && store.Find(profileId) is not null)
                {
                    throw new StoreException(StoreError.Conflict, $"Profile '{profileId}' already exists; send replace=true to rebuild it.");
                }

                var inputs = await Uploads.Read(files, ProfileInputs.Extensions, context.RequestAborted);
                var (samples, contracts) = ProfileInputs.Split(inputs, root);
                var profile = ProfileBuilder.Build(
                    new() { System = system.Trim(), Version = version, Description = description, Samples = samples, Contracts = contracts },
                    new() { RetainValues = noValues != true });
                store.Save(profileId, profile, actor);
                context.Response.Headers.Location = $"/api/profiles/{profileId}";
                return Results.Text(ProfileSerializer.Serialize(profile), "application/json", statusCode: StatusCodes.Status201Created);
            })
            .DisableAntiforgery()
            .Produces<SystemProfile>(StatusCodes.Status201Created)
            .WithSummary("Build a profile from sample payloads and contracts (JSON, XML, JSON Schema, OpenAPI, XSD, WSDL, CSV/Excel field specs).");

        group.MapGet("/{id}", (string id, ProfileStore store) => Results.Text(ProfileSerializer.Serialize(store.Get(id)), "application/json"))
            .Produces<SystemProfile>()
            .WithSummary("Get a profile.");

        group.MapPut("/{id}", async (string id, HttpRequest request, ProfileStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                using var reader = new StreamReader(request.Body);
                var profile = ProfileSerializer.Deserialize(await reader.ReadToEndAsync(request.HttpContext.RequestAborted));
                store.Save(id, profile, actor);
                return Results.Text(ProfileSerializer.Serialize(profile), "application/json");
            })
            .Accepts<SystemProfile>("application/json")
            .Produces<SystemProfile>()
            .WithSummary("Store a profile JSON (e.g. one built with the CLI) under this id.");

        group.MapDelete("/{id}", (string id, ProfileStore store) =>
            {
                store.Get(id);
                store.Delete(id);
                return Results.NoContent();
            })
            .WithSummary("Delete a profile.");

        group.MapPost("/{id}/detect", async (
                string id, DetectRequest? body, ProfileStore profiles, PlaybookStore playbooks, SuggestionStore suggestions, AiAccess ai,
                HttpContext context, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var profile = profiles.Get(id);
                var library = playbooks.Library();
                var report = Detection.Recognise(profile, library);
                if (body?.UseAi != true || report.Remaining.Count == 0)
                {
                    return new DetectResponse(report.System, report.Recognised, [], report.Remaining, []);
                }

                var actor = ApiErrors.Actor(user);
                var result = await new AiFieldAssistant(ai.Require(), AiAccess.Cap(library))
                    .DecodeAsync(profile, report.Remaining, library.Domains, context.RequestAborted);
                var names = profile.Fields.ToDictionary(f => f.Path, f => f.Name, StringComparer.Ordinal);
                var inbox = suggestions.Add(id, profile.System, result.Suggestions.Select(s => new SuggestionContent
                {
                    Path = s.Path,
                    FieldName = names[s.Path],
                    BusinessConcept = s.BusinessConcept,
                    DomainPlaybook = s.DomainPlaybook,
                    ProposedConcept = s.ProposedConcept,
                    Meaning = s.Meaning,
                    ConfidencePercent = s.ConfidencePercent,
                    Reasoning = s.Reasoning,
                    Question = s.Question,
                    Provider = s.Provider,
                    Model = s.Model,
                }), actor);
                return new DetectResponse(report.System, report.Recognised, inbox, result.Unresolved, result.Warnings);
            })
            .WithSummary("Show which business concept the published playbooks recognise in each field; with useAi, ask AI about the rest and file its answers in the suggestions inbox.");
    }
}

internal static class Detection
{
    public static DecodeReport Recognise(SystemProfile profile, PlaybookLibrary library)
    {
        var recognised = new List<PlaybookMatch>();
        var remaining = new List<string>();
        foreach (var field in FieldContext.FromProfile(profile).Where(f => f.Kind == FieldNodeKind.Value || f.Cardinality == Cardinality.Array))
        {
            if (library.Detect(field) is { } result)
            {
                recognised.Add(new(field.Path!, result));
            }
            else
            {
                remaining.Add(field.Path!);
            }
        }

        return new() { System = profile.System, Recognised = recognised, Remaining = remaining };
    }
}
