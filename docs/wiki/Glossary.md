# Glossary

| Term | Meaning | Where |
| --- | --- | --- |
| **Attribute** | A named fact of a concept: `ProcessingVolume.CardVolume`, `Principal.OwnershipPercent`. Rules, value maps and validations attach to attributes | [Playbooks §4](Playbooks.md#4-concept-and-attributes) |
| **Auto-accept** | Review status `autoAccepted`: a mapped row with no review items and confidence ≥ `autoAcceptAt` (90 with the starter process playbook). Everything else `needsReview` | [Mapping Generation §7](Mapping-Generation.md#7-review-status-and-auto-acceptance) |
| **Business concept** | The thing a field is *about*, independent of system or spelling: `ProcessingVolume`, `LegalEntity`, `Principal`, `ChannelMix` | [Playbooks §4](Playbooks.md#4-concept-and-attributes) |
| **Conditional rule** | A playbook rule that infers a target value from other concepts' values (`entityType in (SOLE_PROP) → SSN; otherwise EIN`). Always reviewed | [Playbooks §10](Playbooks.md#10-conditional-rules) |
| **Confidence** | 0–100 per detection (sum of playbook points) and per mapping row (min of the detections involved, minus penalties). Bands High ≥ 85 / Medium ≥ 60 / Low | [Mapping Generation §6](Mapping-Generation.md#6-confidence-arithmetic) |
| **Contract** | A machine-readable description of a system: JSON Schema, OpenAPI, XSD, WSDL, field spec, data dictionary | [System Profiles](System-Profiles.md) |
| **Derivation** | A playbook rule that computes a target variant from same-concept inputs with an expression: `annual / 12`, `moto + ecomm`, `cents / 100` | [Playbooks §9](Playbooks.md#9-derivation-rules) |
| **Detection** | The matcher's verdict for one field: playbook, concept, attribute, qualifiers, score, evidence, review triggers, questions | [Playbooks §8](Playbooks.md#8-how-detection-scores-a-field-the-matcher) |
| **Domain playbook** | YAML knowledge for one business concept: vocabulary, qualifiers, signals, derivations, conditions, value maps, validations, guidance, tests | [Playbooks §3](Playbooks.md#3-domain-playbook-anatomy) |
| **Evidence** | Per-row list of what the decision rested on: playbook terms/signals, sample payloads, name similarity, reviewer, AI | [Mapping Generation §9](Mapping-Generation.md#9-the-mapping-spec) |
| **Expression** | The restricted arithmetic/comparison language of derivations and validations (`+ - * / < <= == != >= >`, `sum count min max abs round`) | [Playbooks §13](Playbooks.md#13-the-expression-language) |
| **Finding** | A mapping-level note: `assumption` (rule note, inferred value, list pairing) or `conflict` (qualifier or profile disagreement) | [Mapping Generation §8](Mapping-Generation.md#8-findings-orphans-conflicts) |
| **Mapping spec** | `mapping.json`: the versioned, machine-readable source of truth; documents are rendered from it and replay executes it | [Mapping Generation §9](Mapping-Generation.md#9-the-mapping-spec) |
| **Orphan** | A source field no row uses | [Mapping Generation §8](Mapping-Generation.md#8-findings-orphans-conflicts) |
| **Process playbook** | The workflow: steps (`ingest → profile → match → aiAssist → validate → review → publish`), which domain playbooks to use, gates, thresholds, reviewers, outputs | [Playbooks §17](Playbooks.md#17-process-playbooks) |
| **Profile** | Normalised description of one system's fields, built from samples and contracts | [System Profiles](System-Profiles.md) |
| **Qualifier** | A dimension that changes a value's meaning without changing the concept: `period=annual|monthly`, `currencyUnit=major|minor`, `unit=percent|fraction`. Direct pairing needs agreement; derivations bridge disagreements | [Playbooks §6](Playbooks.md#6-qualifiers) |
| **Rate / volume mapping** | Mapping processing figures whose *unit or period* differs between systems — annual vs monthly volume, cents vs dollars, percent vs fraction, MOTO + e-commerce vs card-not-present. Implemented by qualifiers + derivations in `domain/processing-volume` and `domain/channel-mix` | [Worked Example](Worked-Example-Volume-Mapping.md) |
| **Replay** | Running source samples through the spec to produce target payloads and check every row and validation rule | [Replay and Validation](Replay-and-Validation.md) |
| **Review trigger** | Why a detection needs a human: `NonEquivalentTerm`, `AssumedQualifier`, `QualifierConflict`, `SensitiveAttribute`, `LossyDerivation`, `ConditionalValue` | [Playbooks §8](Playbooks.md#8-how-detection-scores-a-field-the-matcher) |
| **Sensitive** | `pii`, `sensitivePii`, `financial`, `pci`: values masked in profiles, specs and reports; never sent to AI | [System Profiles](System-Profiles.md#sensitive-data) |
| **Signal** | A non-vocabulary hint in a playbook that adds or subtracts points: data type, value shape, numeric range, cardinality, parent/child names | [Playbooks §7](Playbooks.md#7-detection-signals) |
| **Transformation** | What replay does to the source value(s) for a row: `direct`, `rename`, `typeCast`, `enumMap`, `periodConversion`, `unitConversion`, `aggregate`, `concat`, `split`, `conditional`, `derived`, … | [Mapping Generation §10](Mapping-Generation.md#10-mapping-types-and-transformation-types) |
| **Validation rule** | A playbook expression that must hold over produced target values (`avg <= high`, `sum(ownership) <= 100`); run by replay | [Playbooks §12](Playbooks.md#12-validation-rules) |
| **Value map** | Canonical codes with aliases per system (`SOLE_PROPRIETORSHIP`: `SOLE_PROP`, `SP`) used for `enumMap` transformations and conditional clauses | [Playbooks §11](Playbooks.md#11-value-maps) |
| **Vocabulary** | Terms for a concept/attribute with a relation strength: equivalent 60, narrower 45, broader 40, related 35 | [Playbooks §5](Playbooks.md#5-vocabulary) |
