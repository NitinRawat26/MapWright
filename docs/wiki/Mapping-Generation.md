# Mapping Generation

`mapwright map <source-profile.json> <target-profile.json>` (or `POST /api/mappings/generate`) turns two
[system profiles](System-Profiles.md) plus the active [playbooks](Playbooks.md) into a **mapping spec**: one row per
target value field, each with sources, transformation, confidence, reasoning, evidence, risk and review state. The
spec (`mapping.json`) is the single source of truth; the Excel/HTML/PDF/CSV documents are rendered from it.

Code: `src/MapWright.Core/Matching/MappingGenerator.cs` (orchestration), `MappingBuilder.cs` (per-row decisions),
`RecognisedField.cs` (profile field + detection), `NameMatcher.cs` (fallback), `src/MapWright.Core/Spec/`
(the document model and `MappingSpecValidator`).

## Contents

1. [Pipeline](#1-pipeline)
2. [Recognising fields](#2-recognising-fields)
3. [The five strategies, in order](#3-the-five-strategies-in-order)
4. [Choosing the transformation for a direct pair](#4-choosing-the-transformation-for-a-direct-pair)
5. [Lists and repeating fields](#5-lists-and-repeating-fields)
6. [Confidence arithmetic](#6-confidence-arithmetic)
7. [Review status and auto-acceptance](#7-review-status-and-auto-acceptance)
8. [Findings, orphans, conflicts](#8-findings-orphans-conflicts)
9. [The mapping spec](#9-the-mapping-spec)
10. [Mapping types and transformation types](#10-mapping-types-and-transformation-types)
11. [Spec validation](#11-spec-validation)
12. [Rendering](#12-rendering)
13. [Editing and versioning a mapping](#13-editing-and-versioning-a-mapping)

## 1. Pipeline

```
MappingGenerator.Generate(source, target, library, options)
  1. sources = RecognisedField.From(sourceProfile, library)   # detect every value field
  2. targets = RecognisedField.From(targetProfile, library)
  3. for i, t in targets:  rows += MappingBuilder.Map(t, "M{i:000}")
  4. orphans  = source fields used by no row
  5. findings = builder findings (assumptions, list pairings, qualifier conflicts)
              + profile conflicts (type/kind conflicts, ambiguous date formats, mixed formats) on used fields
  6. playbooks = active domain playbooks cited by any row + active process playbooks
  7. document = MappingDocument { id, title, version 0.1.0, source/target system info, inputs, playbooks,
                                  confidencePolicy, mappings, findings, orphanSourceFields, changeLog[Generated] }
```

Everything is deterministic: same profiles + same playbooks ⇒ same spec (apart from `createdAt`).

## 2. Recognising fields

For every **value** field (not objects/lists) of a profile, `RecognisedField.From` builds a `FieldContext` — name,
path, ancestors, children, description, kind, data type, cardinality, observed/allowed values, value shapes, min/max —
and asks the library for the best detection across all active domain playbooks (see
[Playbooks §8](Playbooks.md#8-how-detection-scores-a-field-the-matcher)). It also records whether the field
**repeats** (it or an ancestor is an array), the nearest list path (`$.owners`) and that list's own detection
(`Principal`).

The result per field: `Detection?` = `{ Playbook, Concept, Attribute, Score, Qualifiers, Evidence, Triggers,
RequiresReview, Questions, Warnings }`, or `null` if no playbook reached its threshold.

## 3. The five strategies, in order

```csharp
var draft = ByConcept(target) ?? ByDerivation(target) ?? ByCondition(target) ?? ByName(target) ?? Unmapped(target);
```

The first that returns a draft wins; there is no scoring across strategies.

### 3.1 ByConcept – same business concept on both sides

* Needs a target detection.
* Candidates: sources whose `BusinessConcept` equals the target's **and** whose qualifiers agree
  (`QualifiersAgree`: every qualifier known on both sides has the same value; a qualifier known on one side only is
  fine).
* Ordered by source detection score, then name similarity to the target. The best becomes the source; the rest are
  listed in reasoning as "Other source candidates: …".
* Produces `type: oneToOne`, `confidence = min(sourceScore, targetScore)`, reasoning "Both fields are recognised as
  X [qualifiers] by playbook@version.", plus:
  * a review item per one-sided qualifier: "`period=monthly` is only known for the target field."
  * "Source/Target match needs review (Trigger, …)" when a detection required review, and a `conflict` finding
    when the trigger was `QualifierConflict`;
  * list-pairing risks (§5);
  * playbook evidence for both sides and sample evidence naming the payloads the field was seen in;
  * the detections' review questions.
* Transformation chosen by §4.

### 3.2 ByDerivation – compute the target variant from same-concept sources

Detailed in [Playbooks §9](Playbooks.md#9-derivation-rules). Summary:

* Needs a target detection with an attribute and a domain playbook.
* Candidate rules: same playbook, `output.attribute` = target attribute, every `output.qualifier` either absent on
  the target or equal.
* Each input is satisfied by the highest-scoring source detected as this concept + the input attribute and carrying
  every input qualifier. All inputs must be satisfied.
* Rules ordered: `requiresReview == false` first, then ascending `dataLoss`; the first is applied, the others are
  named as cross-checks.
* `type`: `manyToOne` (≥2 inputs) · `derived` (transformation `derived`) · otherwise `oneToOne`.
* `transformation = { type: rule.transformation, rule: "<description> (<ID>: var = path, …)", expression,
  inputs: { var: path } }`.
* Confidence `min(inputs…, target)` − 10 if `requiresReview` − 10 if `dataLoss ≥ medium`; review items "Rule X rests
  on an assumption." / data-loss note; the rule `note` becomes an **assumption finding**.

Examples from the sample mapping: `MonthlyVolume = annual / 12` (periodConversion), `OwnershipPct = fraction * 100`
(unitConversion, triggered by a qualifier conflict on the source), `CardNotPresentPct = moto + ecomm` (aggregate,
manyToOne), `Officer/FullName = first ⧺ last` (concat, manyToOne).

### 3.3 ByCondition – infer a code from another concept

Detailed in [Playbooks §10](Playbooks.md#10-conditional-rules). Summary: for a recognised target with a conditional
rule, every `when` clause concept must have a detected source. Produces `type: derived`,
`transformation.type: conditional` with `condition` (e.g. `entityType in (SOLE_PROP) → SSN; otherwise → EIN`),
`defaultValue`, a per-observed-value `valueMap`, and — when a case names more than one concept — `cases` that replay
executes. Confidence `min(...) − 10`; always reviewed ("Conditional rule X infers the value; confirm the exceptions
with the business."); assumption finding "… is inferred, not sent".

### 3.4 ByName – name similarity when a playbook is missing

* Considered only when the source **or** the target has no detection (two recognised fields with different
  concepts are never paired by name — the playbooks have already said they differ).
* `NameMatcher.Score`: 70 for identical names ignoring case/separators (`legalName` ~ `LegalName`), 60 when one name
  abbreviates the other's initials (`dba` ~ `DoingBusinessAs`), 40–69 for ≥ 50 % shared tokens; −20 when data types
  are incompatible. Minimum 50 to pair; ties broken by parent-name similarity.
* Confidence = `min(score, 75)`. Always adds "Name-only match; confirm the meaning, then add the terms to a
  playbook." and `nameSimilarity` evidence ("Name score 70").
* Transformation by §4. Sample rows: `legalName → LegalName` (70, direct), `dbaName → DoingBusinessAs` (60, rename),
  `mcc → MCC` (70, typeCast string → integer).

### 3.5 Unmapped

* `type: unmapped`, confidence 0, no sources.
* Recognised target: "Recognised as X by playbook, but no source field provides it." with resolution "Ask the source
  team for X, or agree a default."
* Unrecognised target: "No playbook recognises this field and no source field matches it." with resolution
  "Confirm the field's meaning, then add a playbook term, agree a default or ask the source team."
* If the target was seen in > 1 sample with exactly one distinct value and is not sensitive: "Every target sample
  holds 'X'; if it is fixed for this source system, map it as a constant."

The optional [AI pass](AI-Assist.md) runs only over these rows.

## 4. Choosing the transformation for a direct pair

`MappingBuilder.Transform(source, target)` — the first that applies:

| Order | Condition | Result |
| --- | --- | --- |
| 1 | the target attribute has a **value map** and source values resolve through it, with at least one spelling change | `enumMap`, "Translate codes through value map ENTITY-TYPES", `valueMap: [{CORP→C}, {SOLE_PROP→SP}, {LLC→LLC (note: playbook code, not seen in target samples)}]`. Unknown source values: −15 and review; unseen target spellings: review |
| 2 | both sides have a **format** and they differ (dates) | `typeCast`, "Convert date format …" |
| 3 | both sides have a single **value shape** and they differ | `typeCast`, "Reformat 999-99-9999 → 999999999", `pattern` = target shape |
| 4 | **data types** differ | `typeCast`, "Convert string → integer"; `TypeRisk`: decimal→integer −5 & medium loss; dateTime→date low loss; string→number low/medium depending on whether every sample parses |
| 5 | compacted names equal | `direct`, "Copy" |
| 6 | otherwise | `rename`, "Copy a to B" |

## 5. Lists and repeating fields

* Source repeats, target single → `dataLoss: high`, "Source repeats (one value per list item) but the target holds a
  single value; decide which item to send."
* Both repeat → an **assumption finding** "Each item of source list `$.owners` becomes an item of target list
  `/…/Officers/Officer` (both Principal)." If either list's detection required review (e.g. `officers` is a
  *related* term for `Principal`), every row under it gets "List pairing … needs review." and the list's questions
  ("Are all of these people beneficial owners (25%+)?").
* Replay keeps items aligned (`owners[1].firstName` with `owners[1].lastName`).

## 6. Confidence arithmetic

| Strategy | Base | Adjustments |
| --- | --- | --- |
| ByConcept | `min(sourceScore, targetScore)` | enumMap unknown values −15 · decimal→integer −5 |
| ByDerivation | `min(all input scores, targetScore)` | `requiresReview` −10 · `dataLoss ≥ medium` −10 |
| ByCondition | `min(all input scores, targetScore) − 10` | – |
| ByName | `min(nameScore, 75)` | – |
| Unmapped | 0 | – |

Detection scores themselves are 0–100 sums of playbook points (term 60/45/40/35, context 25, signals ±w). Final
confidence is clamped to 0–100. Bands (`confidencePolicy`, default High ≥ 85, Medium ≥ 60) are for display and spec
validation.

## 7. Review status and auto-acceptance

`Finish` decides the row's status:

1. `dataLoss ≥ medium` → review item "Data loss risk is medium/high."
2. Mapped, no review items, but `confidence < autoAcceptAt` → "Confidence N% is below auto-accept (M%)."
   `autoAcceptAt = max(confidencePolicy.highThreshold, processPlaybook.thresholds.autoAcceptAt)` — with the shipped
   process playbook that is **90**.
3. `status = needsReview` if unmapped or any review item exists; otherwise `autoAccepted`.
4. `review.openQuestion` = the collected playbook questions; `reasoning` = reasons + "Needs review: …" +
   "Assumption: …"; sample values of sensitive fields are masked (at most 4 visible digits).

In the sample mapping, 5 of 23 rows auto-accept (monthly volume, average ticket, both channel shares, tax id
number); the other 18 carry a concrete review reason or are unmapped.

## 8. Findings, orphans, conflicts

* **Findings** (`F001…`): `assumption` (rule notes, inferred values, list pairings), `conflict` (name vs value
  qualifier disagreement; profile type/kind conflicts, ambiguous date formats, mixed formats on fields the mapping
  uses). Each references the mapping row(s) it concerns and the playbook.
* **Orphan source fields**: source fields used by no row, with their detection if any — the "leftovers" a reviewer
  should confirm are intentionally dropped (`$.applicationId`, `$.account.address.*`, `$.owners[*].email` in the
  sample).
* **Playbooks**: the exact `id@version` list the mapping depends on, so a later playbook change is traceable.

## 9. The mapping spec

Top level: `specVersion`, `id`, `title`, `version`, `createdAt`, `description`, `source`/`target` (system name,
version, format), `confidencePolicy`, `inputs` (the payloads/contracts each profile was built from), `playbooks`,
`mappings[]`, `findings[]`, `orphanSourceFields[]`, `validationRuns[]` (from replay `--record`), `changeLog[]`.

Row (`FieldMapping`):

| Group | Properties |
| --- | --- |
| identity | `id`, `type` |
| ends | `sources[]`, `target` — name, path, dataType, format, required, cardinality, allowedValues, `sampleValue` (masked), description |
| semantics | `businessConcept`, `domainPlaybook` |
| transformation | `type`, `rule` (human), `expression` (machine), `inputs {var: path}`, `condition`, `cases[]`, `defaultValue`, `pattern`, `valueMap[]` |
| confidence | `confidencePercent`, `reasoning`, `evidence[]` (`kind: playbook | sample | schema | fieldSpec | documentation | priorMapping | reviewer | nameSimilarity | aiSuggestion`, `reference`, `detail`) |
| risk | `dataLoss`, `sensitivity`, `targetValidationRules[]` |
| review | `status` (`autoAccepted | needsReview | approved | rejected | overridden`), `reviewer`, `reviewedAt`, `comments`, `openQuestion` |
| gap | `suggestedResolution` (unmapped rows) |

The [worked example](Worked-Example-Volume-Mapping.md) shows complete rows.

## 10. Mapping types and transformation types

`type`: `oneToOne`, `manyToOne`, `oneToMany`, `constant`, `derived`, `unmapped`.

`transformation.type`: `direct`, `rename`, `typeCast`, `unitConversion`, `periodConversion`, `concat`, `split`,
`aggregate`, `enumMap`, `lookup`, `conditional`, `default`, `derived`. The generator picks `direct`/`rename`/
`typeCast`/`enumMap` for direct pairs and copies the rule's declared type for derivations and conditions; the others
are available to reviewers and AI suggestions.

## 11. Spec validation

`mapwright validate <mapping.json>` (`MappingSpecValidator`) enforces what JSON parsing cannot: unique ids and one row
per target path; source count consistent with type (`manyToOne` ≥ 2, `unmapped` 0); `enumMap` carries a value map;
only High-band rows may be `autoAccepted`; sensitive samples reveal ≤ 4 digits; findings and validation runs
reference existing rows; unknown or missing JSON properties are rejected.

## 12. Rendering

`mapwright render <mapping.json> [--format xlsx,html,pdf,csv]` writes the sign-off workbook (Summary, Mapping, Gaps,
Value Maps, Conflicts & Assumptions, Validation, Change Log), the self-contained HTML report, the landscape PDF and
the flat CSV. AI-suggested rows are highlighted and listed separately. Nothing in these documents is computed
outside the spec.

## 13. Editing and versioning a mapping

Through the API/UI a reviewer records a decision per row (`POST /api/mappings/{id}/rows/{rowId}/review`: approve,
reject or override with a corrected mapping), which sets `review.status`, `reviewer`, `reviewedAt` and `comments`.
Mappings are stored and versioned in the store; re-generating after a playbook change produces a fresh document you
can compare with the reviewed one. See [API Reference](API-Reference.md) and [Web UI](Web-UI.md).
