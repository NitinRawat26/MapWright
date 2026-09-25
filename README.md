# MapWright
Playbook-driven data mapping for merchant acquiring systems

MapWright generates field-level mapping documents between two systems that exchange merchant
application data (e.g. a CRM-based sales system sending JSON → an XML underwriting intake), without
requiring a shared canonical model.

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

## Usage

```bash
dotnet run --project src/MapWright.Cli -- validate samples/mappings/sales-alpha__uw-core/mapping.json
dotnet run --project src/MapWright.Cli -- render samples/mappings/sales-alpha__uw-core/mapping.json --out out/
dotnet run --project src/MapWright.Cli -- render <spec.json> --format xlsx,html
```

`samples/mappings/sales-alpha__uw-core/mapping.json` is a synthetic example covering Owners → Officers,
annual → monthly volume, CNP = MOTO + ECOMM, SSN/EIN tax-id type, enum value maps, gaps, conflicts and a
validation run.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build -warnaserror
dotnet test
```

## Layout

```
src/MapWright.Core     Mapping spec model, JSON serializer, validator, summary
src/MapWright.Output   Report model and Excel / CSV / HTML renderers
src/MapWright.Cli      `mapwright` command-line tool
tests/MapWright.Tests  Unit tests
samples/mappings       Example mapping specs
```
