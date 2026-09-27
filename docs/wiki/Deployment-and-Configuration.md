# Deployment and Configuration

## Build and test

Requires the .NET 10 SDK; the web UI needs Node.js 22+.

```bash
dotnet build -warnaserror
dotnet test
dotnet format MapWright.slnx --verify-no-changes
cd web && npm ci && npm run build && npm test
```

## Settings

`appsettings.json`, or environment variables with `__` separators (`MapWright__DatabasePath`).

| Setting | Default | Meaning |
| --- | --- | --- |
| `MapWright:DatabasePath` | `data/mapwright.db` | SQLite file; `:memory:` for a throw-away store |
| `MapWright:SeedPlaybooks` | `playbooks` | Folder imported when the store has no playbooks (relative to the app) |
| `MapWright:RequireIndependentReview` | `true` | The submitter of a playbook version cannot publish it |
| `MapWright:Auth:…` | off | Sign-in methods (below) |
| `MAPWRIGHT_VERTEX_PROJECT`, `MAPWRIGHT_OLLAMA_URL`, … | unset | AI providers — see [AI Assist](AI-Assist.md#providers-and-configuration) |

## Sign-in

Off until at least one method is configured under `MapWright:Auth`. Once on, every `/api` request must be signed in
(`/health`, `/swagger`, UI files stay open), `X-MapWright-User` from callers is ignored and the verified name is
recorded. Methods combine.

| Method | Settings | Caller sends | Recorded name |
| --- | --- | --- | --- |
| API keys (scripts, CI) | `ApiKeys:0:Name`, `ApiKeys:0:Sha256` (hex SHA-256 of the key; `:1:`, `:2:` … for more) | `X-Api-Key: <key>` or `Authorization: ApiKey <key>` | the key's `Name` |
| OAuth / OIDC bearer tokens | `Jwt:Authority` + `Jwt:Audience` (keys from discovery); or `Jwt:SigningKey` (HMAC ≥ 32 bytes) + `Jwt:Issuer`; `Jwt:NameClaims` overrides name claims | `Authorization: Bearer <token>` | first of `preferred_username`, `email`, `upn`, `name`, `sub` |
| SSO via sign-in proxy (web UI) | `Proxy:UserHeader` (e.g. `X-Forwarded-Email`), `Proxy:Secret` (≥ 16 chars), `Proxy:SecretHeader` (default `X-MapWright-Proxy-Secret`) | nothing — the proxy adds both headers | the user header |

Tokens are checked for signature, issuer, audience, expiry; API keys are stored as hashes and compared in constant
time; a half-configured method stops start-up. `GET /api/me` reports the caller's identity.

```bash
KEY=$(openssl rand -hex 32); echo "key: $KEY"
printf %s "$KEY" | sha256sum
MapWright__Auth__ApiKeys__0__Name=ci-bot MapWright__Auth__ApiKeys__0__Sha256=<hash> \
  dotnet run --project src/MapWright.Api --urls http://localhost:5080
curl localhost:5080/api/me -H "X-Api-Key: $KEY"
```

## Docker

```bash
docker build -t mapwright-api .
docker run -p 8080:8080 -v mapwright-data:/var/data mapwright-api
# UI http://localhost:8080/   Swagger /swagger   Health /health
```

The image builds the UI (Node 24) and the API (.NET 10); the API serves the UI at `/`, seeds the starter playbooks
into an empty store and keeps SQLite at `/var/data/mapwright.db` — mount a volume there. Override any setting with
`-e MapWright__…`. For Vertex AI mount the key and point `GOOGLE_APPLICATION_CREDENTIALS` at it:

```bash
docker run -p 8080:8080 -v mapwright-data:/var/data \
  -v "$PWD/secrets/vertex-key.json:/etc/secrets/vertex-key.json:ro" \
  -e GOOGLE_APPLICATION_CREDENTIALS=/etc/secrets/vertex-key.json \
  -e MAPWRIGHT_VERTEX_PROJECT=<gcp-project> mapwright-api
```

`secrets/` and `*-key.json` are ignored by `.gitignore` and `.dockerignore`.

## Render

`render.yaml` is a Render Blueprint: a Docker web service on the free plan with a `/health` check. The free plan has no
persistent disk, so the database resets on every deploy/restart (playbooks re-seeded; profiles, mappings, drafts and
suggestions lost) and the service sleeps when idle. For persistence use a paid plan and a disk:

```yaml
    plan: starter
    disk:
      name: mapwright-data
      mountPath: /var/data
      sizeGB: 1
```

1. **New > Blueprint**, pick the repository.
2. Fill in `MAPWRIGHT_VERTEX_PROJECT` (optionally location/model) or `MAPWRIGHT_OLLAMA_URL`, or leave empty for
   playbooks only.
3. For Vertex AI add the service-account key as Secret File `vertex-key.json` (available at
   `/etc/secrets/vertex-key.json`, where `GOOGLE_APPLICATION_CREDENTIALS` already points; needs the Vertex AI User
   role).

Turn on sign-in before exposing the service; keep key hashes, signing keys and the proxy secret as secret values.

## Repository layout

```
src/MapWright.Core     spec model, profiles and readers, playbooks (model, validator, matcher, tests, expressions),
                       mapping generator, replay
src/MapWright.Output   report model, Excel / CSV / HTML / PDF renderers, Excel field-spec reader
src/MapWright.Ai       Vertex AI and Ollama providers, fallback, masked prompts
src/MapWright.Cli      mapwright command line
src/MapWright.Api      ASP.NET Core API (+ Swagger), serves the UI
src/MapWright.Store    SQLite store: playbook versions/lifecycle, profiles, mappings, reviews, AI suggestions
web                    Angular UI
tests/MapWright.Tests  unit and API tests
playbooks              starter domain and process playbooks
samples                sample systems, contracts, profiles, mappings, replay output
docs/wiki              this wiki
Dockerfile, render.yaml
```
