# System Profiles

A **system profile** is the normalised description of one system's contract — the input to detection and mapping.
`mapwright profile <files|dirs>... --system "<name>" [--version v] [--root r] [--no-values] [--out profile.json]`
(or `POST /api/profiles`) builds one from any number of inputs of the same format (JSON or XML).

Code: `src/MapWright.Core/Profile/`.

## Inputs

| Input | Recognised by | What is read |
| --- | --- | --- |
| Sample payloads | `.json` / `.xml` that are not a schema | Paths, kinds, types, cardinality, presence, observed values, value shapes, ranges, date formats |
| JSON Schema | `$schema`, or `type: object` + `properties` | `properties`, `required`, arrays/`maxItems`, `enum`/`const`, `format`, length, range, `multipleOf`, `description`, local/external `$ref`, `allOf`; `oneOf`/`anyOf` merged (fields optional) |
| OpenAPI 3.x / Swagger 2.0 (JSON or YAML) | `openapi` / `swagger` | Request body of one operation or one named schema (`--root`) |
| XSD | `.xsd` / `xs:schema` | One global element: sequences, `xs:all`, choices, groups, attributes, named/inline types, extensions, `simpleContent` (`/text()`), occurs, enumerations, facets, `fixed`, annotations; `xs:import`/`include`/`redefine`/`override` from co-uploaded files |
| WSDL 1.1 / 2.0 | `.wsdl` | Input of one operation (`--root`) via its XSD; RPC style becomes a wrapper element per operation |
| Field spec | `.csv` / `.xlsx` | One row per field; only a path column is required. Type (`String(20)`, `Decimal(12,2)`), required (`Y`/`M`/`C`), format, lengths, min/max, scale, allowed values (`CORP = Corporation; LLC = …`), description, sensitive/PII, repeats |
| Data dictionary | `.csv` / `.xlsx` | Field-spec reader plus dictionary column names (`Attribute Name`, `Parent`, `Mandatory Y/N`, `Nullable`, `Domain Values`, `PII Flag`, …); multi-sheet workbooks; sheet name as parent |
| PDF / Word (`.docx`) | | Field tables read by rules (header row with a path column); heading becomes parent for plain names. Text outside tables only when AI is asked (`useAi`) — masked, each AI-added field gets an `aiExtracted` finding |

Precedence per attribute: schemas > field specs > samples. Disagreements are **findings, not overwrites**
(`typeConflict`, `contractMismatch`, `undeclaredField`, `unresolvedReference`, `schemaSimplified`, …).

## What a field records

```
path            $.processing.annualCardVolume   /UnderwritingRequest/Processing/MonthlyVolume   (JSONPath / XPath local names)
name            annualCardVolume                 MonthlyVolume
parent          $.processing                     /UnderwritingRequest/Processing
kind            value | object
dataType        string | integer | decimal | boolean | date | dateTime | object | unknown
format          yyyy-MM-dd, MM/dd/yyyy …          (dates; ambiguous formats reported)
cardinality     single | array (arrays, repeated XML elements, plural wrappers)
required        required | optional (contract) · likelyRequired | optional (samples only)
length / min / max / scale
allowedValues   from enum / field spec
observedValues  distinct sample values (short code-like only for sensitive fields: none)
valueShapes     999-99-9999, 99-9999999, …
seenIn          which samples / contracts contributed
sensitive       PII / financial by name pattern or by value shape or by contract flag
description     from schema / spec / dictionary
provenance      which input supplied each attribute
```

The `FieldContext` handed to the playbook matcher is built from exactly these attributes — see
[Playbooks §8](Playbooks.md#8-how-detection-scores-a-field-the-matcher). So the quality of detection depends
directly on the profile: descriptions, enums and numeric ranges all feed the score and the qualifiers
(`annualCardVolume` with values 84 000–3 000 000 → `period=annual`; `cardPresentPercent` 70–90 → `unit=percent`).

## Sensitive data

Fields whose names match PII/financial terms (SSN, tax id, DOB, account/routing/card number …), whose values look
like identifiers, or which a contract flags sensitive keep **only a masked sample** (`*****3456`), no observed values
and no ranges. `--no-values` stores no values for any field. Masking is preserved downstream: mapping rows show
masked samples, AI never receives the values, replay `--mask` masks the written payloads.

## XML specifics

Repeated elements become arrays; a single child of a plural wrapper (`<Officers><Officer/>`) is inferred as an array
with a finding; SOAP envelopes are unwrapped; namespaces are recorded; `xsi:nil` is null; attributes are `/@name`;
mixed text is `/text()`; DTDs are rejected.

## Findings

Type/kind conflicts, mixed or ambiguous date formats, inferred cardinality, SOAP/namespace handling, contract
mismatches, undeclared fields, unresolved references, simplified schemas, camelCased dictionary names, duplicate
rows. Findings on fields a mapping uses are copied into the mapping's `findings[]` as `conflict`s.

## Detection on a profile

`mapwright playbook detect <profile.json> [--playbooks dir] [--ai ask|yes|no] [--out report.json]` or
`POST /api/profiles/{id}/detect` runs the domain playbooks over every field and reports concept, attribute,
qualifiers, score, evidence and review questions — the same detections the mapping generator uses. Useful to check a
new playbook term or a new system before generating a mapping.

## Samples in the repository

| Path | System | Format | Contents |
| --- | --- | --- | --- |
| `samples/systems/sales-alpha` | SalesAlpha CRM | JSON | 3 applications, JSON Schema, OpenAPI (JSON + YAML), CSV field spec, split contracts, `profile.json` |
| `samples/systems/uw-core` | UW Core | XML | SOAP + plain XML applications, XSD, WSDL (document and RPC), `profile.json` |
| `samples/systems/sales-beta` | | | PDF and Word specifications + sample |
| `samples/systems/sales-gamma` | | | Excel (3 sheets) and CSV data dictionary + sample |

The [worked example](Worked-Example-Volume-Mapping.md) maps `sales-alpha` → `uw-core`.
