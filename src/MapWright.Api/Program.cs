using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MapWright.Api;
using MapWright.Core.Playbooks;
using MapWright.Store;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
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
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapPlaybookEndpoints();
app.MapProfileEndpoints();
app.MapMappingEndpoints();

app.Run();

static void SeedPlaybooks(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
    var store = app.Services.GetRequiredService<PlaybookStore>();
    if (string.IsNullOrWhiteSpace(options.SeedPlaybooks) || store.List().Count > 0)
    {
        return;
    }

    var directory = Path.IsPathRooted(options.SeedPlaybooks) ? options.SeedPlaybooks : Path.Combine(AppContext.BaseDirectory, options.SeedPlaybooks);
    if (!Directory.Exists(directory))
    {
        app.Logger.LogWarning("Seed playbook directory {Directory} not found; starting with no playbooks.", directory);
        return;
    }

    var imported = store.Import(PlaybookLibrary.Load([directory]).All, "seed");
    app.Logger.LogInformation("Imported {Count} playbook(s) from {Directory}.", imported.Count, directory);
}
