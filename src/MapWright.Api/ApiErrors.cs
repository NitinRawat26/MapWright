using MapWright.Core.Playbooks;
using MapWright.Core.Profile;
using MapWright.Core.Spec;
using MapWright.Store;

namespace MapWright.Api;

/// <summary>Error body for every failed request; <c>issues</c> lists validation errors or failed tests.</summary>
public sealed record ApiProblem(string Title, int Status, string Detail, IReadOnlyList<SpecIssue> Issues);

public static class ApiErrors
{
    public const string UserHeader = "X-MapWright-User";

    /// <summary>The caller's name, recorded on every change; required for writes.</summary>
    public static string Actor(string? user) =>
        string.IsNullOrWhiteSpace(user)
            ? throw new StoreException(StoreError.Invalid, $"Send your name in the {UserHeader} header; it is recorded with the change.")
            : user.Trim();

    public static IApplicationBuilder UseApiErrors(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (StoreException ex)
        {
            var status = ex.Error switch
            {
                StoreError.NotFound => StatusCodes.Status404NotFound,
                StoreError.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            await Write(context, status, ex.Message, ex.Issues);
        }
        catch (Exception ex) when (ex is PlaybookException or ProfileException or MappingSpecException)
        {
            await Write(context, StatusCodes.Status400BadRequest, ex.Message, []);
        }
    });

    private static Task Write(HttpContext context, int status, string detail, IReadOnlyList<SpecIssue> issues)
    {
        var title = status switch
        {
            StatusCodes.Status404NotFound => "Not found",
            StatusCodes.Status409Conflict => "Conflict",
            _ => "Invalid request",
        };
        return Results.Json(new ApiProblem(title, status, detail, issues), statusCode: status).ExecuteAsync(context);
    }
}
