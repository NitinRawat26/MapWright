# Replay and Validation

Replay proves a mapping on real data before anyone builds the integration: it runs source payloads through the
mapping spec exactly as written, writes the target payload for each sample and checks every row and every playbook
validation rule.

```bash
mapwright replay <mapping.json> <sample|dir>... --target <target-profile.json> \
  [--xml-namespace urn:…] [--out dir] [--record] [--strict] [--mask]
# API: POST /api/mappings/{id}/replay  (multipart files, target, xmlNamespace, record, mask)
```

Code: `src/MapWright.Core/Replay/` — `SourceValues`, `TransformEngine`, `TargetWriter`, `ReplayValidator`,
`ReplayMasking`.

## 1. Transform — one row at a time

`TransformEngine` executes each row's `transformation` **exactly as recorded in the spec**; nothing is re-derived
from playbooks at this stage.

| `transformation.type` | What runs | Example from the sample mapping |
| --- | --- | --- |
| `direct`, `rename` | copy, then cast to the target type / scale | `averageTicket → AverageTicket` |
| `typeCast` | date-format conversion, digit reshaping (`pattern`), number/string/boolean casts, decimals rounded to the target's scale | `999-99-9999 → 999999999`; `mcc` string → integer |
| `enumMap` | look the value up in the row's `valueMap` | `SOLE_PROP → SP` |
| `periodConversion`, `unitConversion`, `aggregate`, `derived` … | evaluate `expression` with the variables bound from `transformation.inputs` | `annual / 12` with `annual = $.processing.annualCardVolume` → 3 000 000 → 250 000; `moto + ecomm` → 5 + 25 = 30 |
| `concat` | join inputs with a space | `firstName ⧺ lastName → FullName` |
| `split` | full name split at the last space | |
| `conditional` | first case in `cases[]` whose clauses all hold, else `defaultValue`; values compared exactly or, after trimming, case-insensitively | `entityType in (SOLE_PROP) → SSN; otherwise EIN` |
| `default`, `constant` rows | write `defaultValue` | |

The expression engine is the same one used by playbooks ([Playbooks §13](Playbooks.md#13-the-expression-language)).
A missing input, a division by zero or a type mismatch fails the row rather than producing a value.

**Lists stay aligned:** sources are lined up by list position — `owners[1].firstName` with `owners[1].lastName` —
so each `Officer` gets its own concatenated `FullName`. Single values are shared by every position; a repeating
source into a single target keeps the first value (the row already carries the `dataLoss: high` review note).

## 2. Write — the target payload

`TargetWriter` emits JSON or XML in the **target profile's field order**, building nested objects, JSON arrays,
repeated XML elements, XML attributes (`/@type`) and text nodes (`/text()`). `--xml-namespace` sets the default
namespace (`urn:uwcore:intake:4.2` for UW Core). Fields with no value are omitted.

## 3. Check — per-row results and playbook validations

Every row gets one result per sample:

| Outcome | When |
| --- | --- |
| `pass` | a value was produced and passes the target checks |
| `fail` | transformation error · required target field with no value · wrong type or date format · value outside `allowedValues` · value not matching the digit shape |
| `skipped` | unmapped and optional target, or nothing to map in this sample |

Then `ReplayValidator` runs the **domain playbooks' validation rules** over the produced values, binding each rule
variable to the target field detected as that concept/attribute:

```
VOL-VAL-01  avg <= high                       AverageTicket vs HighTicket       (left out: no HighTicket field in UW Core)
VOL-VAL-02  volume >= 0                       MonthlyVolume
MIX-VAL-01  abs(cp + moto + ecomm - 100) <= 0.5  (left out: UW Core has no Moto/Ecommerce fields)
MIX-VAL-02  abs(cp + cnp - 100) <= 0.5        CardPresentPct + CardNotPresentPct   80 + 20 = 100 ✓
PRN-VAL-01  sum(ownership) <= 100             Officer/OwnershipPct across the list
PRN-VAL-02  sum(ownership) >= 25              (warning severity)
```

Rules whose inputs the target has no field for are left out, so a rule never fails for a field the target does not
carry. Each rule result records the rule id, the values used and pass/fail/warning.

## 4. Runs, recording and exit codes

* Each sample becomes one **validation run** `V001`, `V002`, … with its per-row results and rule results.
* `--record` (API `record=true`) appends the runs to `validationRuns[]` of the mapping file so `render` shows them on
  the Validation tab and the mapping summary counts them.
* `--strict` exits 1 when any check fails — for CI.
* `--out dir` writes the target payloads. The sample repository keeps them in
  `samples/mappings/sales-alpha__uw-core/replay/`.

## 5. Masking

Checks always run on the real values. The written payloads hold the real values **unless** `--mask`, which keeps
only the last four characters of any value that is personal or card data by the row's risk (names, SSN, DOB …), whose
source or target name is sensitive (account number), or whose field the target profile marks sensitive
(`*****3456`, written as text). Business amounts (volumes, tickets, percentages) stay readable. Results shown in
reports and the API mask sensitive values regardless.

## 6. Using replay to verify a rate/volume mapping

```bash
dotnet run --project src/MapWright.Cli -- replay samples/mappings/sales-alpha__uw-core/generated-mapping.json \
  samples/systems/sales-alpha/samples --target samples/systems/uw-core/profile.json \
  --xml-namespace urn:uwcore:intake:4.2 --out out/replay --record --strict
```

For `corp-three-owners.json` (annualCardVolume 3 000 000, averageTicket 42.1, cp 80, moto 0, ecomm 20) the written
XML (`samples/mappings/sales-alpha__uw-core/replay/corp-three-owners.xml`) contains

```xml
<Processing>
  <MonthlyVolume>250000</MonthlyVolume>
  <AverageTicket>42.1</AverageTicket>
  <CardPresentPct>80</CardPresentPct>
  <CardNotPresentPct>20</CardNotPresentPct>
</Processing>
```

and `llc-two-owners.xml` shows the rounding to the target's scale: 500 000 / 12 → `41666.67`, with
`CardNotPresentPct` 5 + 25 → `30`; `sole-prop.xml` has 84 000 / 12 → `7000`.

The run reports `MIX-VAL-02` pass (80 + 20 = 100), `VOL-VAL-02` pass (250 000 ≥ 0), `HighTicket` skipped
(unmapped, optional). If a reviewer had wrongly overridden M017 to a `direct` copy, `MonthlyVolume` would be
3 000 000 — still a "pass" for type and range, which is why the review notes and the twelve-fold risk text on the
row matter as much as the mechanical checks.
