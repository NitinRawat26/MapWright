using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MapWright.Api;
using MapWright.Core.Playbooks;
using MapWright.Store;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.AddMapWrightSignIn(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<ApiOptions>>().Value;
    return new MapWrightDatabase(
        new() { DatabasePath = options.DatabasePath, RequireIndependentReview = options.RequireIndependentReview },
        sp.GetService<TimeProvider>());
});
builder.Services.AddSingleton<PlaybookStore>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<MappingStore>();
builder.Services.AddSingleton<SuggestionStore>();
builder.Services.AddSingleton<DetectionStore>();
builder.Services.AddSingleton(sp => new AiAccess(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title = "MapWright API",
    Version = "v1",
    Description = "Playbooks, system profiles and mapping specs for integrating any two systems.",
}));

var app = builder.Build();

SeedPlaybooks(app);

app.UseApiErrors();
app.UseMapWrightSignIn();
app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapSignInEndpoints();
app.MapPlaybookEndpoints();
app.MapProfileEndpoints();
app.MapMappingEndpoints();
app.MapSuggestionEndpoints();
if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
{
    app.MapFallbackToFile("{*path:regex(^(?!api/|swagger/|health$).*$)}", "index.html");
}

app.Run();

static void SeedPlaybooks(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
    var store = app.Services.GetRequiredService<PlaybookStore>();
    if (string.IsNullOrWhiteSpace(options.SeedPlaybooks))
    {
        return;
    }

    var directory = Path.IsPathRooted(options.SeedPlaybooks) ? options.SeedPlaybooks : Path.Combine(AppContext.BaseDirectory, options.SeedPlaybooks);
    var empty = store.List().Count == 0;
    if (!Directory.Exists(directory))
    {
        if (empty)
        {
            app.Logger.LogWarning("Seed playbook directory {Directory} not found; starting with no playbooks.", directory);
        }

        return;
    }

    if (!empty)
    {
        var restored = store.RestoreYaml(PlaybookLibrary.Read([directory]));
        if (restored.Count > 0)
        {
            app.Logger.LogInformation("Restored the YAML and comments of {Count} playbook version(s) from {Directory}.", restored.Count, directory);
        }

        return;
    }

    var imported = store.Import(PlaybookLibrary.Read([directory]), "seed");
    app.Logger.LogInformation("Imported {Count} playbook(s) from {Directory}.", imported.Count, directory);
}
