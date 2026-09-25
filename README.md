# MapWright
Playbook-driven data mapping for merchant acquiring systems

MapWright generates field-level mapping documents between two systems that exchange merchant
application data (e.g. a sales system → an underwriting system, or underwriting → boarding), without
requiring a shared canonical model. Either side may use JSON or XML, in any combination.

## Output model

The **mapping spec (JSON)** is the single source of truth. Every human-facing document is rendered
from it, so documents never drift from what the tool executes.

| Output | Purpose |
|---|---|
| `<id>.xlsx` | Sign-off workbook for BAs: Summary, Mapping, Gaps, Value Maps, Conflicts & Assumptions, Validation, Change Log |
| `<id>.html` | Self-contained, printable report for leadership review and audit |
| `<id>.csv`  | Flat export of the Mapping sheet (multi-value cells joined with ` \| `) |
| `mapping.json` | Machine-readable, versioned mapping spec |

### Mapping sheet columns (one row per target field)

| Group | Columns |
|---|---|
| Identity | Mapping ID, Mapping Type (1:1, Many:1, 1:Many, Constant, Derived, Unmapped) |
| Source / Target | Field Name, Field Path (JSONPath / XPath), Datatype, Format / Length, Required, Cardinality, Allowed Values, Sample Value (masked), Description |
| Semantics | Business Concept, Domain Playbook |
| Transformation | Transformation Type, Rule (human-readable), Expression (machine-executable), Condition, Default Value |
| Confidence | Confidence %, Confidence Band (High / Medium / Low), Reasoning, Evidence Sources |
| Risk | Data Loss Risk, PII / Sensitivity, Target Validation Rules |
| Review | Review Status, Reviewer, Review Date, Reviewer Comments, Open Question |

Confidence bands come from the spec's `confidencePolicy` (default High ≥ 85%, Medium ≥ 60%).

### Spec validation

`mapwright validate` enforces rules JSON parsing alone cannot, including:

- unique mapping IDs and exactly one row per target path
- source count consistent with mapping type (e.g. Many:1 needs ≥ 2 sources, Unmapped needs none)
- `enumMap` transformations carry a value map
- only High-confidence mappings may be `autoAccepted`
- samples of sensitive fields (PII, financial, PCI) reveal at most 4 digits
- findings and validation results reference existing mappings

Unknown JSON properties and missing required properties are rejected.

## System profiles

A system profile is a normalized description of one system's contract, built from any number of its inputs:
JSON or XML sample payloads, JSON Schema, OpenAPI (JSON), XSD, WSDL and field specs (CSV or Excel). All
inputs of one system are merged into one profile. PDF and Word documentation will feed the same profile in a
later release.

Each field records its path, parent, kind (object/value), cardinality, inferred data type and date format,
required signal, length/value ranges, observed values and value shapes, presence counts, the samples it was
seen in, and the source of each attribute.

- **Paths**: JSONPath for JSON (`$.owners[*].ssn`), XPath-style local names for XML
  (`/UnderwritingRequest/Officers/Officer/SSN`, attributes as `/@type`, mixed text as `/text()`).
- **XML**: repeated elements become arrays, a single child of a plural wrapper (`<Officers><Officer/>`) is
  inferred as an array and reported, SOAP envelopes are unwrapped, namespaces are reported, `xsi:nil` is a null
  and DTDs are rejected.
- **Required**: samples can only show *likely required* (present every time) or *optional* (missing, null or
  empty at least once); a contract makes it authoritative (see below).
- **Dates**: ISO dates and times are detected; `MM/dd/yyyy` vs `dd/MM/yyyy` is resolved from the values or
  reported as ambiguous.
- **Sensitive data**: fields whose names match PII/financial terms (SSN, tax id, DOB, account/routing number,
  card number…) or whose values look like identifiers store only a masked sample (last 4 characters), no
  observed values and no numeric ranges. `--no-values` stores no values at all.
- **Findings**: type conflicts, object/value conflicts, mixed or ambiguous date formats, inferred
  cardinality, SOAP and namespace handling.

### Contracts (schemas, service definitions, field specs)

| Input | Recognised by | What is read |
|---|---|---|
| JSON Schema | `$schema`, or `type: object` with `properties` | `properties`, `required`, arrays and `maxItems`, `enum`/`const`, `format` (date, date-time), length, range, `multipleOf` (scale), `description`; local `$ref`, `allOf`; `oneOf`/`anyOf` are merged and their fields made optional |
| OpenAPI 3.x / Swagger 2.0 (JSON) | `openapi` or `swagger` | The JSON request body of one operation, or one named schema, read as JSON Schema |
| XSD | `.xsd`, or an `xs:schema` root | One global element: sequences, `xs:all`, choices (optional), groups, attributes and attribute groups, named and inline types, extensions, `simpleContent` (`/text()`), `minOccurs`/`maxOccurs`, enumerations, length, range and `fractionDigits` facets, `fixed`, annotations |
| WSDL 1.1 / 2.0 | `.wsdl`, or a WSDL root | The input element of one document/literal operation, read from the XSD in `types` |
| Field spec | `.csv`, `.xlsx` | One row per field. Only a path column is required (`Path`, `Field Path`, `XPath`, `JSON Path`, `Field`, `Element`). Also recognised: type (`String(20)`, `Decimal(12,2)`, `Date`…), required/mandatory (`Y`, `M`, `C` = conditional…), format (`YYYY-MM-DD`), min/max length, min/max value, scale, allowed values (`CORP = Corporation; LLC = …`), description, sensitive/PII and repeats. `owners[].ssn` and `/uw:Merchant/uw:Name` style paths are normalized |

- **`--root`** picks the XSD root element, the WSDL operation, or the OpenAPI `operationId`, `"POST /path"` or
  schema name, when a contract has more than one. The profiled part is recorded in the input's notes.
- **Precedence**: schemas (JSON Schema, OpenAPI, XSD, WSDL) over field specs over samples, per attribute.
  Requiredness becomes authoritative (`required`/`optional`). A contract `string` with date samples keeps the
  sample date format. Samples still supply presence, observed values and value shapes.
- **Disagreements are findings, not overwrites**: `typeConflict` (samples look like a number, the contract
  declares a boolean), `contractMismatch` (required but missing in samples, values outside the allowed list,
  values longer than the declared length, a repeating field declared once, a different date format),
  `undeclaredField` (seen in samples, not in any contract), `unresolvedReference` (external `$ref`,
  `xs:import`) and `schemaSimplified` (merged alternatives, recursion, unknown types).
- **Provenance**: every attribute records the input kind and file it came from, and `seenIn` lists samples and
  contracts. A field a contract marks sensitive masks the sample values too.
- XML contracts are read with DTDs rejected and no external resolution; referenced files are not fetched.
- YAML OpenAPI documents are not read yet; convert them to JSON first.

## Playbooks

Playbooks are versioned JSON files that hold the domain knowledge the engine applies. `playbooks/` holds a
starter set.

| Playbook | Knows about |
|---|---|
| `domain/principals` | Owners ≈ Principals; Officers, Signers, Guarantors are *related* (not always owners); ownership as fraction or percent; SSN/DOB sensitivity |
| `domain/processing-volume` | Card volume per period (monthly/annual, ÷12 / ×12), cents vs dollars, average and high ticket |
| `domain/channel-mix` | Card-present, MOTO, e-commerce; `CNP = MOTO + ECOMM`; shares add up to 100 |
| `domain/tax-id` | Business tax ID and its type; SSN for sole proprietors, EIN otherwise; a principal's SSN is not the business tax ID |
| `domain/entity-type` | Legal structure and its codes (`CORP` = `C` = `CORPORATION`, `SOLE_PROP` = `SP`, …) |
| `process/onboard-new-system` | Ingest → profile → match → optional AI assist → validate → review → publish, with gates and thresholds |

A **domain playbook** has a concept and its attributes, vocabulary (each term *equivalent*, *narrower*,
*broader* or *related*), qualifiers (period, unit…), detection signals (name, parent, children, description,
value pattern/shape/range, code list, type, cardinality), derivation rules and conditional rules with worked
examples, value maps, validation rules, confidence settings, risks, review questions, optional AI guidance and
test cases. Derivations and validations use a small arithmetic language (`+ - * /`, comparisons, `sum`,
`count`, `min`, `max`, `abs`, `round`); nothing else can run.

A **process playbook** lists required inputs, ordered steps with gates and reviewers, confidence thresholds and
outputs. An AI step must be optional and capped below the auto-accept threshold, and review must come before
publish.

Detection adds up evidence: a vocabulary term scores by its relation, a parent that names the concept adds
context, and signals add or subtract their weight. Terms that are not equivalent, assumed or conflicting
qualifiers (e.g. named *percent* but valued 0–1) and sensitive attributes can require review, and each match
carries its evidence and the playbook's review questions.

`playbook validate` checks each playbook and the library as a whole (IDs and versions, references to
attributes, qualifiers, value maps and other playbooks, regexes and expressions, duplicate terms or codes,
one published version per ID). `playbook test` also runs every detection test and rule example; a published
domain playbook must have tests.

### Stored playbooks (lifecycle)

`MapWright.Store` keeps playbooks in SQLite with the same JSON shape as the files, one row per version, plus an
audit trail of every change. The files in `playbooks/` can be imported as the starting set.

| From | To | Rule |
|---|---|---|
| (new) | Draft | New playbooks and new versions start as drafts; only one open (draft or in-review) version per ID |
| Draft | Draft | Only drafts are editable |
| Draft | In Review | The playbook is valid and all its tests and rule examples pass |
| In Review | Draft | Changes requested |
| In Review | Published | Valid, tests pass, and published by someone other than the submitter; the previous published version is retired |
| Published / Draft | Retired | No longer applied (a retired draft is abandoned) |

A new version copies an existing one (next minor version by default) and adds a change note. The same store
holds system profiles, mapping specs and reviewers' decisions on mapping rows (approve, reject or override,
also written to the row's review block and the mapping's change log). It is a store for mapping work, not for
merchant data.

## AI assist (optional)

Playbooks always run first. When fields are left unrecognised, `playbook detect` asks
*"Do you want to use AI to decode the remaining N field(s)?"* and calls AI only on **yes** (`--ai yes` skips
the question, `--ai no` never asks). Providers are tried in order and are configured by environment variables;
with none configured MapWright uses playbooks only.

| Provider | Variables |
|---|---|
| Vertex AI (Gemini, first) | `MAPWRIGHT_VERTEX_PROJECT` (enables it), `MAPWRIGHT_VERTEX_LOCATION` (default `global`), `MAPWRIGHT_VERTEX_MODEL` (default `gemini-3.5-flash`); credentials from Application Default Credentials, e.g. `GOOGLE_APPLICATION_CREDENTIALS=/path/to/service-account.json` |
| Ollama (fallback) | `MAPWRIGHT_OLLAMA_URL` (enables it, e.g. `http://localhost:11434`), `MAPWRIGHT_OLLAMA_MODEL` (default `qwen3`) |
| Both | `MAPWRIGHT_AI_TIMEOUT_SECONDS` (default 180) |

What the AI sees and what it can do:
- Only the remaining fields: path, name, parents, children, type, cardinality, description, value shapes, and
  short code-like values (`CORP`, `5411`, `0.5`). Sensitive fields, free text (names, e-mails, phones,
  addresses) and sample values are never sent.
- The domain playbooks' concepts, attributes and `aiGuidance`, so answers use playbook concepts
  (`Principal.Email`) or propose a new one (`new: Merchant.Mcc`) as a candidate for a playbook change.
- Every suggestion is `needsReview`, records the provider and model, and its confidence is capped at the
  process playbook's AI step `maxConfidence` (70), below auto-accept. AI never publishes anything.
- If the AI call fails, detection still succeeds with the playbook results.

`--out <report.json>` writes the playbook matches, AI suggestions and still-remaining fields.

## Generating a mapping

`mapwright map <source-profile.json> <target-profile.json>` pairs two system profiles through the playbooks
and writes a mapping spec with one row per target field. Either side can be JSON or XML.

1. **Concept match:** both fields are recognised as the same business concept with agreeing qualifiers,
   e.g. `$.account.taxId` → `TaxId/Number` (LegalEntity.TaxId). Differences become a rename, a type cast
   (`999-99-9999` → `999999999`) or a code value map (`SOLE_PROP` → `SP`).
2. **Playbook rules:** when no source has the target's concept or qualifiers, a derivation or conditional rule
   builds it: `annual / 12`, `moto + ecomm`, first + last name → full name, SSN/EIN from the entity type.
   Rules that lose data (full name → first name, CNP → ecommerce) carry their risk and always need review.
3. **Name-only fallback:** fields no playbook covers are paired by name (`legalName` ~ `LegalName`,
   `dbaName` ~ `DoingBusinessAs`), capped at 75% and always reviewed.
4. **Gaps and leftovers:** target fields with no source are `unmapped` with a suggested resolution; unused
   source fields are listed as orphans.
5. **Findings:** rule assumptions, list pairings (owners → Officer: "are all officers owners?"), qualifier
   conflicts and profile type conflicts on mapped fields.

A row is auto-accepted only at or above the auto-accept threshold (90%) with no review reason and no data
loss; everything else is `needsReview`. Sensitive sample values stay masked.

If target fields are still unmapped after the playbooks, `map` asks *"Do you want to use AI to decode the
remaining N field(s)?"* (same `--ai ask|yes|no` and providers as `playbook detect`). On yes, the AI sees the
unmapped target fields and all source fields as masked metadata (each marked used or unused) and proposes the
source field(s) and transformation for each target. Its rows are capped at 70%, always `needsReview`, carry
`aiSuggestion` evidence naming the provider and model, and never replace a playbook row. Without `--out` the
question goes to stderr so stdout stays pure JSON.

## Replaying samples

`mapwright replay <mapping.json> <sample|dir>... --target <target-profile.json>` runs source payloads through a
mapping spec, writes the target payload for each sample and checks it. This proves each transformation works on
real data before anyone builds the integration.

1. **Transform:** each row reads its source paths (list items stay aligned, e.g. `owners[1].firstName` with
   `owners[1].lastName`) and runs its transformation: copies, type casts (`999-99-9999` → `999999999`, date
   formats, decimals rounded to the target's scale), value maps, conditions, concatenation and expressions such
   as `annual / 12` with the source fields recorded in the row's `transformation.inputs`.
2. **Write:** values are written as target JSON or XML in the target profile's field order, with nested objects,
   JSON arrays, repeated XML elements and XML attributes. `--xml-namespace` sets the XML namespace.
3. **Check:** every row gets a result: pass, fail (transformation error, required target field with no value,
   wrong type or date format, a value outside the allowed values or the digit shape) or skipped (unmapped and
   optional). The domain playbooks' validation rules then run over the produced values, e.g. `PRN-VAL-01`
   `sum(ownership) <= 100` or `MIX-VAL-02` `abs(cp + cnp - 100) <= 0.5`; rules for concepts the target has no field for are
   left out.

Each sample becomes one validation run (`V001`, `V002`, ...). `--record` adds the runs to the mapping file so
the Validation tab of `render` shows them; `--strict` exits with 1 when any check fails. Sensitive values stay
masked in the results, but the written target payloads contain the real values from the samples.

## Usage

```bash
dotnet run --project src/MapWright.Cli -- validate samples/mappings/sales-alpha__uw-core/mapping.json
dotnet run --project src/MapWright.Cli -- render samples/mappings/sales-alpha__uw-core/mapping.json --out out/
dotnet run --project src/MapWright.Cli -- render <spec.json> --format xlsx,html

dotnet run --project src/MapWright.Cli -- profile samples/systems/sales-alpha/samples \
  --system "SalesAlpha CRM" --version 2026.3 --out samples/systems/sales-alpha/profile.json
dotnet run --project src/MapWright.Cli -- profile a.xml b.xml --system "UW Core" --no-values

# Samples plus contracts, merged into one profile
dotnet run --project src/MapWright.Cli -- profile samples/systems/sales-alpha/samples samples/systems/sales-alpha/contracts \
  --system "SalesAlpha CRM" --root submitApplication --out out/sales-alpha.profile.json
dotnet run --project src/MapWright.Cli -- profile samples/systems/uw-core/samples samples/systems/uw-core/contracts/uw-core-intake.xsd \
  --system "UW Core" --out out/uw-core.profile.json
dotnet run --project src/MapWright.Cli -- profile samples/systems/uw-core/contracts/uw-core-intake.wsdl \
  --system "UW Core" --root SubmitApplication --out out/uw-core.wsdl.profile.json
```

```bash
dotnet run --project src/MapWright.Cli -- playbook validate playbooks
dotnet run --project src/MapWright.Cli -- playbook test playbooks
dotnet run --project src/MapWright.Cli -- playbook detect samples/systems/uw-core/profile.json --playbooks playbooks
dotnet run --project src/MapWright.Cli -- playbook detect samples/systems/sales-alpha/profile.json --ai yes --out out/sales-alpha.decode.json
```

```bash
dotnet run --project src/MapWright.Cli -- map samples/systems/sales-alpha/profile.json samples/systems/uw-core/profile.json \
  --out out/sales-alpha__uw-core.mapping.json
dotnet run --project src/MapWright.Cli -- render out/sales-alpha__uw-core.mapping.json --out out/
dotnet run --project src/MapWright.Cli -- map samples/systems/uw-core/profile.json samples/systems/sales-alpha/profile.json
```

```bash
dotnet run --project src/MapWright.Cli -- replay samples/mappings/sales-alpha__uw-core/generated-mapping.json \
  samples/systems/sales-alpha/samples --target samples/systems/uw-core/profile.json \
  --xml-namespace urn:uwcore:intake:4.2 --out out/replay
dotnet run --project src/MapWright.Cli -- replay out/sales-alpha__uw-core.mapping.json samples/systems/sales-alpha/samples \
  --target samples/systems/uw-core/profile.json --out out/replay --record --strict
```

`profile` accepts files and directories (their `*.json`, `*.xml`, `*.xsd`, `*.wsdl`, `*.csv` and `*.xlsx`
files). JSON and XML files are treated as contracts when they are a schema, OpenAPI or WSDL document, and as
samples otherwise. All inputs of one system must share a format (JSON or XML). Without `--out` the profile is
printed to stdout.

`samples/mappings/sales-alpha__uw-core/mapping.json` is a synthetic example covering Owners → Officers,
annual → monthly volume, CNP = MOTO + ECOMM, SSN/EIN tax-id type, enum value maps, gaps, conflicts and a
validation run. `generated-mapping.json` next to it is what `mapwright map` produces from the two sample
profiles, and `replay/` holds the UW Core XML that `mapwright replay` builds from the SalesAlpha samples. `samples/systems/sales-alpha` (three JSON applications) and `samples/systems/uw-core` (SOAP
and plain XML applications) hold synthetic sample payloads and the profiles generated from them. Their
`contracts/` folders hold matching synthetic contracts: a JSON Schema, an OpenAPI document and a CSV field
spec for SalesAlpha, and an XSD and WSDL for UW Core.

## API

`src/MapWright.Api` is an ASP.NET Core API over the same engine and the SQLite store. Run it with:

```bash
dotnet run --project src/MapWright.Api --urls http://localhost:5080
# Swagger UI: http://localhost:5080/swagger
```

Settings (`appsettings.json`, or environment variables such as `MapWright__DatabasePath`):

| Setting | Default | Meaning |
|---|---|---|
| `MapWright:DatabasePath` | `data/mapwright.db` | SQLite file (`:memory:` for a throw-away store) |
| `MapWright:SeedPlaybooks` | `playbooks` | Imported when the store has no playbooks (relative to the app folder) |
| `MapWright:RequireIndependentReview` | `true` | The submitter of a version cannot publish it |

Writes need an `X-MapWright-User` header; the name is recorded in the audit trail. Errors come back as
`{ title, status, detail, issues }` with 400 (invalid), 404 (not found) or 409 (conflict, e.g. editing a
published version).

### Playbooks

Playbooks are addressed as `/api/playbooks/{kind}/{slug}/{version}`, e.g. `/api/playbooks/domain/tax-id/1.0.0`.
Bodies and responses use the playbook file format.

| Method and path | Does |
|---|---|
| `GET /api/playbooks?status=` | List versions (`draft`, `inReview`, `published`, `retired`) |
| `POST /api/playbooks` | Create a draft |
| `POST /api/playbooks/validate`, `POST /api/playbooks/test` | Validate or test an unsaved playbook |
| `GET /api/playbooks/{kind}/{slug}` | Versions of one playbook |
| `GET /api/playbooks/{kind}/{slug}/history` | Audit trail |
| `GET` / `PUT /api/playbooks/{kind}/{slug}/{version}` | Get a version; replace a draft |
| `POST .../{version}/versions` | Draft a new version: `{ "version": "1.1.0", "note": "…" }` (both optional) |
| `POST .../{version}/status` | Change status: `{ "status": "inReview", "note": "…" }` |
| `GET .../{version}/validate`, `GET .../{version}/test` | Validate or test a stored version |

```bash
curl -X POST localhost:5080/api/playbooks/domain/tax-id/1.0.0/versions -H 'X-MapWright-User: ana' \
  -H 'Content-Type: application/json' -d '{"note":"Add TIN as a term"}'
curl -X POST localhost:5080/api/playbooks/domain/tax-id/1.1.0/status -H 'X-MapWright-User: ana' \
  -H 'Content-Type: application/json' -d '{"status":"inReview"}'
curl -X POST localhost:5080/api/playbooks/domain/tax-id/1.1.0/status -H 'X-MapWright-User: ben' \
  -H 'Content-Type: application/json' -d '{"status":"published"}'
```

### Profiles, mappings and replay

| Method and path | Does |
|---|---|
| `GET /api/profiles` | List profiles |
| `POST /api/profiles` | Build a profile from uploaded files (multipart `files`, plus `system`, and optional `id`, `version`, `description`, `root`, `noValues`, `replace`) |
| `GET` / `PUT` / `DELETE /api/profiles/{id}` | Get, store (profile JSON, e.g. from the CLI) or delete a profile |
| `POST /api/profiles/{id}/detect` | Which business concept the published playbooks recognise in each field |
| `GET /api/mappings` | List mappings |
| `POST /api/mappings` | Generate a mapping: `{ "source": "<profile id>", "target": "<profile id>", "id": "…", "title": "…", "replace": false }` |
| `GET` / `PUT` / `DELETE /api/mappings/{id}` | Get, store (mapping JSON) or delete a mapping |
| `GET /api/mappings/{id}/summary` | Coverage, confidence bands, review status and validation counts |
| `GET /api/mappings/{id}/export/{xlsx\|csv\|html}` | Download the mapping document |
| `POST /api/mappings/{id}/replay` | Replay samples (multipart `files`, plus `target` profile id, optional `xmlNamespace`, `record`) |
| `POST /api/mappings/{id}/rows/{rowId}/review` | `{ "decision": "approve" \| "reject" \| "override", "comment": "…", "row": { … } }` |
| `GET /api/mappings/{id}/reviews` | Review decisions, oldest first |

Generation uses the published playbooks only, so drafts never change a mapping until they are published.
Replay responses contain the built target payloads, which hold the samples' real values; `record=true` also
saves the validation runs in the mapping.

```bash
curl -X POST localhost:5080/api/profiles -H 'X-MapWright-User: ana' \
  -F system="SalesAlpha CRM" -F id=sales-alpha -F root=submitApplication \
  -F files=@samples/systems/sales-alpha/samples/sole-prop.json \
  -F files=@samples/systems/sales-alpha/contracts/sales-alpha-application.schema.json
curl -X PUT localhost:5080/api/profiles/uw-core -H 'X-MapWright-User: ana' \
  -H 'Content-Type: application/json' --data-binary @samples/systems/uw-core/profile.json
curl -X POST localhost:5080/api/mappings -H 'X-MapWright-User: ana' \
  -H 'Content-Type: application/json' -d '{"source":"sales-alpha","target":"uw-core"}'
curl -X POST localhost:5080/api/mappings/sales-alpha__uw-core/replay \
  -F target=uw-core -F files=@samples/systems/sales-alpha/samples/sole-prop.json
curl -o mapping.xlsx localhost:5080/api/mappings/sales-alpha__uw-core/export/xlsx
```

### AI and the suggestions inbox

AI never runs unless a request asks for it with `"useAi": true`, and it only looks at what the playbooks left
over. The provider comes from the same variables as the CLI: `MAPWRIGHT_VERTEX_PROJECT` (Vertex AI, first choice),
`MAPWRIGHT_OLLAMA_URL` (fallback). With neither set, `useAi: true` returns 400 and everything else works with
playbooks only. Answers are capped at the process playbook's AI confidence (70% in the starter set) and always
need review. If the provider fails, the API returns 502 and saves nothing.

| Method and path | Does |
|---|---|
| `GET /api/ai` | Whether a provider is configured, its name and the confidence cap |
| `POST /api/profiles/{id}/detect` with `{ "useAi": true }` | Asks AI about the fields no playbook recognised and files each answer in the inbox |
| `POST /api/mappings` with `"useAi": true` | AI suggests sources for unmapped targets; those rows need review like any other |
| `GET /api/suggestions?status=&profile=` | The inbox (`pending`, `approved`, `rejected`) |
| `POST /api/suggestions/{id}/approve` | `{ "concept": "Concept.Attribute", "comment": "…" }`: adds the field's name as a vocabulary term to a draft of that concept's domain playbook |
| `POST /api/suggestions/{id}/reject` | `{ "comment": "…" }` |

Approving never publishes anything. It opens a draft (the next minor version, or the open draft if there is
one) and the draft goes through test, review and publish as usual. `concept` defaults to the AI's answer; it is
needed when the AI proposed a concept no playbook defines, or when two playbooks share the concept. A term that
is already in the playbook, or a playbook that is in review, returns 409.

## Deployment

### Docker

```bash
docker build -t mapwright-api .
docker run -p 8080:8080 -v mapwright-data:/var/data mapwright-api
# http://localhost:8080/swagger, http://localhost:8080/health
```

The image seeds the starter playbooks into an empty store and keeps the SQLite file at
`/var/data/mapwright.db`, so mount a volume there. Any setting can be overridden with an environment variable,
e.g. `-e MapWright__RequireIndependentReview=false`. To use AI, pass the provider variables
(`MAPWRIGHT_VERTEX_PROJECT`, `MAPWRIGHT_OLLAMA_URL`, ...) and, for Vertex AI, mount the service-account key and
point `GOOGLE_APPLICATION_CREDENTIALS` at it:

```bash
docker run -p 8080:8080 -v mapwright-data:/var/data \
  -v "$PWD/secrets/vertex-key.json:/etc/secrets/vertex-key.json:ro" \
  -e GOOGLE_APPLICATION_CREDENTIALS=/etc/secrets/vertex-key.json \
  -e MAPWRIGHT_VERTEX_PROJECT=<gcp-project> mapwright-api
```

Keep key files out of git: `secrets/` and `*-key.json` are ignored by both `.gitignore` and `.dockerignore`.

### Render

`render.yaml` is a Render Blueprint: a Docker web service with a 1 GB persistent disk at `/var/data` for the
SQLite file and a `/health` check. Persistent disks need a paid instance type, which is why the plan is `starter`.

1. In Render, choose **New > Blueprint** and pick this repository.
2. When asked, fill in `MAPWRIGHT_VERTEX_PROJECT` (and optionally `MAPWRIGHT_VERTEX_LOCATION`, `MAPWRIGHT_VERTEX_MODEL`),
   or `MAPWRIGHT_OLLAMA_URL`, or leave them empty to run with playbooks only.
3. For Vertex AI, add the service-account key under **Environment > Secret Files** with the file name
   `vertex-key.json`. It is available at `/etc/secrets/vertex-key.json`, where `GOOGLE_APPLICATION_CREDENTIALS`
   already points. The account needs the Vertex AI User role.

The API has no sign-in: `X-MapWright-User` is recorded for audit but not verified. Put it behind your own
authentication (a gateway, VPN or Render private service) before exposing it.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build -warnaserror
dotnet test
dotnet format MapWright.slnx --verify-no-changes
```

## Layout

```
src/MapWright.Core     Mapping spec model, system profiles, sample and contract readers, playbook model, validator and matcher,
                       mapping generator, replay (transform engine, JSON/XML target writers, validation)
src/MapWright.Output   Report model, Excel / CSV / HTML renderers and the Excel field-spec reader
src/MapWright.Ai       Optional AI assist: Vertex AI and Ollama providers, fallback, masked field prompts
src/MapWright.Cli      `mapwright` command-line tool
src/MapWright.Api      ASP.NET Core API (Swagger at /swagger) over the engine and the store
src/MapWright.Store    SQLite store: playbook versions and lifecycle, profiles, mappings, review decisions, AI suggestions
tests/MapWright.Tests  Unit and API tests
samples/mappings       Example mapping specs
samples/systems        Example sample payloads, contracts and generated system profiles
playbooks              Starter domain and process playbooks
Dockerfile, render.yaml  API container image and Render Blueprint
```
