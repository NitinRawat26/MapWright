using MapWright.Ai;
using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;
using MapWright.Output.Readers;
using MapWright.Store;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace MapWright.Api;

/// <param name="UseAi">Consent to send the remaining fields' masked metadata to the configured AI provider.</param>
public sealed record DetectRequest(bool UseAi = false);

/// <param name="Suggestions">AI answers, filed in the suggestions inbox for review.</param>
/// <param name="Remaining">Fields neither the playbooks nor the AI resolved.</param>
public sealed record DetectResponse(
    string System, IReadOnlyList<PlaybookMatch> Recognised, IReadOnlyList<Suggestion> Suggestions, IReadOnlyList<string> Remaining, IReadOnlyList<string> Warnings)
{
    public bool UsedAi { get; init; }

    /// <summary>When the result was saved; every run replaces the profile's previous result.</summary>
    public DateTimeOffset? DetectedAt { get; init; }

    public string? DetectedBy { get; init; }

    /// <summary>Why a saved result may be out of date: the profile or the published playbooks changed since.</summary>
    public IReadOnlyList<string> Stale { get; init; } = [];
}

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
                [FromForm] bool? useAi,
                ProfileStore store,
                PlaybookStore playbooks,
                AiAccess ai,
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
                var set = ProfileInputs.Read(inputs, root);
                var aiFindings = useAi == true && set.Documents.Count > 0
                    ? await Detection.ReadWithAi(set, ai, playbooks, context.RequestAborted)
                    : [];
                if (set.Documents.FirstOrDefault(d => set.Contracts.All(c => c.Name != d.Name && c.Name != d.Name + AiDocumentReader.Suffix)) is { } unread)
                {
                    throw new StoreException(StoreError.Invalid, useAi == true
                        ? $"Neither the rules nor AI found fields in '{unread.Name}'. {string.Join(" ", aiFindings.Where(f => f.Inputs.Contains(unread.Name)).Select(f => f.Message))}".TrimEnd()
                        : ProfileInputs.NoFieldTable(unread.Name));
                }

                var profile = ProfileBuilder.Build(
                    new() { System = system.Trim(), Version = version, Description = description, Samples = set.Samples, Contracts = set.Contracts },
                    new() { RetainValues = noValues != true });
                profile = profile with { Findings = [.. profile.Findings, .. aiFindings] };
                store.Save(profileId, profile, actor);
                context.Response.Headers.Location = $"/api/profiles/{profileId}";
                return Results.Text(ProfileSerializer.Serialize(profile), "application/json", statusCode: StatusCodes.Status201Created);
            })
            .DisableAntiforgery()
            .Produces<SystemProfile>(StatusCodes.Status201Created)
            .WithSummary("Build a profile from sample payloads and contracts (JSON, XML, JSON Schema, OpenAPI, XSD, WSDL, CSV/Excel field specs, PDF/Word specs); with useAi, AI also reads the documents' text.");

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
                string id, DetectRequest? body, ProfileStore profiles, PlaybookStore playbooks, SuggestionStore suggestions, DetectionStore detections,
                AiAccess ai, IOptions<HttpJsonOptions> json, HttpContext context, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var profile = profiles.Get(id);
                var library = playbooks.Library();
                var report = Detection.Recognise(profile, library);
                if (body?.UseAi != true || report.Remaining.Count == 0)
                {
                    var actorOrAnyone = string.IsNullOrWhiteSpace(user) ? Detection.Anonymous : user.Trim();
                    return Detection.Save(detections, id, new(report.System, report.Recognised, [], report.Remaining, []), false, library, actorOrAnyone, json.Value);
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
                return Detection.Save(detections, id, new(report.System, report.Recognised, inbox, result.Unresolved, result.Warnings), true, library, actor, json.Value);
            })
            .WithSummary("Show which business concept the published playbooks recognise in each field and save the result; with useAi, ask AI about the rest and file its answers in the suggestions inbox.");

        group.MapGet("/{id}/detection", (
                string id, ProfileStore profiles, PlaybookStore playbooks, SuggestionStore suggestions, DetectionStore detections, IOptions<HttpJsonOptions> json) =>
            {
                profiles.Get(id);
                if (detections.Find(id) is not { } saved)
                {
                    return Results.NoContent();
                }

                var response = JsonSerializer.Deserialize<DetectResponse>(saved.Json, json.Value.SerializerOptions)!;
                var current = suggestions.List(profileId: id).ToDictionary(s => s.Id);
                var stale = new List<string>();
                if (saved.ProfileChanged)
                {
                    stale.Add("The profile has been saved again since detection ran.");
                }

                var published = Detection.References(playbooks.Library());
                List<string> changes =
                [
                    .. published.Except(saved.Playbooks, StringComparer.Ordinal).Select(r => $"{r} is now published"),
                    .. saved.Playbooks.Except(published, StringComparer.Ordinal).Select(r => $"{r} is no longer published"),
                ];
                if (changes.Count > 0)
                {
                    stale.Add($"The published playbooks have changed since detection ran: {string.Join("; ", changes)}.");
                }

                return Results.Ok(response with
                {
                    Suggestions = [.. response.Suggestions.Select(s => current.GetValueOrDefault(s.Id, s))],
                    UsedAi = saved.UsedAi,
                    DetectedAt = saved.DetectedAt,
                    DetectedBy = saved.DetectedBy,
                    Stale = stale,
                });
            })
            .Produces<DetectResponse>()
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("The profile's latest saved detection result, with the suggestions' current status and whether it may be out of date; 204 when detection has not run.");
    }
}

internal static class Detection
{
    public const string Anonymous = "anonymous";

    public static List<string> References(PlaybookLibrary library) => [.. library.Domains.Select(p => p.Reference).Order(StringComparer.Ordinal)];

    public static DetectResponse Save(
        DetectionStore detections, string id, DetectResponse response, bool usedAi, PlaybookLibrary library, string actor, HttpJsonOptions json)
    {
        var saved = detections.Save(id, JsonSerializer.Serialize(response, json.SerializerOptions), usedAi, References(library), actor);
        return response with { UsedAi = usedAi, DetectedAt = saved.DetectedAt, DetectedBy = saved.DetectedBy };
    }

    /// <summary>Adds the fields AI reads from each document's text; returns its warnings as findings.</summary>
    public static async Task<List<ProfileFinding>> ReadWithAi(ProfileInputSet set, AiAccess ai, PlaybookStore playbooks, CancellationToken cancellationToken)
    {
        var reader = new AiDocumentReader(ai.Require(), AiAccess.Cap(playbooks.Library()));
        var known = set.Contracts.SelectMany(c => c.Fields).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var findings = new List<ProfileFinding>();
        foreach (var document in set.Documents.Where(d => d.Text.Trim().Length > 0 || set.Contracts.All(c => c.Name != d.Name)))
        {
            var result = await reader.ReadAsync(document.Name, document.Sha256, document.Text, set.Format, known, cancellationToken);
            if (result.Contract is { } contract)
            {
                set.Contracts.Add(contract);
                known.UnionWith(contract.Fields.Select(f => f.Path));
            }

            findings.AddRange(result.Warnings.Select(w => new ProfileFinding { Kind = ProfileFindingKind.AiExtracted, Message = w, Inputs = [document.Name] }));
        }

        return findings;
    }

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
