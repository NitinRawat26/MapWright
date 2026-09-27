# Worked Example: Volume ("rate") Mapping

This page traces, number by number, how the `domain/processing-volume` and `domain/channel-mix` playbooks turn the
processing section of the SalesAlpha CRM (JSON) into the `Processing` block of UW Core (XML). Every value below is
taken from `samples/mappings/sales-alpha__uw-core/generated-mapping.json`, which you can regenerate with:

```bash
dotnet run --project src/MapWright.Cli -- map \
  samples/systems/sales-alpha/profile.json samples/systems/uw-core/profile.json --ai no --out /tmp/mapping.json
```

Background: [Playbooks](Playbooks.md) (what each section means) and [Mapping Generation](Mapping-Generation.md)
(the strategy order).

## The two sides

Source profile (`$.processing`, JSON, 3 sample payloads):

| Path | Type | Observed | Min–Max |
| --- | --- | --- | --- |
| `$.processing.annualCardVolume` | integer | 3000000, 500000, 84000 | 84 000 – 3 000 000 |
| `$.processing.averageTicket` | decimal | 42.1, 12.5, 140 | 12.5 – 140 |
| `$.processing.cardPresentPercent` | integer | 80, 70, 90 | 70 – 90 |
| `$.processing.motoPercent` | integer | 0, 5, 10 | 0 – 10 |
| `$.processing.ecommPercent` | integer | 20, 25, 0 | 0 – 25 |

Target profile (`/UnderwritingRequest/Processing`, XML, 2 sample payloads):

| Path | Type | Min–Max |
| --- | --- | --- |
| `…/Processing/MonthlyVolume` | decimal | 7 000 – 250 000 |
| `…/Processing/AverageTicket` | decimal | 42.1 – 140 |
| `…/Processing/HighTicket` | decimal | 1 500 |
| `…/Processing/CardPresentPct` | integer | 80 – 90 |
| `…/Processing/CardNotPresentPct` | integer | 10 – 20 |

Note that the two systems disagree on the **period** (annual vs monthly) and on **how the channel mix is split**
(three shares vs two). No field name is identical. The playbooks resolve both.

## Step 1 – Detection (both sides, every value field)

`PlaybookLibrary.Detect` scores each field against all five active domain playbooks. Only the winning playbook's
result is kept. The processing fields all land in `processing-volume` or `channel-mix`:

| Field | Concept | Qualifiers | Score | Evidence (abridged) |
| --- | --- | --- | --- | --- |
| `$.processing.annualCardVolume` | `ProcessingVolume.CardVolume` | `period=annual` | 95 | term `CardVolume` +60 · ancestor `processing` +25 · `VOL-SIG-NUM-VOL` +10 · period from name |
| `$.processing.averageTicket` | `ProcessingVolume.AverageTicket` | – | 95 | term `AverageTicket` +60 · ancestor +25 · `VOL-SIG-NUM-AVG` +10 |
| `$.processing.cardPresentPercent` | `ChannelMix.CardPresent` | `unit=percent` | 95 | term `CardPresent` +60 · ancestor +25 · range signal +10 · unit from name |
| `$.processing.motoPercent` | `ChannelMix.Moto` | `unit=percent` | 95 | term `Moto` +60 · +25 · `MIX-SIG-RANGE-MOTO` +10 |
| `$.processing.ecommPercent` | `ChannelMix.Ecommerce` | `unit=percent` | 95 | term `ecomm` +60 · +25 · `MIX-SIG-RANGE-ECOMMERCE` +10 |
| `…/MonthlyVolume` | `ProcessingVolume.CardVolume` | `period=monthly` | 95 | term `volume` +60 · ancestor `Processing` +25 · numeric +10 · period from name |
| `…/AverageTicket` | `ProcessingVolume.AverageTicket` | – | 95 | |
| `…/HighTicket` | `ProcessingVolume.HighTicket` | – | 95 | term `HighTicket` +60 · +25 · `VOL-SIG-NUM-HIGH` +10 |
| `…/CardPresentPct` | `ChannelMix.CardPresent` | `unit=percent` | 95 | `pct` is a `percent` qualifier term |
| `…/CardNotPresentPct` | `ChannelMix.CardNotPresent` | `unit=percent` | 95 | |

Why 95 and not 100: no field carries a `currencyUnit` word, so that qualifier stays unset (no default → no
assumption, no review). The `period` qualifier *does* have a default (`monthly`), but every volume field here names
its period, so the default is never used and no `assumedQualifier` trigger fires.

## Step 2 – Mapping row by row (one row per target field)

The generator tries, for each target: **ByConcept → ByDerivation → ByCondition → ByName → Unmapped**.

### `MonthlyVolume` ← `annualCardVolume` (row M017): a period conversion

1. **ByConcept**: a source with `ProcessingVolume.CardVolume` exists (`annualCardVolume`) but qualifiers disagree
   (`period=annual` vs `period=monthly`) → `QualifiersAgree` is false → no direct pair.
2. **ByDerivation**: rules of `processing-volume` whose output is `CardVolume` with `period=monthly`: `VOL-PERIOD-01`
   (`annual / 12`). Its single input `annual` needs `CardVolume [period=annual]` → `annualCardVolume` (score 95) fits.
   `VOL-PERIOD-02` (`monthly * 12`) outputs `period=annual`, so it is not a candidate; `VOL-UNIT-01` outputs
   `currencyUnit=major`, which the target does not state, so it is *compatible* but needs a `currencyUnit=minor`
   source, which does not exist → not applicable.
3. Resulting row:

```json
{
  "id": "M017",
  "type": "oneToOne",
  "sources": [{ "path": "$.processing.annualCardVolume", "dataType": "integer", "sampleValue": "***0000" }],
  "target":   { "path": "/UnderwritingRequest/Processing/MonthlyVolume", "dataType": "decimal", "sampleValue": "****00.00" },
  "businessConcept": "ProcessingVolume.CardVolume",
  "domainPlaybook": "domain/processing-volume@1.0.0",
  "transformation": {
    "type": "periodConversion",
    "rule": "Annual volume to monthly volume. (VOL-PERIOD-01: annual = $.processing.annualCardVolume)",
    "expression": "annual / 12",
    "inputs": { "annual": "$.processing.annualCardVolume" }
  },
  "confidencePercent": 95,
  "reasoning": "Target is ProcessingVolume.CardVolume [period=monthly]; domain/processing-volume@1.0.0 derives it with rule VOL-PERIOD-01 from ProcessingVolume.CardVolume [period=annual]. Assumption: Assumes volume is spread evenly; seasonal merchants may differ.",
  "evidence": [
    { "kind": "playbook", "reference": "domain/processing-volume@1.0.0", "detail": "Derivation VOL-PERIOD-01: annual / 12" },
    { "kind": "playbook", "detail": "Source $.processing.annualCardVolume: Name matches equivalent term 'CardVolume' (+60). Ancestor 'processing' names the ProcessingVolume concept (+25). Signal VOL-SIG-NUM-VOL (numeric) (+10). period=annual from the name." },
    { "kind": "sample",   "reference": "corp-three-owners.json, llc-two-owners.json, sole-prop.json", "detail": "Source $.processing.annualCardVolume observed" },
    { "kind": "playbook", "detail": "Target /UnderwritingRequest/Processing/MonthlyVolume: Name matches equivalent term 'volume' (+60). Ancestor 'Processing' names the ProcessingVolume concept (+25). Signal VOL-SIG-NUM-VOL (numeric) (+10). period=monthly from the name." }
  ],
  "risk": { "dataLoss": "none", "sensitivity": "financial",
            "targetValidationRules": ["Likely required (present in every sample)", "VOL-VAL-02: Volume cannot be negative."] },
  "review": { "status": "autoAccepted" }
}
```

How each value was produced:

| Field | Source of the value |
| --- | --- |
| `type: oneToOne` | one input and transformation ≠ `derived` |
| `transformation.type` | the rule's `transformation: periodConversion` |
| `expression` / `inputs` | rule expression; input variable → chosen source path. Replay evaluates `annual / 12` with `annual` read from that path |
| `confidencePercent: 95` | `min(source score 95, target score 95)`; no `requiresReview`, no `dataLoss` → no deductions |
| `reasoning … Assumption:` | the rule's `note`; also emitted as a mapping-level **finding** of kind `assumption` |
| `sampleValue: "***0000"` | attribute sensitivity `financial` → samples masked |
| `targetValidationRules` | validation rules of the playbook that take this attribute as input (`VOL-VAL-02`) |
| `review.status: autoAccepted` | 95 ≥ auto-accept 90 (process `autoAcceptAt`) and no review items |

Replay then produces `3000000 / 12 = 250000`, `500000 / 12 ≈ 41666.67`, `84000 / 12 = 7000` — matching the
target's observed 7 000 – 250 000 range. The rule's own examples (`3000000 → 250000`, `84000 → 7000`) were checked
when the playbook was published.

**If the systems were the other way round** (source monthly, target annual) the same playbook selects
`VOL-PERIOD-02` and writes `expression: "monthly * 12"`; nothing else changes.

**If the source were in cents** (`annualCardVolumeCents`, `currencyUnit=minor` from the name) and the target in
dollars (`…VolumeUsd`), there is no single rule that does both conversions; the target row would fall to
`unmapped` with "Recognised as ProcessingVolume.CardVolume … but no source field provides it". The fix is a
playbook change: add a rule with both qualifier changes (`annualCents / 12 / 100`) or profile an intermediate
field. This is by design — the engine never chains rules it was not given.

### `AverageTicket` ← `averageTicket` (row M018): a direct pair

Both sides are `ProcessingVolume.AverageTicket` with no qualifiers → **ByConcept** succeeds.
`Transform`: no value map, same format, decimal → decimal, compacted names equal (`averageticket`) → `direct` /
"Copy". Confidence `min(95, 95) = 95` → `autoAccepted`. Reasoning: "Both fields are recognised as
ProcessingVolume.AverageTicket by domain/processing-volume@1.0.0."

### `HighTicket` (row M019): recognised but unmapped

Target detection is `ProcessingVolume.HighTicket` (95), but no source field is `HighTicket`, no derivation outputs
it, no conditional infers it, and ByName (which may pair a recognised target with an *unrecognised* source) finds no
source name scoring ≥ 50 against `HighTicket`. Result:

```json
{ "id": "M019", "type": "unmapped", "businessConcept": "ProcessingVolume.HighTicket",
  "confidencePercent": 0,
  "reasoning": "Recognised as ProcessingVolume.HighTicket by domain/processing-volume@1.0.0, but no source field provides it.",
  "risk": { "targetValidationRules": ["VOL-VAL-01: Average ticket cannot be above high ticket."] },
  "review": { "status": "needsReview" },
  "suggestedResolution": "Ask the source team for ProcessingVolume.HighTicket, or agree a default." }
```

Had the field appeared in more than one target sample with a single repeated value, and were the field not flagged
sensitive by the profile, the resolution would add "Every target sample holds '1500'; if it is fixed for this source
system, map it as a constant." Here it was seen in one sample only.

### `CardPresentPct` ← `cardPresentPercent` (row M020): a rename

Same concept, same `unit=percent` → direct. Compacted names differ (`cardpresentpercent` vs `cardpresentpct`) →
`rename`, "Copy cardPresentPercent to CardPresentPct". 95, auto-accepted.

### `CardNotPresentPct` ← `motoPercent` + `ecommPercent` (row M021): an aggregate

1. ByConcept: no source is `ChannelMix.CardNotPresent`.
2. ByDerivation in `channel-mix`: two rules output `CardNotPresent [unit=percent]`:
   * `MIX-CNP-01` `moto + ecomm` — inputs `Moto` and `Ecommerce` with `unit=percent`: both found (95 each).
   * `MIX-CNP-02` `100 - cp` — input `CardPresent [unit=percent]`: found.
   Neither requires review or loses data, so the first in file order wins and the other is reported as a cross-check.
3. Row:

```json
{ "id": "M021", "type": "manyToOne",
  "sources": [{ "path": "$.processing.motoPercent" }, { "path": "$.processing.ecommPercent" }],
  "target": { "path": "/UnderwritingRequest/Processing/CardNotPresentPct" },
  "businessConcept": "ChannelMix.CardNotPresent", "domainPlaybook": "domain/channel-mix@1.0.0",
  "transformation": { "type": "aggregate",
    "rule": "Card-not-present is MOTO plus e-commerce. (MIX-CNP-01: moto = $.processing.motoPercent, ecomm = $.processing.ecommPercent)",
    "expression": "moto + ecomm",
    "inputs": { "moto": "$.processing.motoPercent", "ecomm": "$.processing.ecommPercent" } },
  "confidencePercent": 95,
  "reasoning": "Target is ChannelMix.CardNotPresent [unit=percent]; domain/channel-mix@1.0.0 derives it with rule MIX-CNP-01 from ChannelMix.Moto [unit=percent] and ChannelMix.Ecommerce [unit=percent]. Rule(s) MIX-CNP-02 (100 - cp) also apply and can cross-check the result.",
  "risk": { "targetValidationRules": ["MIX-VAL-02: Card-present and card-not-present add up to 100%."] },
  "review": { "status": "autoAccepted" } }
```

`type` is `manyToOne` because the rule has two inputs. At replay, `MIX-VAL-02`
(`abs(cp + cnp - 100) <= 0.5`) checks the generated payload; with the samples, `80 + 20`, `70 + 30`, `90 + 10` all pass.

## Step 3 – What a reviewer sees for this block

| Row | Target | Type | Transformation | Conf. | Status | Why |
| --- | --- | --- | --- | --- | --- | --- |
| M017 | MonthlyVolume | oneToOne | periodConversion `annual / 12` | 95 | autoAccepted | derivation, assumption noted as a finding |
| M018 | AverageTicket | oneToOne | direct | 95 | autoAccepted | same concept |
| M019 | HighTicket | unmapped | – | 0 | needsReview | no source; ask or default |
| M020 | CardPresentPct | oneToOne | rename | 95 | autoAccepted | same concept + qualifier |
| M021 | CardNotPresentPct | manyToOne | aggregate `moto + ecomm` | 95 | autoAccepted | derivation with cross-check |

Findings for the whole mapping include
`assumption: /UnderwritingRequest/Processing/MonthlyVolume uses rule VOL-PERIOD-01: Assumes volume is spread evenly; seasonal merchants may differ.`

## Step 4 – Variations you will meet, and what the playbook does

| Situation | Detection | Mapping outcome |
| --- | --- | --- |
| Source `cardVolume` with no period word | `period=monthly` **assumed**, trigger `assumedQualifier`, question "Which period does this volume cover?" | Pairs directly with `MonthlyVolume`, but `needsReview` with "Source match needs review (AssumedQualifier)." and the question |
| Source `grossSalesYearly` under `financials` | `CardVolume [period=annual]` via *related* term → `nonEquivalentTerm` | Derivation `VOL-PERIOD-01` still applies; row reviewed with "Does this figure include cash or other non-card sales?" |
| Source `volumeTier` = `LOW/HIGH` | values are not numbers → `VOL-SIG-TIER-VOL` −30 → score 30 → **not** recognised | Falls to ByName (if a target is unrecognised too) or is listed as an orphan |
| Source `annualCardVolume` = `"280,000"` (text) | type `string`: no `VOL-SIG-NUM-VOL` bonus, but the values are numeric text so no penalty → 85, `period=annual` | `VOL-PERIOD-01` runs (replay accepts thousands separators → `23333.33`); row is reviewed, not auto-accepted |
| Source `ownershipPercent` holding 0.2–1 | name says percent, values say fraction → `unit=fraction`, `qualifierConflict` | `PRN-PCT-01` `fraction * 100` feeds the percent target; a `conflict` finding explains why |
| Target `AnnualVolume`, source `monthlyVolume` | both `CardVolume`, periods differ | `VOL-PERIOD-02` `monthly * 12` |
| Target `VolumeUsd`, source `volumeCents` | `currencyUnit` major vs minor | `VOL-UNIT-01` `cents / 100` |
| Source decimal volume, target integer | – | direct pair, `typeCast`, −5 confidence, `dataLoss: medium` → review "Data loss risk is medium." |

## Step 5 – Adding your own "rate" knowledge

To make the engine handle a new variant (say a *daily* volume, or a *rate* expressed in basis points), you only
touch the playbook:

```yaml
qualifiers:
  - name: period
    values:
      - value: daily
        terms: [daily, day, per day]
      # … existing monthly / annual
derivations:
  - id: VOL-PERIOD-03
    description: Daily volume to monthly volume (30-day month).
    output: { attribute: CardVolume, qualifiers: { period: monthly } }
    inputs: [{ name: daily, attribute: CardVolume, qualifiers: { period: daily } }]
    expression: daily * 30
    transformation: periodConversion
    requiresReview: true            # the 30-day assumption should be confirmed
    note: Uses a 30-day month.
    examples: [{ inputs: { daily: 1000 }, expected: 30000 }]
tests:
  - id: VOL-T-08
    field: { name: dailyCardVolume, ancestors: [processing], dataType: decimal }
    expect: ProcessingVolume.CardVolume
    expectQualifiers: { period: daily }
```

Then `mapwright playbook test playbooks`, bump the version with a change note, publish, and re-run `mapwright map`.
The new row will show `expression: "daily * 30"`, confidence lowered by 10 for `requiresReview`, and the review item
"Rule VOL-PERIOD-03 rests on an assumption." No code changes are needed.
