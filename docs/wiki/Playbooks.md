# Playbooks

A playbook is a versioned YAML (or JSON) file that captures what a business analyst knows about one slice of
merchant-acquiring data. Playbooks are the **only** source of business knowledge in MapWright: the engine has no
hard-coded notion of "volume" or "tax id"; everything it recognises, derives, converts and questions comes from a
published playbook. AI is optional and runs after the playbooks (see [AI Assist](AI-Assist.md)).

This page explains every section of a playbook, exactly how the engine consumes it, and how to author and test one.
For how the results are combined into a mapping spec, see [Mapping Generation](Mapping-Generation.md); for a full
numeric trace of the processing-volume playbook, see [Worked Example: Volume Mapping](Worked-Example-Volume-Mapping.md).

Source of truth in code: `src/MapWright.Core/Playbooks/` (`Playbook.cs` model, `PlaybookMatcher.cs` detection,
`Expressions.cs` expression language, `PlaybookValidator.cs` rules `PB0xx`, `PlaybookTestRunner.cs`).

---

## Contents

1. [Two kinds of playbook](#1-two-kinds-of-playbook)
2. [File header and lifecycle](#2-file-header-and-lifecycle)
3. [Domain playbook anatomy](#3-domain-playbook-anatomy)
4. [Concept and attributes](#4-concept-and-attributes)
5. [Vocabulary](#5-vocabulary)
6. [Qualifiers](#6-qualifiers)
7. [Detection signals](#7-detection-signals)
8. [How detection scores a field (the matcher)](#8-how-detection-scores-a-field-the-matcher)
9. [Derivation rules](#9-derivation-rules)
10. [Conditional rules](#10-conditional-rules)
11. [Value maps](#11-value-maps)
12. [Validation rules](#12-validation-rules)
13. [The expression language](#13-the-expression-language)
14. [Confidence, risks, review guidance, AI guidance](#14-confidence-risks-review-guidance-ai-guidance)
15. [Tests](#15-tests)
16. [Validation of the playbook itself (`PB0xx` codes)](#16-validation-of-the-playbook-itself-pb0xx-codes)
17. [Process playbooks](#17-process-playbooks)
18. [How the playbooks drive a mapping (summary)](#18-how-the-playbooks-drive-a-mapping-summary)
19. [Authoring checklist](#19-authoring-checklist)
20. [Where playbooks live and how they are loaded](#20-where-playbooks-live-and-how-they-are-loaded)

---

## 1. Two kinds of playbook

| Kind | Id prefix | Answers | Example |
| --- | --- | --- | --- |
| **Domain** | `domain/` | *What is this field, and how does it relate to other fields?* One business concept, its names, units/periods, arithmetic between variants, allowed codes, sanity checks. | `domain/processing-volume`, `domain/owners`, `domain/tax-id`, `domain/entity-type`, `domain/channel-mix` |
| **Process** | `process/` | *How do we run and gate a mapping exercise?* Steps, which domain playbooks to use, whether AI may run, gates, reviewer roles, thresholds, output formats. | `process/onboard-new-system` |

A file has exactly one of a `domain:` or `process:` section, matching its `kind` (rule `PB005`).

> Codes such as `PB005` are the playbook validator's issue codes: each one is a structural check that
> `mapwright playbook validate` / `playbook test` and every save through the API or UI runs on the YAML.
> They are listed in [section 16](#16-validation-of-the-playbook-itself-pb0xx-codes).

## 2. File header and lifecycle

```yaml
specVersion: "1.0"                  # must equal Playbook.CurrentSpecVersion (PB001)
id: domain/processing-volume        # '<kind>/<lower-case-slug>' (PB002)
name: Processing Volume             # required (PB004)
kind: domain                        # domain | process
version: "1.0.0"                    # MAJOR.MINOR.PATCH (PB003); quote it so YAML keeps it a string
status: published                   # draft | inReview | published | retired | abandoned
owner: Underwriting BA team
description: 'Expected card sales: total volume per period, average ticket and high ticket.'
changeNotes:
  - version: "1.0.0"                # a published version needs a change note (PB006)
    date: "2026-09-25"
    author: MapWright starter set
    description: Initial version.
```

`Reference` is `id@version`, e.g. `domain/processing-volume@1.0.0`; it is what mapping rows cite as their source of
knowledge.

### Lifecycle

```
draft ──► inReview ──► published ──► retired
  │                        ▲
  └──► abandoned           └── a new draft of the same id is edited and published in its place
```

* Only **one** version per id may be `published` (`PB041`), and a version can't be defined twice (`PB040`).
* A published domain playbook must have at least one detection test (`PB021`) and all its tests and rule examples must
  pass before the API/UI will publish it.
* When a library is loaded, `PlaybookLibrary.Active` picks, per id, the published version, otherwise the newest
  draft/in-review version. Retired and abandoned versions are never used for detection.
* In the UI a **draft** is edited as a form (see [Web UI](Web-UI.md)); on disk you edit the YAML and check it with
  `mapwright playbook test playbooks`.

## 3. Domain playbook anatomy

```yaml
domain:
  concept:        # the one business concept this playbook owns, with its attributes
  vocabulary:     # names systems use for the concept / attributes, with relation strength
  qualifiers:     # dimensions that change meaning: period, currencyUnit, unit (percent/fraction)
  signals:        # extra evidence with weights: data type, regexes, value ranges, value-map membership
  derivations:    # arithmetic between variants:  monthly = annual / 12
  conditions:     # inferred codes:  TaxIdType = SSN when EntityType in (SOLE_PROPRIETORSHIP) else EIN
  valueMaps:      # canonical codes + aliases:  SOLE_PROPRIETORSHIP ← SP, SOLE_PROP, "Sole Proprietor"
  validations:    # sanity checks run at replay:  avg <= high
  confidence:     # points and thresholds (all optional; defaults shown below)
  risks:          # text warnings attached to every match
  reviewGuidance: # questions to ask reviewers, optionally only when a trigger fires
  aiGuidance:     # one paragraph handed to the AI when it is asked about unmatched fields
  tests:          # fields the playbook must / must not recognise
```

Each section is described below with the exact engine behaviour.

## 4. Concept and attributes

```yaml
concept:
  name: ProcessingVolume            # PascalCase (PB010)
  description: How much the merchant expects to process.
  attributes:                       # at least one (PB010); names unique + PascalCase
    - name: CardVolume
      description: Total card sales for a period.
      dataType: decimal             # string | integer | decimal | boolean | date | dateTime | object
      sensitivity: financial        # none | pii | sensitivePii | financial | pci  → non-none adds a review trigger
    - name: AverageTicket
      dataType: decimal
      sensitivity: financial
    - name: HighTicket
      dataType: decimal
      sensitivity: financial
```

* A **business concept** string is `Concept.Attribute` (`ProcessingVolume.CardVolume`) for a value field, or just
  `Concept` (`Principal`) for a group/list field such as `$.owners`.
* `requiresContext: true` on an attribute means a field can only match it when an *ancestor* names the concept.
  Use it for generic attribute names (`Title`, `Number`) that would otherwise match everywhere.
* Two playbooks may not define the same `Concept.Attribute` (`PB046`).
* Sensitivity flows into masking: sample values of sensitive attributes are masked in specs, never sent to AI, and a
  sensitive match always carries the `sensitiveAttribute` review trigger.

## 5. Vocabulary

```yaml
vocabulary:
  - term: processing volume                 # no appliesTo → names the concept itself
  - term: processing
    relation: related
  - term: card volume
    appliesTo: CardVolume                   # names one attribute
  - term: volume
    appliesTo: CardVolume
  - term: gross sales
    appliesTo: CardVolume
    relation: related
    note: May include cash and non-card sales.
  - term: average ticket
    appliesTo: AverageTicket
```

* The concept name and every attribute name are **implicit equivalent terms**; you only list extra spellings.
* `relation` is `equivalent` (default), `narrower`, `broader` or `related`. It decides the points awarded (see §8) and
  whether the match is flagged: anything other than `equivalent` raises the `nonEquivalentTerm` trigger, which by
  default **requires review**. So `grossSalesYearly` is recognised as `CardVolume` but a reviewer is asked
  "Does this figure include cash or other non-card sales?".
* Matching is token-based, not substring: terms and field names are split into lower-case words (`annualCardVolume` →
  `annual`, `card`, `volume`; `avg_txn` → `avg`, `txn`) and lightly stemmed (`officers` ~ `officer`). A term matches when
  **all** its tokens appear in the field's tokens. `card volume` matches `annualCardVolume`; `volume` matches too,
  but the multi-token term wins ties because more tokens matched.
* A term may be equivalent for only one target (`PB012`) so a name can't be an exact synonym of two attributes.

## 6. Qualifiers

Qualifiers are the dimensions that change the *meaning* of a value without changing the concept. They are the key to
rate/volume mapping: `annualCardVolume` and `MonthlyVolume` are both `ProcessingVolume.CardVolume`, but with different
`period`.

```yaml
qualifiers:
  - name: period
    description: The period a volume covers.
    appliesTo: [CardVolume]           # omitted → applies to every attribute
    default: monthly                  # assumed when nothing in the field says otherwise
    values:
      - value: monthly
        terms: [monthly, month, mthly, per month, mnthly]
      - value: annual
        terms: [annual, annually, yearly, year, yr, per annum, pa]
  - name: currencyUnit
    values:
      - value: major
        terms: [dollars, usd, dollar]
      - value: minor
        terms: [cents, minor units, pennies]
```

The owners playbook shows the numeric form:

```yaml
  - name: unit
    appliesTo: [OwnershipPercent]
    values:
      - value: fraction
        terms: [fraction, ratio, decimal share]
        minValue: 0
        maxValue: 1
      - value: percent
        terms: [percent, pct, percentage, perc]
        minValue: 0
        maxValue: 100
```

### Resolution order (`PlaybookMatcher.ResolveQualifier`)

For each qualifier that applies to the matched attribute:

1. **By name** – the first value whose `terms` match the field's name tokens *plus* its description tokens.
2. **By observed values** – if the profile recorded `minValue`/`maxValue` for the field, the first value whose
   `[minValue, maxValue]` range contains them.
3. If both are found and **disagree**: the *observed values win*, a warning "Name says unit 'percent' but observed
   values 0.2–1 look like 'fraction'" is recorded, and the `qualifierConflict` trigger fires (review).
4. If only one is found, it is used (evidence: "`period=annual` from the name.").
5. Otherwise, if the qualifier has a `default`, it is assumed and the `assumedQualifier` trigger fires (review). The
   volume playbook pairs this with a review question: "The name does not say monthly or annual. Which period does this
   volume cover?".
6. Otherwise the qualifier is left unset.

### Why qualifiers matter downstream

* Two fields are a **direct** pair only when every qualifier they *both* have agrees (`monthly` = `monthly`). A qualifier
  known on only one side still pairs, but adds the review item "`period=monthly` is only known for the target field."
* A qualifier mismatch (`annual` vs `monthly`) blocks the direct pair and makes the generator look for a
  **derivation** whose output has the target's qualifiers and whose inputs have the source's (§9).

## 7. Detection signals

Signals add (or subtract) weighted evidence beyond the name.

```yaml
signals:
  - id: VOL-SIG-NUM-VOL
    kind: dataType
    appliesTo: CardVolume
    pattern: integer|decimal
    weight: 10
    note: numeric
  - id: VOL-SIG-TEXT-VOL
    kind: dataType
    appliesTo: CardVolume
    pattern: date|dateTime|boolean
    weight: -30
    note: not a number
  - id: VOL-SIG-TIER-VOL
    kind: valuePattern
    appliesTo: CardVolume
    pattern: ^(?!-?(\d+|\d{1,3}(,\d{3})+)(\.\d+)?$)
    weight: -30
    note: text that is not a number, e.g. a volume tier
```

`dataType` signals see the **profiled** type, not the raw text. A JSON number `280000` profiles as `integer`
(+10 via `VOL-SIG-NUM-VOL`); a JSON string `"280000"` or `"280,000"` keeps its declared type `string`
(untyped XML text is inferred from content, so `<Volume>280000</Volume>` is `integer` but `280,000` is `string`).
That is why the penalty for text is a `valuePattern` on the observed values: `"280,000"` is numeric text and
scores 0 from signals (recognised by name and context, reviewed rather than auto-accepted), while `LOW`/`HIGH`
is a tier and gets −30. Replay parses thousands separators, so the `annual / 12` derivation still runs on `"280,000"`.

| `kind` | Matches when | `pattern` / extras |
| --- | --- | --- |
| `namePattern` | regex matches the field name | .NET regex |
| `ancestorNamePattern` | regex matches any ancestor name | regex |
| `childNamePattern` | regex matches any child name | regex |
| `descriptionPattern` | regex matches the description | regex |
| `valuePattern` | **all** observed/allowed values match | regex |
| `valueShape` | **all** value shapes match (`999-99-9999`, `AAAA`) | regex over shapes |
| `valueRange` | observed min ≥ `minValue` and/or max ≤ `maxValue` | `minValue`, `maxValue` (at least one, `PB014`) |
| `valueInMap` | at least half the observed values resolve to a code of the named value map | `pattern: <valueMap id>` of this playbook |
| `dataType` | the field's data type is one of the listed | `integer|decimal` |
| `cardinality` | the field is `array` or `single` | `array` / `single` |

Weights are −100…100 (`PB014`). Signals with `appliesTo` only run for that attribute; without it they run when the
concept itself is the candidate. Each matching signal appends evidence such as
`Signal VOL-SIG-NUM-VOL (numeric) (+10).`

## 8. How detection scores a field (the matcher)

`PlaybookLibrary.Detect(field)` runs `PlaybookMatcher.Detect` for every active domain playbook and keeps the
highest-scoring result (ties → more matched name tokens). Within one playbook, the matcher scores the **concept** and
**each attribute** as candidates and returns the best that reaches the threshold.

Input is a `FieldContext`: name, path, ancestors (nearest first), children, description, kind (value/object),
data type, cardinality, up to N observed or allowed values, value shapes, min/max of numeric values. It is built from
the [system profile](System-Profiles.md), never from raw payloads.

### Step by step

```
tokens, context = Tokens(field)
  # generic leaf names (number, value, id, no, num, text(), type, code — with or without '@')
  # borrow their parent's name: /TaxId/@type → tokens [tax, id, type], context [Merchant, …]

for each candidate in [concept] + attributes:
  0. kind check       object fields only match the concept (or object-typed attributes); value fields only attributes
  1. vocabulary       best term whose tokens ⊆ field tokens; add its points
                       equivalent 60 | narrower 45 | broader 40 | related 35   (defaults; see §14)
                       relation ≠ equivalent  → trigger nonEquivalentTerm
  2. context          (attributes only) an ancestor matches a concept-level term → +25 (contextPoints)
                       no such ancestor and attribute.requiresContext → no match
  3. signals          each matching signal with matching appliesTo adds its weight
  4. sensitivity      attribute.sensitivity ≠ none → trigger sensitiveAttribute
  5. qualifiers       resolve every applicable qualifier (§6) → may add assumedQualifier / qualifierConflict
  6. score            clamp(sum, 0, 100)

result = best candidate with score ≥ matchThreshold (50); null if none
```

The result (`DetectionResult`) carries: `Playbook` reference, `Concept`, `Attribute`, `Score`, `Qualifiers`,
`Evidence` (one line per point source), `Triggers`, `RequiresReview` (any trigger listed in
`confidence.reviewTriggers`), `Questions` (review guidance whose `when` trigger fired, or has no `when`), `Warnings`
(qualifier conflicts + the playbook's `risks`), `MatchedTokens`.

### Example: `$.processing.annualCardVolume` (integer, 84 000–3 000 000)

| Step | Evidence | Points |
| --- | --- | --- |
| vocabulary | `Name matches equivalent term 'CardVolume' (+60).` (the implicit attribute-name term; `card volume` ties and is listed later) | 60 |
| context | `Ancestor 'processing' names the ProcessingVolume concept (+25).` (`processing` is a *related* concept term; any concept-level term counts for context) | 85 |
| signal | `Signal VOL-SIG-NUM-VOL (numeric) (+10).` | 95 |
| qualifier | `period=annual from the name.` | — |
| qualifier | `currencyUnit` – nothing in name or values, no default → unset | — |

Result: `ProcessingVolume.CardVolume [period=annual]`, score **95**, no triggers, no review.

### Example: `/UnderwritingRequest/Processing/MonthlyVolume` (decimal)

`volume` (+60) → ancestor `Processing` (+25) → numeric (+10) = **95**, `period=monthly` from the name.

### Example: `$.processing.cardVolume` with no period word

Same 95, but `period=monthly assumed (playbook default).` → trigger `assumedQualifier` → **requires review**, question
"The name does not say monthly or annual. Which period does this volume cover?". This is test `VOL-T-03`.

### Example: `volumeTier` with values `LOW`, `HIGH`

`volume` (+60), no ancestor, values are not numbers → `VOL-SIG-TIER-VOL` (−30) = 30 < 50 → **no match** (test `VOL-T-06`).

### Example: `annualCardVolume` sent as text `"280,000"`

`CardVolume` (+60), ancestor `processing` (+25), type `string` so no `VOL-SIG-NUM-VOL`; the values are numeric text so
`VOL-SIG-TIER-VOL` does not fire → 85, `period=annual` from the name (test `VOL-T-08`). Below `autoAcceptAt` 90, so the
row is reviewed.

### Example: `$.owners[*].ownershipPercent` (decimal, 0.2–1)

`ownership percent` (+60) + ancestor `owners` (+25) + range signal (+10) = 95. Qualifier `unit`: name says `percent`,
values fit `fraction` → **`unit=fraction`**, warning recorded, trigger `qualifierConflict` → requires review. The
mapping later uses derivation `OWN-PCT-01` (`fraction * 100`) to feed a percent target — the conflict is what makes
the conversion happen instead of a wrong direct copy.

## 9. Derivation rules

A derivation says how to compute one variant of an attribute from other attributes/variants. It is what turns an
annual figure into a monthly one, cents into dollars, or MOTO + e-commerce into card-not-present.

```yaml
derivations:
  - id: VOL-PERIOD-01                     # unique within the playbook (PB009)
    description: Annual volume to monthly volume.
    output:
      attribute: CardVolume
      qualifiers: { period: monthly }     # what the rule PRODUCES
    inputs:
      - name: annual                      # variable name used in the expression
        attribute: CardVolume             # an attribute of THIS concept (derivations stay inside one playbook)
        qualifiers: { period: annual }    # what the rule NEEDS — the source must carry exactly these
    expression: annual / 12
    transformation: periodConversion      # direct|rename|typeCast|unitConversion|periodConversion|concat|split|
                                          # aggregate|enumMap|lookup|conditional|default|derived
    dataLoss: none                        # none | low | medium | high
    requiresReview: false                 # true → "Rule X rests on an assumption", −10 confidence
    note: Assumes volume is spread evenly; seasonal merchants may differ.   # surfaced as an assumption finding
    examples:                             # executed by `playbook test`; must pass to publish
      - inputs: { annual: 3000000 }
        expected: 250000
      - inputs: { annual: 84000 }
        expected: 7000
```

More shipped rules, to show the range:

| Rule | Expression | Transformation | Notes |
| --- | --- | --- | --- |
| `VOL-PERIOD-02` | `monthly * 12` | periodConversion | inverse of 01 |
| `VOL-UNIT-01` | `cents / 100` | unitConversion | `currencyUnit: minor` → `major`; 12345 → 123.45 |
| `OWN-PCT-01` | `fraction * 100` | unitConversion | ownership 0–1 → 0–100 |
| `OWN-PCT-02` | `percent / 100` | unitConversion | inverse |
| `OWN-NAME-01` | *(none)* | concat | first + last → `FullName`; non-arithmetic rules have no expression |
| `OWN-NAME-02` | *(none)* | split | full → first; `dataLoss: high`, `requiresReview: true` |
| `MIX-CNP-01` | `moto + ecomm` | aggregate | two inputs → `ChannelMix.CardNotPresent` |
| `MIX-CNP-02` | `100 - cp` | derived | one input, alternative route to the same output |
| `MIX-SPLIT-01` | *(none)* | split | CNP → Ecommerce, high data loss, review |

### How the generator applies a derivation (`MappingBuilder.ByDerivation`)

Runs only when no direct concept pair was found for a target field that **was** recognised.

1. Take the target's detection: attribute `A`, qualifiers `Q_t`.
2. Candidate rules = rules of that playbook whose `output.attribute == A` and whose `output.qualifiers` agree with
   `Q_t` on every qualifier both sides state.
3. For every input of the rule, find the **best source field** whose detection is this concept + the input's attribute
   and whose qualifiers *contain* every qualifier the input states (`period=annual` must be present on the source, not
   merely absent). "Best" = highest detection score. If any input has no source, the rule is not applicable.
4. Order applicable rules: rules **not** requiring review first, then lower `dataLoss`. Apply the first; the others
   are mentioned as cross-checks ("Rule(s) MIX-CNP-02 (100 - cp) also apply and can cross-check the result.").
5. Build the row:
   * `type` = `manyToOne` if >1 input; else `derived` if `transformation: derived`; else `oneToOne`.
   * `transformation.type` = the rule's transformation; `rule` = description + `(ID: var = path, …)`;
     `expression` = the rule expression; `inputs` = `{ variableName: sourcePath }` — exactly what replay evaluates.
   * `confidence` = min(all input detection scores, target detection score); −10 if `requiresReview`; −10 if
     `dataLoss ≥ medium`.
   * reasoning: "Target is ProcessingVolume.CardVolume [period=monthly]; domain/processing-volume@1.0.0 derives it
     with rule VOL-PERIOD-01 from ProcessingVolume.CardVolume [period=annual]."
   * evidence: `Derivation VOL-PERIOD-01: annual / 12` plus the source and target detection evidence.
   * `note` → an **assumption finding** on the mapping ("… uses rule VOL-PERIOD-01: Assumes volume is spread evenly …").
   * input detections that themselves required review (e.g. a qualifier conflict) add "Source match needs review
     (QualifierConflict)." and their review questions.

See the [worked example](Worked-Example-Volume-Mapping.md) for the resulting JSON row.

## 10. Conditional rules

A conditional rule infers a coded value from *another* concept when the target is not sent by the source at all.

```yaml
conditions:
  - id: TIN-TYPE-01
    description: Sole proprietors usually use the owner's SSN; every other entity type uses an EIN.
    output:
      attribute: TaxIdType
    cases:
      - when:
          - concept: LegalEntity.EntityType     # Concept.Attribute of ANY domain playbook (PB043)
            in: [SOLE_PROPRIETORSHIP]           # canonical codes of that attribute's value map (PB044)
        then: SSN
    otherwise: EIN
    examples:
      - inputs: { LegalEntity.EntityType: SOLE_PROPRIETORSHIP }
        expected: SSN
      - inputs: { LegalEntity.EntityType: LLC }
        expected: EIN
```

### How the generator applies it (`MappingBuilder.ByCondition`)

Runs after derivations fail, for a recognised target.

1. Find rules whose output attribute matches the target.
2. For every `when` clause find a source field detected as that concept (best score). All clauses of at least one case
   must be satisfiable, otherwise the rule is skipped.
3. Build a `conditional` transformation:
   * `condition`: a readable form using the **source's own spellings** where they are known —
     `entityType in (SOLE_PROP) → SSN; otherwise → EIN` — because the source sends `SOLE_PROP`, which the entity-type
     value map resolves to the canonical `SOLE_PROPRIETORSHIP`.
   * `defaultValue`: the `otherwise` value.
   * `valueMap`: one entry per observed source value → resulting target value (`CORP → EIN (otherwise)`,
     `SOLE_PROP → SSN`).
4. `type` = `derived`; `confidence` = min(input scores, target score) **− 10**; review item "Conditional rule
   TIN-TYPE-01 infers the value; confirm the exceptions with the business."; an assumption finding "… is inferred,
   not sent".

Conditional rows therefore never auto-accept: the value is a business inference, not data the source sent.

## 11. Value maps

```yaml
valueMaps:
  - id: ENTITY-TYPES
    attribute: EntityType
    values:
      - code: SOLE_PROPRIETORSHIP          # canonical code, unique (PB017)
        label: Sole proprietorship
        aliases: [SP, SOLE_PROP, SOLE PROP, SOLEPROP, INDIVIDUAL, "Sole Proprietor"]
      - code: LLC
        label: Limited liability company
        aliases: [L.L.C., LIMITED LIABILITY COMPANY, LIMITED LIABILITY CO]
      - code: CORPORATION
        aliases: [CORP, C, C-CORP, C CORP, INC, INCORPORATED]
```

`PlaybookMatcher.ResolveCode(map, value)` compares case-insensitively after compacting punctuation/whitespace, against
codes, labels and aliases. A spelling may belong to only one code (`PB017`).

Value maps are used in three places:

* **Detection** – a `valueInMap` signal ("at least half the observed values are known codes") is strong evidence, e.g.
  `ENT-SIG-CODES` +20 on `entityType`.
* **Direct mappings** – when both source and target values resolve through the same map, the row gets an `enumMap`
  transformation with a per-value table: `CORP → C`, `SOLE_PROP → SP`. A source value that resolves to a code but was
  never seen in the target samples is emitted with the canonical code and the note "Playbook code LLC; not seen in
  target samples, confirm the target spelling." (review). A source value with **no** code costs −15 and a review item.
* **Conditional rules** – to normalise observed source values to the canonical codes named in `when … in`.

## 12. Validation rules

Validations are sanity checks executed at **replay** time against generated target payloads (and, at authoring
time, against their own examples). They do not affect detection or mapping confidence.

```yaml
validations:
  - id: VOL-VAL-01
    description: Average ticket cannot be above high ticket.
    inputs:
      - { name: avg,  attribute: AverageTicket }
      - { name: high, attribute: HighTicket }
    expression: avg <= high
    severity: error                     # error (default) | warning
    examples:
      - { inputs: { avg: 42.1, high: 1500 }, expected: true }
      - { inputs: { avg: 200,  high: 150  }, expected: false }
  - id: VOL-VAL-02
    description: Volume cannot be negative.
    inputs: [{ name: volume, attribute: CardVolume }]
    expression: volume >= 0
```

List-valued inputs feed the aggregate functions: `OWN-VAL-01` is `sum(ownership) <= 100` over every owner's
ownership percent; `MIX-VAL-01` is `abs(cp + moto + ecomm - 100) <= 0.5`. A validation runs only when every input can
be located in the target through the mapping's business concepts; see [Replay and Validation](Replay-and-Validation.md).

## 13. The expression language

`Expressions.cs` implements a small, side-effect-free language used by derivation and validation rules.

* Operands: named inputs (numbers or lists of numbers), numeric literals.
* Arithmetic: `+ - * /`, parentheses.
* Comparison: `< <= == != >= >` → boolean (validations must produce a boolean; derivations a number).
* Functions: `sum(list)`, `count(list)`, `min(list)`, `max(list)`, `abs(x)`, `round(x)`.
* Errors (fail the example / replay row, never silently produce a value): unknown variable, division by zero,
  unknown function, wrong argument count, type mismatch (e.g. adding a list to a number).
* No strings, no dates, no conditionals — use `conditions:` for branching and `concat`/`split` transformations for
  text.

A derivation or validation with examples must have an expression (`PB015`); rules with text transformations
(`concat`, `split`) have none and are executed by the transformation type at replay.

## 14. Confidence, risks, review guidance, AI guidance

```yaml
confidence:                     # every key optional; these are the defaults
  equivalentTermPoints: 60
  narrowerTermPoints: 45
  broaderTermPoints: 40
  relatedTermPoints: 35
  contextPoints: 25
  matchThreshold: 50            # below this the playbook does not claim the field
  reviewTriggers:               # which triggers make RequiresReview true
    - nonEquivalentTerm
    - assumedQualifier
    - qualifierConflict
    - sensitiveAttribute
risks:
  - id: VOL-RISK-PERIOD
    appliesTo: CardVolume       # omitted → every match of the concept
    level: high
    text: A monthly/annual mix-up changes exposure twelve-fold.
reviewGuidance:
  - id: VOL-Q-PERIOD
    appliesTo: CardVolume
    when: assumedQualifier      # omitted → asked on every match of the attribute
    question: The name does not say monthly or annual. Which period does this volume cover?
  - id: VOL-Q-GROSS
    appliesTo: CardVolume
    when: nonEquivalentTerm
    question: Does this figure include cash or other non-card sales?
aiGuidance: Card volume is expected card sales for a period. Always establish the period (monthly or annual) and the unit (dollars or cents). Gross sales and revenue may include non-card sales.
```

* Points and thresholds are 0–100 (`PB019`).
* Risks are copied into the detection's `Warnings` and onto the mapping row.
* Review questions become the row's `review.openQuestion` (joined) and are part of what blocks the process
  `open-questions` gate.
* `aiGuidance` is the only free text the AI receives about the domain besides concept/attribute names and descriptions.

## 15. Tests

Tests are detection assertions run by `mapwright playbook test` and by the API before publishing. Together with rule
`examples` they are the playbook's regression suite.

```yaml
tests:
  - id: VOL-T-01
    description: Annual card volume.
    field:                         # a synthetic FieldContext
      name: annualCardVolume
      ancestors: [processing, $]   # nearest first
      dataType: integer
      minValue: 84000
      maxValue: 3000000
      # also allowed: children, description, kind, cardinality, values, valueShapes
    expect: ProcessingVolume.CardVolume   # or 'ProcessingVolume' for the concept, or null for "must not match"
    expectQualifiers: { period: annual }
    minScore: 90
    expectReview: false
  - id: VOL-T-06
    description: A volume tier is not a volume.
    field: { name: volumeTier, values: [LOW, HIGH] }
    expect: null
```

Checks performed, in order: concept matches `expect` (or nothing matched when `expect: null`); each `expectQualifiers`
entry; `score ≥ minScore`; `RequiresReview == expectReview`. Failure messages are precise
(`expected period=annual, got period=monthly`, `score 85 below 90`, `expected review to be required`).

Rule examples are run through the expression engine (`VOL-PERIOD-01#1`: `annual=3000000` → expects `250000`) and
conditional examples through the case logic.

## 16. Validation of the playbook itself (`PB0xx` codes)

`mapwright playbook validate` (and every save through the API) reports errors and warnings with stable codes:

| Code | Meaning |
| --- | --- |
| PB001–PB006 | header: spec version, id shape, semver, name, kind/section, change note for published |
| PB009 | rule ids missing or duplicated |
| PB010–PB011 | concept/attribute naming; `appliesTo`/`attribute` must name a known attribute |
| PB012 | vocabulary: empty/duplicate term, equivalent term shared by two targets |
| PB013 | qualifiers: duplicate, missing values, default not a value, min > max, unknown `qualifier=value` in a rule |
| PB014 | signals: weight range, `valueRange` needs a bound, `valueInMap` must name a map, data type / cardinality syntax |
| PB015 | derivation with examples or `derived` transformation needs an expression |
| PB016 | conditional needs ≥1 case; clause must reference `Concept.Attribute` with values |
| PB017 | value map codes unique; spelling used by two codes |
| PB019 | confidence points/thresholds 0–100 |
| PB020–PB021 | tests: `expect` shape; published domain needs ≥1 test |
| PB030–PB036 | process: steps present/unique; AI step optional with `maxConfidence < autoAcceptAt`; publish last after review; only match steps `uses`; review steps need reviewers; gate ids/coverage/counts; inputs/outputs |
| PB040–PB046 | library-level: duplicate version, two published versions, unknown `uses`, unknown clause concept/value, attribute owned by two playbooks |

## 17. Process playbooks

A process playbook describes the workflow around the domain playbooks. `process/onboard-new-system` is the shipped one:

```yaml
process:
  inputs:                       # what each side must provide (samplePayload, jsonSchema, xsd, openApi, wsdl, …), minCount
  steps:
    - { id: ingest,  kind: ingest }
    - { id: profile, kind: profile,  gates: [{ id: profile-conflicts, metric: unresolvedConflicts, operator: gt, value: 0, action: warn }] }
    # gate metrics: requiredTargetCoverage | lowConfidenceMappings | unresolvedConflicts | failedValidations |
    #               openQuestions | unreviewedSensitiveMappings;  operators gt/lt/…;  actions stop | requireReview | warn
    - id: match
      kind: match
      uses: [domain/owners, domain/processing-volume, domain/channel-mix, domain/tax-id, domain/entity-type]
    - id: ai-assist
      kind: aiAssist
      optional: true            # required (PB031): the process must work without AI
      maxConfidence: 70         # must be < thresholds.autoAcceptAt (PB031)
    - id: validate
      kind: validate
      gates:
        - { metric: failedValidations,       operator: gt, value: 0,   action: stop }
        - { metric: requiredTargetCoverage,  operator: lt, value: 100, action: requireReview }
    - id: review
      kind: review
      reviewers: [Integration BA, Underwriting SME]     # ≥1 required (PB035)
      gates:
        - { metric: openQuestions,               operator: gt, value: 0, action: stop }
        - { metric: unreviewedSensitiveMappings,  operator: gt, value: 0, action: stop }
    - { id: publish, kind: publish }      # must be last and after a review step (PB032)
  thresholds:
    autoAcceptAt: 90            # 0 ≤ rejectBelow ≤ reviewBelow ≤ autoAcceptAt ≤ 100 (PB033)
    reviewBelow: 90
    rejectBelow: 30
  outputs: [json, xlsx, csv, html]
```

How the engine uses it:

* `thresholds.autoAcceptAt` raises the mapping's auto-accept bar: the generator uses the higher of the mapping
  policy's high-confidence threshold and `autoAcceptAt`. With 90, an 85-point derived row is reviewed even if
  otherwise clean.
* The AI step's `maxConfidence` caps every AI suggestion (default 70), so an AI-only row can never auto-accept.
* Gates are evaluated on the mapping's metrics (failed validations, coverage, open questions, unreviewed sensitive
  rows) to decide `stop` / `requireReview` / `warn` in the workflow and UI.
* `uses` documents and restricts which domain playbooks a match step relies on; unknown ids are `PB042`.

## 18. How the playbooks drive a mapping (summary)

```
source profile ─┐                                  ┌─ target profile
                ▼                                  ▼
   Detect every value field with the active domain playbooks
   → business concept + attribute + qualifiers + score + evidence + triggers
                │
                ▼   for each TARGET field, first strategy that applies wins:
   1. ByConcept     same Concept.Attribute on a source, qualifiers agree        → direct / rename / enumMap / typeCast
   2. ByDerivation  a derivation whose output = target variant, inputs found    → periodConversion / unitConversion / aggregate / concat …
   3. ByCondition   a conditional rule whose clause concepts are available      → conditional (inferred, always reviewed)
   4. ByName        at least one side unrecognised; names similar (≥50)        → capped at 75, always reviewed
   5. Unmapped      nothing                                                     → confidence 0, suggested resolution
                │
                ▼
   Finish: clamp confidence, add data-loss / below-threshold review items, mask sensitive samples,
           autoAccepted only if no review items and confidence ≥ autoAcceptAt
```

Every knowledge-bearing decision in that flow — the concept a field is, the qualifier it carries, the arithmetic
between variants, the code translation, the inferred code, the question a reviewer is asked — comes from a playbook
section described on this page. Details of the strategies are in [Mapping Generation](Mapping-Generation.md).

## 19. Authoring checklist

1. **Pick one concept** and its attributes with data types and sensitivity (`none | pii | sensitivePii | financial | pci`).
   Derivations and validations only see this concept's attributes; conditional rules may reference another playbook's
   `Concept.Attribute`. Never redefine an attribute another playbook owns (`PB046`).
2. **Collect vocabulary** from real contracts; mark anything that is not a true synonym as `related`/`broader`/
   `narrower` so it is reviewed rather than silently accepted.
3. **Model qualifiers** for every dimension that changes meaning (period, unit, currency unit). Add numeric ranges
   when the values themselves reveal the unit; add a `default` only when a wrong assumption would be caught
   (pair it with a `reviewGuidance` `when: assumedQualifier`).
4. **Add signals**: a positive `dataType` signal for numeric attributes, a negative one for the wrong type, a
   `valueInMap` signal when there is a code list, `valueShape` for formatted ids.
5. **Write derivations** for every pair of variants you expect to meet (both directions), each with ≥2 examples.
   Set `dataLoss` and `requiresReview` honestly — they lower confidence and force review.
6. **Write conditional rules** for values that are inferred from other concepts; reference canonical codes only.
7. **Write value maps** with every spelling you have seen; one spelling → one code.
8. **Write validations** that catch the mistakes you fear (sum to 100, avg ≤ high, non-negative).
9. **Add risks and review questions**; tie questions to triggers so reviewers are not spammed.
10. **Write `aiGuidance`** as the one paragraph you would tell a new analyst.
11. **Add tests**: at least one positive per attribute with `minScore`, one `expect: null` negative, one per
    qualifier value, one for each review trigger you rely on.
12. Run `mapwright playbook test playbooks` until clean, add a change note, set `status: published`, and regenerate a
    sample mapping to see the effect (`mapwright map …`).

## 20. Where playbooks live and how they are loaded

* Repo: `playbooks/domain/*.yaml`, `playbooks/process/*.yaml`. The CLI defaults to `./playbooks`
  (`--playbooks <file|dir>…` to override; `.yaml`, `.yml`, `.json` under directories are loaded in ordinal order).
* API/UI: playbooks are stored in the SQLite store (`MAPWRIGHT_DATA_DIR`) and edited as drafts; the starter set is seeded
  from the repo files. `mapwright playbook convert … --to json|yaml` switches formats.
* `PlaybookLibrary.Active` decides which version of each id is in force (published, else newest non-retired).
* Samples of every section are in the five shipped domain playbooks; `processing-volume.yaml` is the smallest
  complete one and the basis of the [worked example](Worked-Example-Volume-Mapping.md).
