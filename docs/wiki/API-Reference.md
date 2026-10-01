# API Reference

`src/MapWright.Api` is an ASP.NET Core API over the same engine plus the SQLite store (`src/MapWright.Store`). It
adds what files cannot: playbook lifecycle with independent review, audit history, stored profiles/detections/
mappings, row-level review decisions and the AI suggestions inbox. Swagger UI documents every endpoint.

```bash
dotnet run --project src/MapWright.Api --urls http://localhost:5080
# Swagger: http://localhost:5080/swagger   Health: /health   UI (once built): /
```

## Conventions

* Writes need a caller name for the audit trail: header `X-MapWright-User` without sign-in, or the verified identity
  with [sign-in](Deployment-and-Configuration.md#sign-in) enabled.
* Errors: `{ title, status, detail, issues }` with `400` invalid · `401` not signed in · `404` not found ·
  `409` conflict (editing a published version, publishing your own submission, duplicate term …) · `502` AI provider
  failure.
* Playbook bodies are YAML or JSON (`Content-Type: application/yaml` or `application/json`; JSON detected when the
  body starts with `{`). Responses are JSON unless `?format=yaml` / `Accept: application/yaml`, which returns the
  stored YAML with comments.
* `GET /api/me` → `{ name, method, signInRequired }`.

## Playbooks

Addressed as `/api/playbooks/{kind}/{slug}/{version}`, e.g. `/api/playbooks/domain/processing-volume/1.0.0`.

| Method and path | Does |
| --- | --- |
| `GET /api/playbooks?status=` | List versions (`draft`, `inReview`, `published`, `retired`, `abandoned`) |
| `POST /api/playbooks` | Create a draft from a playbook body |
| `POST /api/playbooks/validate`, `POST /api/playbooks/test` | Validate / test an unsaved body |
| `GET /api/playbooks/{kind}/{slug}` | Versions of one playbook |
| `GET …/{slug}/history` | Audit trail |
| `GET` / `PUT …/{slug}/{version}` | Get a version; replace a draft |
| `POST …/{version}/versions` | Draft a new version `{ "version": "1.1.0", "note": "…" }` |
| `POST …/{version}/status` | `{ "status": "inReview" \| "published" \| "retired" \| "abandoned" \| "draft", "note": "…" }` |
| `DELETE …/{version}?note=` | Delete a never-submitted draft (409 otherwise — abandon it) |
| `GET …/{version}/validate`, `GET …/{version}/test` | Validate / test a stored version |

Allowed transitions: `draft → inReview` (submitted) · `inReview → draft` (changes requested) · `inReview → published`
· `published → retired` · `draft → abandoned`. Publishing a version retires the previously published one
("Replaced by 1.1.0"). With `MapWright:RequireIndependentReview=true` (default) the submitter cannot publish their own
version. Generation uses **published** playbooks only.

```bash
curl -X POST localhost:5080/api/playbooks/domain/processing-volume/1.0.0/versions \
     -H 'X-MapWright-User: ana' -H 'Content-Type: application/json' -d '{"note":"Add daily period"}'
curl -X PUT  localhost:5080/api/playbooks/domain/processing-volume/1.1.0 \
     -H 'X-MapWright-User: ana' -H 'Content-Type: application/yaml' --data-binary @processing-volume.yaml
curl        localhost:5080/api/playbooks/domain/processing-volume/1.1.0/test
curl -X POST localhost:5080/api/playbooks/domain/processing-volume/1.1.0/status \
     -H 'X-MapWright-User: ana' -H 'Content-Type: application/json' -d '{"status":"inReview"}'
curl -X POST localhost:5080/api/playbooks/domain/processing-volume/1.1.0/status \
     -H 'X-MapWright-User: ben' -H 'Content-Type: application/json' -d '{"status":"published"}'
```

## Profiles and detection

| Method and path | Does |
| --- | --- |
| `GET /api/profiles` | List |
| `POST /api/profiles` | Build from uploads: multipart `files`, `system`, optional `id`, `version`, `description`, `root`, `noValues`, `replace`, `useAi` (PDF/Word text) |
| `GET` / `PUT` / `DELETE /api/profiles/{id}` | Get, store (profile JSON from the CLI), delete |
| `POST /api/profiles/{id}/detect` | Run the published playbooks over the profile; `{ "useAi": true }` also asks AI about the rest and files answers in the inbox. Saved with `detectedAt`/`detectedBy` |
| `GET /api/profiles/{id}/detection` | Latest saved detection (204 if none), AI suggestion statuses, `stale` reasons if the profile or published playbooks changed since |

## Mappings, review and replay

| Method and path | Does |
| --- | --- |
| `GET /api/mappings` | List |
| `POST /api/mappings` | Generate: `{ "source": "<profile id>", "target": "<profile id>", "id", "title", "replace": false, "useAi": false }`. With `useAi`, the stored `aiPass` gives provider, cap, `suggestedRows`, `unmatched`, `warnings` |
| `GET` / `PUT` / `DELETE /api/mappings/{id}` | Get, store (mapping JSON), delete |
| `GET /api/mappings/{id}/summary` | Coverage, confidence bands, review status and validation counts |
| `GET /api/mappings/{id}/export/{xlsx\|csv\|html\|pdf}` | Download a rendered document |
| `POST /api/mappings/{id}/replay` | multipart `files`, `target` profile id, optional `xmlNamespace`, `record`, `mask` |
| `POST /api/mappings/{id}/ask-ai` | `{ "rowIds": ["M001", …], "source"?, "target"? }` — asks the AI for an alternative source on those rows and stores it as `aiSuggestion` on each answered row; optional `source`/`target` profile ids override the lookup by system name |
| `POST /api/mappings/{id}/rows/{rowId}/review` | `{ "decision": "approve" \| "reject" \| "override", "comment": "…", "row": { … } }` — `override` replaces the row with the supplied one |
| `GET /api/mappings/{id}/reviews` | Review decisions, oldest first |

```bash
curl -X PUT  localhost:5080/api/profiles/sales-alpha -H 'X-MapWright-User: ana' \
     -H 'Content-Type: application/json' --data-binary @samples/systems/sales-alpha/profile.json
curl -X PUT  localhost:5080/api/profiles/uw-core -H 'X-MapWright-User: ana' \
     -H 'Content-Type: application/json' --data-binary @samples/systems/uw-core/profile.json
curl -X POST localhost:5080/api/mappings -H 'X-MapWright-User: ana' \
     -H 'Content-Type: application/json' -d '{"source":"sales-alpha","target":"uw-core"}'
curl -X POST localhost:5080/api/mappings/sales-alpha__uw-core/rows/M017/review -H 'X-MapWright-User: ben' \
     -H 'Content-Type: application/json' -d '{"decision":"approve","comment":"annual/12 confirmed with UW"}'
curl -X POST localhost:5080/api/mappings/sales-alpha__uw-core/replay \
     -F target=uw-core -F files=@samples/systems/sales-alpha/samples/corp-three-owners.json -F record=true
curl -o mapping.xlsx localhost:5080/api/mappings/sales-alpha__uw-core/export/xlsx
```

## AI and the suggestions inbox

| Method and path | Does |
| --- | --- |
| `GET /api/ai` | Provider configured?, name, confidence cap |
| `GET /api/suggestions?status=&profile=` | Inbox (`pending`, `approved`, `rejected`) |
| `POST /api/suggestions/{id}/approve` | `{ "concept": "Concept.Attribute", "comment": "…", "create": false }` → vocabulary term added to a draft playbook (see [AI Assist](AI-Assist.md#the-suggestions-inbox--playbook-drafts)) |
| `POST /api/suggestions/{id}/reject` | `{ "comment": "…" }` |

Without a provider configured `useAi: true` returns 400. Provider failure returns 502 and stores nothing.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `MapWright:DatabasePath` | `data/mapwright.db` | SQLite file (`:memory:` for a throw-away store) |
| `MapWright:SeedPlaybooks` | `playbooks` | Imported when the store has no playbooks |
| `MapWright:RequireIndependentReview` | `true` | Submitter cannot publish their own version |
| `MapWright:Auth:*` | off | See [Deployment and Configuration](Deployment-and-Configuration.md#sign-in) |

Environment form: `MapWright__DatabasePath`, `MapWright__Auth__ApiKeys__0__Sha256`, ….
