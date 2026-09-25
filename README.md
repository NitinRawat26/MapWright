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

A system profile is a normalized description of one system's contract, built from any number of its inputs.
This release builds profiles from JSON or XML sample payloads; schemas, field specs, metadata exports and
documentation will feed the same profile in later releases.

Each field records its path, parent, kind (object/value), cardinality, inferred data type and date format,
required signal, length/value ranges, observed values and value shapes, presence counts, the samples it was
seen in, and the source of each attribute.

- **Paths**: JSONPath for JSON (`$.owners[*].ssn`), XPath-style local names for XML
  (`/UnderwritingRequest/Officers/Officer/SSN`, attributes as `/@type`, mixed text as `/text()`).
- **XML**: repeated elements become arrays, a single child of a plural wrapper (`<Officers><Officer/>`) is
  inferred as an array and reported, SOAP envelopes are unwrapped, namespaces are reported, `xsi:nil` is a null
  and DTDs are rejected.
- **Required**: samples can only show *likely required* (present every time) or *optional* (missing, null or
  empty at least once); the formal schema inputs will make this authoritative.
- **Dates**: ISO dates and times are detected; `MM/dd/yyyy` vs `dd/MM/yyyy` is resolved from the values or
  reported as ambiguous.
- **Sensitive data**: fields whose names match PII/financial terms (SSN, tax id, DOB, account/routing number,
  card number…) or whose values look like identifiers store only a masked sample (last 4 characters), no
  observed values and no numeric ranges. `--no-values` stores no values at all.
- **Findings**: type conflicts, object/value conflicts, mixed or ambiguous date formats, inferred
  cardinality, SOAP and namespace handling.

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

`profile` accepts files and directories (their `*.json` and `*.xml` files). All samples of one system must
share a format. Without `--out` the profile is printed to stdout.

`samples/mappings/sales-alpha__uw-core/mapping.json` is a synthetic example covering Owners → Officers,
annual → monthly volume, CNP = MOTO + ECOMM, SSN/EIN tax-id type, enum value maps, gaps, conflicts and a
validation run. `generated-mapping.json` next to it is what `mapwright map` produces from the two sample
profiles, and `replay/` holds the UW Core XML that `mapwright replay` builds from the SalesAlpha samples. `samples/systems/sales-alpha` (three JSON applications) and `samples/systems/uw-core` (SOAP
and plain XML applications) hold synthetic sample payloads and the profiles generated from them.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build -warnaserror
dotnet test
dotnet format MapWright.slnx --verify-no-changes
```

## Layout

```
src/MapWright.Core     Mapping spec model, system profiles and sample readers, playbook model, validator and matcher,
                       mapping generator, replay (transform engine, JSON/XML target writers, validation)
src/MapWright.Output   Report model and Excel / CSV / HTML renderers
src/MapWright.Ai       Optional AI assist: Vertex AI and Ollama providers, fallback, masked field prompts
src/MapWright.Cli      `mapwright` command-line tool
tests/MapWright.Tests  Unit tests
samples/mappings       Example mapping specs
samples/systems        Example sample payloads and generated system profiles
playbooks              Starter domain and process playbooks
```
