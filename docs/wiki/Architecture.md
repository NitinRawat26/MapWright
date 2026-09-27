# Architecture

## The problem MapWright solves

Merchant-acquiring systems (sales/CRM capture, underwriting, boarding, risk) exchange the same business facts —
legal entity, principals, expected volume, channel mix, tax ids — under different names, shapes, units and periods,
in JSON or XML. MapWright produces the field-level mapping between any two such systems **without a canonical
model**: knowledge is captured once per business concept in a [playbook](Playbooks.md) and reused for every pair of
systems.

## Design principles

1. **Deterministic first.** Profiles + published playbooks ⇒ the same mapping every time. Every row explains itself
   (reasoning, evidence, confidence arithmetic).
2. **Playbooks own the knowledge.** No business term, unit, conversion or code list is hard-coded. Adding a variant
   means editing YAML and publishing, not changing code.
3. **AI is optional and subordinate.** It runs only on request, only over what the playbooks left unmatched, sees only
   masked metadata, is capped below auto-accept and can never publish. See [AI Assist](AI-Assist.md).
4. **The spec is the source of truth.** Excel/HTML/PDF/CSV are renderings of `mapping.json`; replay executes the same
   spec, so documents cannot drift from behaviour.
5. **Sensitive data stays masked** at every stage: profiles keep only masked samples of PII/financial fields, specs
   show ≤ 4 digits, AI never sees them.

## End-to-end pipeline

```
 inputs per system               profile               playbooks                    mapping                 replay
┌───────────────────┐   ┌──────────────────────┐   ┌────────────────────┐   ┌────────────────────────┐   ┌──────────────────┐
│ sample payloads   │   │ one SystemProfile:   │   │ domain playbooks:  │   │ MappingGenerator       │   │ source samples → │
│ JSON Schema /     │──►│ fields, paths, types,│──►│ detect concept +   │──►│ ByConcept / ByDerivation│──►│ target payloads  │
│ OpenAPI / XSD /   │   │ cardinality, values, │   │ attribute +        │   │ / ByCondition / ByName │   │ per-row checks + │
│ WSDL / field spec │   │ shapes, ranges,      │   │ qualifiers + score │   │ / Unmapped → rows with │   │ playbook         │
│ / dictionary /    │   │ required, findings,  │   │ + evidence         │   │ transformation,        │   │ validation rules │
│ PDF / Word        │   │ provenance, masking  │   │ process playbook:  │   │ confidence, evidence,  │   │                  │
└───────────────────┘   └──────────────────────┘   │ steps, gates,      │   │ risk, review, findings │   └──────────────────┘
                                                   │ thresholds         │   └───────────┬────────────┘
                                                   └────────────────────┘               │  optional AI over unmapped rows
                                                                                        ▼
                                                                          render: xlsx · html · pdf · csv
```

Pages: [System Profiles](System-Profiles.md) → [Playbooks](Playbooks.md) → [Mapping Generation](Mapping-Generation.md)
→ [Replay and Validation](Replay-and-Validation.md).

## Projects

| Project | Responsibility | Key types |
| --- | --- | --- |
| `src/MapWright.Core` | Everything deterministic: profile readers (samples, contracts, specs, dictionaries), playbook model / serializer / validator / matcher / test runner / expression engine, mapping generator, spec model and validator, replay (transform engine, JSON/XML writers, validation) | `SystemProfile`, `Playbook`, `PlaybookLibrary`, `PlaybookMatcher`, `MappingGenerator`, `MappingBuilder`, `MappingDocument`, `Expressions` |
| `src/MapWright.Output` | Report model and renderers (Excel, CSV, HTML, PDF); Excel field-spec reader | |
| `src/MapWright.Ai` | Provider abstraction (`IAiProvider`), Vertex AI and Ollama providers, fallback chain, masked prompts for field decoding and mapping suggestions | `AiFieldAssistant`, `AiMappingAssistant` |
| `src/MapWright.Store` | SQLite persistence: playbook versions + lifecycle + audit, profiles, detections, mappings, review decisions, AI suggestions | |
| `src/MapWright.Cli` | `mapwright` command line over Core/Output/Ai | |
| `src/MapWright.Api` | ASP.NET Core API (+ Swagger) over the engine and the store; serves the built web UI | |
| `web/` | Angular (Material) UI: playbooks, profiles, mappings, replay, AI suggestions inbox | |
| `playbooks/` | Starter domain and process playbooks (seeded into an empty store) | |
| `samples/` | Synthetic systems (SalesAlpha CRM JSON, UW Core XML, …), contracts, profiles and generated mappings | |
| `tests/MapWright.Tests` | Unit and API tests | |

## Data flow between store and engine

* The CLI works on files: profiles, playbooks and mappings are JSON/YAML on disk.
* The API stores the same documents in SQLite and adds lifecycle (playbook status transitions with independent
  review), audit history, review decisions and the AI suggestions inbox. Generation through the API uses **published**
  playbooks only; drafts never influence a mapping until published.
* AI suggestions approved in the inbox flow back into playbook **drafts** (new vocabulary term, attribute or
  playbook), closing the loop: what AI guessed once becomes deterministic knowledge after human review.

## Where to look when …

| You want to | Read | Code |
| --- | --- | --- |
| understand why a field was (not) recognised | [Playbooks §8](Playbooks.md#8-how-detection-scores-a-field-the-matcher) | `PlaybookMatcher.Detect`, row `evidence[]` |
| understand why a row got its source/transformation/confidence | [Mapping Generation §3–7](Mapping-Generation.md) | `MappingBuilder` |
| add a unit/period/synonym/code | [Playbooks §19](Playbooks.md#19-authoring-checklist) | `playbooks/domain/*.yaml` |
| prove a mapping on real data | [Replay and Validation](Replay-and-Validation.md) | `MapWright.Core/Replay` |
| know what AI is allowed to do | [AI Assist](AI-Assist.md) | `MapWright.Ai` |
