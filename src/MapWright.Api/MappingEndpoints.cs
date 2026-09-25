using MapWright.Ai;
using MapWright.Core.Matching;
using MapWright.Core.Profile;
using MapWright.Core.Profile.Samples;
using MapWright.Core.Replay;
using MapWright.Core.Spec;
using MapWright.Output.Renderers;
using MapWright.Output.Report;
using MapWright.Store;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace MapWright.Api;

/// <param name="Source">Source profile id.</param>
/// <param name="Target">Target profile id.</param>
/// <param name="Id">Mapping id; default "&lt;source&gt;__&lt;target&gt;".</param>
/// <param name="Replace">Overwrite an existing mapping with the same id.</param>
/// <param name="UseAi">Consent to ask the configured AI provider about target fields the playbooks left unmapped.</param>
public sealed record GenerateMappingRequest(string Source, string Target, string? Id = null, string? Title = null, bool Replace = false, bool UseAi = false);

/// <param name="Row">The replacement row, for an override. It keeps the row id and target path.</param>
public sealed record ReviewRequest(ReviewDecisionKind Decision, string? Comment = null, FieldMapping? Row = null);

/// <param name="Payload">The target payload built from the sample. It holds the sample's real values.</param>
public sealed record ReplaySample(string Sample, string Payload, ValidationRun Run);

/// <param name="Masked">Sensitive values in the payloads are masked; the checks ran on the real values.</param>
public sealed record ReplayResponse(IReadOnlyList<ReplaySample> Samples, bool Recorded, bool Masked);

/// <summary>Generated mapping specs: generate, review row by row, export and replay samples through them.</summary>
public static class MappingEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapMappingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mappings").WithTags("Mappings");

        group.MapGet("/", (MappingStore store) => store.List())
            .WithSummary("List stored mappings.");

        group.MapPost("/", async (
                GenerateMappingRequest body, MappingStore mappings, ProfileStore profiles, PlaybookStore playbooks, AiAccess ai,
                MapWrightDatabase database, HttpContext context, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                if (string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Target))
                {
                    throw new StoreException(StoreError.Invalid, "Both 'source' and 'target' profile ids are required.");
                }

                var id = body.Id ?? $"{body.Source}__{body.Target}";
                if (!body.Replace && mappings.Find(id) is not null)
                {
                    throw new StoreException(StoreError.Conflict, $"Mapping '{id}' already exists; send replace: true to regenerate it.");
                }

                var source = profiles.Get(body.Source);
                var target = profiles.Get(body.Target);
                var library = playbooks.Library();
                var document = MappingGenerator.Generate(source, target, library, new() { Id = id, Title = body.Title, CreatedAt = Uploads.Now(database) });
                if (body.UseAi && document.Mappings.Any(m => m.Type == MappingType.Unmapped))
                {
                    var paired = await new AiMappingAssistant(ai.Require(), AiAccess.Cap(library))
                        .PairAsync(document, source, target, library.Domains, context.RequestAborted);
                    document = paired.Document;
                }

                mappings.Save(document, actor);
                context.Response.Headers.Location = $"/api/mappings/{id}";
                return Results.Text(MappingSpecSerializer.Serialize(document), "application/json", statusCode: StatusCodes.Status201Created);
            })
            .Produces<MappingDocument>(StatusCodes.Status201Created)
            .WithSummary("Generate a mapping between two stored profiles with the published playbooks; with useAi, AI suggests sources for the unmapped targets (capped, always needing review).");

        group.MapGet("/{id}", (string id, MappingStore store) => Json(store.Get(id)))
            .Produces<MappingDocument>()
            .WithSummary("Get a mapping spec.");

        group.MapPut("/{id}", async (string id, HttpRequest request, MappingStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = ApiErrors.Actor(user);
                using var reader = new StreamReader(request.Body);
                var document = MappingSpecSerializer.Deserialize(await reader.ReadToEndAsync(request.HttpContext.RequestAborted));
                if (document.Id != id)
                {
                    throw new StoreException(StoreError.Invalid, $"The mapping's id '{document.Id}' does not match '{id}'.");
                }

                store.Save(document, actor);
                return Json(document);
            })
            .Accepts<MappingDocument>("application/json")
            .Produces<MappingDocument>()
            .WithSummary("Store a mapping spec JSON (e.g. one generated with the CLI).");

        group.MapDelete("/{id}", (string id, MappingStore store) =>
            {
                store.Get(id);
                store.Delete(id);
                return Results.NoContent();
            })
            .WithSummary("Delete a mapping and its review decisions.");

        group.MapGet("/{id}/summary", (string id, MappingStore store) => MappingSummary.From(store.Get(id)))
            .WithSummary("Coverage, confidence bands, review status and validation counts.");

        group.MapGet("/{id}/export/{format}", (string id, string format, MappingStore store) =>
            {
                var renderer = MappingRenderers.Find(format)
                    ?? throw new StoreException(StoreError.NotFound, $"Format '{format}' not found; use {string.Join(", ", MappingRenderers.All.Select(r => r.Format))}.");
                using var output = new MemoryStream();
                renderer.Render(MappingReport.Build(store.Get(id)), output);
                var contentType = ContentTypes.TryGetContentType(renderer.FileExtension, out var type) ? type : "application/octet-stream";
                return Results.File(output.ToArray(), contentType, id + renderer.FileExtension);
            })
            .WithSummary("Download the mapping document as xlsx, csv or html.");

        group.MapPost("/{id}/replay", async (
                string id,
                IFormFileCollection files,
                [FromForm] string? target,
                [FromForm] string? xmlNamespace,
                [FromForm] bool? record,
                [FromForm] bool? mask,
                MappingStore mappings, ProfileStore profiles, PlaybookStore playbooks, MapWrightDatabase database, HttpContext context,
                [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
            {
                var actor = record == true ? ApiErrors.Actor(user) : null;
                var mapping = mappings.Get(id);
                if (string.IsNullOrWhiteSpace(target))
                {
                    throw new StoreException(StoreError.Invalid, "A 'target' profile id is required.");
                }

                var targetProfile = profiles.Get(target);
                if (targetProfile.Format != mapping.Target.Format)
                {
                    throw new StoreException(StoreError.Invalid, $"Target profile is {targetProfile.Format} but the mapping's target is {mapping.Target.Format}.");
                }

                var library = playbooks.Library();
                var ranAt = Uploads.Now(database);
                var samples = new List<ReplaySample>();
                foreach (var input in await Uploads.Read(files, [".json", ".xml"], context.RequestAborted))
                {
                    var sample = SampleReader.Read(input.Name, Uploads.Text(input));
                    if (sample.Format != mapping.Source.Format)
                    {
                        throw new StoreException(StoreError.Invalid, $"{input.Name}: sample is {sample.Format} but the mapping's source is {mapping.Source.Format}.");
                    }

                    var result = TransformEngine.Run(mapping, sample, targetProfile);
                    var values = mask == true ? ReplayMasking.Mask(result.Values, mapping, targetProfile) : result.Values;
                    var payload = TargetWriter.Write(values, targetProfile, new() { XmlNamespace = xmlNamespace });
                    var run = ReplayValidator.Validate(mapping, result, targetProfile, library, ReplayValidator.NextRunId(mapping, samples.Count), ranAt);
                    samples.Add(new(input.Name, payload, run));
                }

                if (actor is not null)
                {
                    mappings.Save(mapping with { ValidationRuns = [.. mapping.ValidationRuns, .. samples.Select(s => s.Run)] }, actor);
                }

                return new ReplayResponse(samples, actor is not null, mask == true);
            })
            .DisableAntiforgery()
            .WithSummary("Run source samples through the mapping, build the target payloads and check them; record=true saves the runs; mask=true masks sensitive values in the payloads.");

        group.MapPost("/{id}/rows/{rowId}/review", (
                string id, string rowId, ReviewRequest body, MappingStore store, [FromHeader(Name = ApiErrors.UserHeader)] string? user) =>
                store.Decide(id, rowId, body.Decision, ApiErrors.Actor(user), body.Comment, body.Row))
            .WithSummary("Approve, reject or override one mapping row.");

        group.MapGet("/{id}/reviews", (string id, MappingStore store) =>
            {
                store.Get(id);
                return store.Decisions(id);
            })
            .WithSummary("Every review decision on a mapping, oldest first.");
    }

    private static IResult Json(MappingDocument document) =>
        Results.Text(MappingSpecSerializer.Serialize(document), "application/json");
}
