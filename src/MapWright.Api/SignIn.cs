using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MapWright.Api;

/// <summary>The caller as the API sees them. <c>Method</c> is <c>header</c> when sign-in is off, otherwise how they signed in.</summary>
public sealed record SignedInUser(string? Name, string? Method, bool SignInRequired);

public static class SignIn
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string MePath = "/api/me";
    private const string MethodItem = "mapwright.sign-in";

    public static IServiceCollection AddMapWrightSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddAuthentication().AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) => Configure(jwt, auth.Value.Jwt));
        return services;
    }

    /// <summary>
    /// Once sign-in is configured, rejects unsigned <c>/api</c> calls (except <c>/api/me</c>) and replaces any
    /// <c>X-MapWright-User</c> the caller sent with the verified name, so every endpoint records the real caller.
    /// </summary>
    public static IApplicationBuilder UseMapWrightSignIn(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!options.Enabled || !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var (user, method, failure) = await Identify(context, options);
        context.Request.Headers.Remove(ApiErrors.UserHeader);
        if (failure is not null || (user is null && !context.Request.Path.Equals(MePath, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.Headers.WWWAuthenticate = string.Join(", ", Challenges(options));
            await Results.Json(new ApiProblem("Sign-in required", StatusCodes.Status401Unauthorized, failure ?? Instructions(options), []),
                statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }

        if (user is not null)
        {
            context.Request.Headers[ApiErrors.UserHeader] = user;
            context.Items[MethodItem] = method;
        }

        await next(context);
    });

    public static IEndpointRouteBuilder MapSignInEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(MePath, (HttpContext context, IOptions<AuthOptions> options) =>
            {
                var name = context.Request.Headers[ApiErrors.UserHeader].ToString().Trim();
                var method = options.Value.Enabled ? context.Items[MethodItem] as string : "header";
                return new SignedInUser(name.Length > 0 ? name : null, method, options.Value.Enabled);
            })
            .WithTags("Sign-in")
            .WithSummary("Who the API records as the author of your changes, and whether sign-in is required");
        return app;
    }

    private static async Task<(string? User, string? Method, string? Failure)> Identify(HttpContext context, AuthOptions options)
    {
        var headers = context.Request.Headers;
        var authorization = headers.Authorization.ToString();
        var key = headers[ApiKeyHeader].ToString().Trim();
        if (key.Length == 0 && authorization.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
        {
            key = authorization["ApiKey ".Length..].Trim();
        }

        if (key.Length > 0)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var match = options.Keys.FirstOrDefault(k => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(k.Sha256), hash));
            return match is null ? (null, null, "The API key is not valid.") : (match.Name.Trim(), "apiKey", null);
        }

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.Jwt.Enabled)
            {
                return (null, null, "Bearer tokens are not accepted by this API; use an API key.");
            }

            var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (!result.Succeeded)
            {
                return (null, null, "The bearer token was rejected: it is expired, for another audience or issuer, or its signature does not match.");
            }

            var name = options.Jwt.Names.Select(c => result.Principal.FindFirst(c)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return name is null
                ? (null, null, $"The bearer token has none of the claims used for your name ({string.Join(", ", options.Jwt.Names)}).")
                : (name.Trim(), "bearer", null);
        }

        if (options.Proxy.Enabled && headers[options.Proxy.UserHeader!].ToString().Trim() is { Length: > 0 } proxied)
        {
            var sent = Encoding.UTF8.GetBytes(headers[options.Proxy.SecretHeader].ToString());
            return CryptographicOperations.FixedTimeEquals(sent, Encoding.UTF8.GetBytes(options.Proxy.Secret!))
                ? (proxied, "proxy", null)
                : (null, null, $"{options.Proxy.UserHeader} is only accepted from the sign-in proxy.");
        }

        return (null, null, null);
    }

    private static IEnumerable<string> Challenges(AuthOptions options)
    {
        if (options.Keys.Count > 0)
        {
            yield return "ApiKey";
        }

        if (options.Jwt.Enabled)
        {
            yield return "Bearer";
        }
    }

    private static string Instructions(AuthOptions options)
    {
        var ways = new List<string>();
        if (options.Keys.Count > 0)
        {
            ways.Add($"send an API key in the {ApiKeyHeader} header");
        }

        if (options.Jwt.Enabled)
        {
            ways.Add("send a bearer token from your identity provider");
        }

        if (options.Proxy.Enabled)
        {
            ways.Add("open MapWright through the sign-in proxy");
        }

        return $"Sign in first: {string.Join(", or ", ways)}.";
    }

    private static void Configure(JwtBearerOptions jwt, JwtSignIn settings)
    {
        jwt.MapInboundClaims = false;
        jwt.Authority = string.IsNullOrWhiteSpace(settings.Authority) ? null : settings.Authority;
        jwt.Audience = settings.Audience;
        if (!string.IsNullOrWhiteSpace(settings.Issuer))
        {
            jwt.TokenValidationParameters.ValidIssuer = settings.Issuer;
        }

        if (!string.IsNullOrWhiteSpace(settings.SigningKey))
        {
            jwt.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
        }
    }
}
