using Microsoft.Extensions.Options;

namespace MapWright.Api;

/// <summary>
/// Who may call the API. With nothing configured the API trusts the <c>X-MapWright-User</c> header (local use);
/// once any sign-in method is configured every <c>/api</c> request must be signed in and the audit name is the
/// verified identity.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "MapWright:Auth";

    /// <summary>Keys for scripts and services, stored as the SHA-256 of the key, never the key itself.</summary>
    public List<ApiKeySignIn> ApiKeys { get; set; } = [];

    /// <summary>OAuth 2.0 / OpenID Connect bearer tokens (JWT) from your identity provider.</summary>
    public JwtSignIn Jwt { get; set; } = new();

    /// <summary>A sign-in proxy in front of the API (SSO for the web UI) that passes the user in a header.</summary>
    public ProxySignIn Proxy { get; set; } = new();

    /// <summary>Configured keys; entries left completely blank (e.g. empty deployment variables) are ignored.</summary>
    public IReadOnlyList<ApiKeySignIn> Keys => [.. ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k.Name) || !string.IsNullOrWhiteSpace(k.Sha256))];

    public bool Enabled => Keys.Count > 0 || Jwt.Enabled || Proxy.Enabled;
}

public sealed class ApiKeySignIn
{
    /// <summary>Recorded as the author of every change made with the key.</summary>
    public string Name { get; set; } = "";

    /// <summary>Hex SHA-256 of the key, e.g. from <c>printf %s "$KEY" | sha256sum</c>.</summary>
    public string Sha256 { get; set; } = "";
}

public sealed class JwtSignIn
{
    /// <summary>OpenID Connect issuer URL; its signing keys are read from the discovery document.</summary>
    public string? Authority { get; set; }

    /// <summary>Required: the audience the tokens must be issued for.</summary>
    public string? Audience { get; set; }

    /// <summary>Expected issuer; required with <see cref="SigningKey"/>.</summary>
    public string? Issuer { get; set; }

    /// <summary>Shared HMAC secret (at least 32 bytes) for issuers without a discovery document.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Claims tried in order for the audit name.</summary>
    public string[]? NameClaims { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) || !string.IsNullOrWhiteSpace(SigningKey);

    public IReadOnlyList<string> Names => NameClaims is { Length: > 0 } ? NameClaims : ["preferred_username", "email", "upn", "name", "sub"];
}

public sealed class ProxySignIn
{
    /// <summary>Header the proxy sets to the signed-in user, e.g. <c>X-Forwarded-Email</c>.</summary>
    public string? UserHeader { get; set; }

    /// <summary>Shared secret the proxy sends in <see cref="SecretHeader"/>, so callers cannot set the user header themselves.</summary>
    public string? Secret { get; set; }

    public string SecretHeader { get; set; } = "X-MapWright-Proxy-Secret";

    public bool Enabled => !string.IsNullOrWhiteSpace(UserHeader);
}

internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var problems = new List<string>();
        for (var i = 0; i < options.ApiKeys.Count; i++)
        {
            var key = options.ApiKeys[i];
            if (!options.Keys.Contains(key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(key.Name))
            {
                problems.Add($"{AuthOptions.Section}:ApiKeys:{i}:Name is required; it is recorded as the author of changes.");
            }

            if (key.Sha256.Length != 64 || !key.Sha256.All(Uri.IsHexDigit))
            {
                problems.Add($"{AuthOptions.Section}:ApiKeys:{i}:Sha256 must be the 64-character hex SHA-256 of the key.");
            }
        }

        var jwt = options.Jwt;
        if (jwt.Enabled && string.IsNullOrWhiteSpace(jwt.Audience))
        {
            problems.Add($"{AuthOptions.Section}:Jwt:Audience is required.");
        }

        if (!string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            if (string.IsNullOrWhiteSpace(jwt.Issuer))
            {
                problems.Add($"{AuthOptions.Section}:Jwt:Issuer is required with a SigningKey.");
            }

            if (System.Text.Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
            {
                problems.Add($"{AuthOptions.Section}:Jwt:SigningKey must be at least 32 bytes.");
            }
        }

        if (options.Proxy.Enabled && (options.Proxy.Secret?.Length ?? 0) < 16)
        {
            problems.Add($"{AuthOptions.Section}:Proxy:Secret (at least 16 characters) is required, so only the proxy can set {options.Proxy.UserHeader}.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
