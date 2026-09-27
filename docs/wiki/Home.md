# MapWright Wiki

MapWright generates field-level mappings between merchant-acquiring systems (JSON or XML) without a shared
canonical model. Business knowledge lives in **playbooks**; the engine applies them deterministically, an optional
**AI** pass fills what the playbooks missed, and every row carries evidence, confidence and open questions for a
reviewer.

The [README](../../README.md) is the quick tour. These pages go deeper, one topic per page.

## Pages

| Page | What it covers |
| --- | --- |
| [Architecture](Architecture.md) | Pipeline overview, projects, where each concern lives |
| [System Profiles](System-Profiles.md) | Contracts, data dictionaries and samples → one profile per system; masking |
| [Playbooks](Playbooks.md) | **Detailed**: anatomy of a domain playbook, detection scoring, qualifiers, derivation / conditional / validation rules, value maps, tests, lifecycle, process playbooks |
| [Mapping Generation](Mapping-Generation.md) | How two profiles plus the playbooks become a mapping spec: the five strategies, transformations, confidence, review, findings |
| [Worked Example: Volume ("rate") Mapping](Worked-Example-Volume-Mapping.md) | End-to-end trace of `annualCardVolume` → `MonthlyVolume`, `averageTicket`, `HighTicket` and the channel percentages, with the exact numbers the engine produces |
| [Replay and Validation](Replay-and-Validation.md) | Running a mapping over sample payloads, executing playbook validation rules, `--strict` |
| [AI Assist](AI-Assist.md) | What the AI sees, what it may do, caps, providers, privacy |
| [CLI Reference](CLI-Reference.md) | Every `mapwright` command with examples |
| [API Reference](API-Reference.md) | HTTP endpoints for playbooks, profiles, mappings, replay, AI, suggestions |
| [Web UI](Web-UI.md) | Pages of the Angular front end and what each does |
| [Deployment and Configuration](Deployment-and-Configuration.md) | Settings, sign-in, Docker, Render, repository layout |
| [Glossary](Glossary.md) | Terms used across the wiki |

## Reading order

1. New to MapWright: [Architecture](Architecture.md) → [Playbooks](Playbooks.md) → [Mapping Generation](Mapping-Generation.md) → [Worked Example](Worked-Example-Volume-Mapping.md).
2. Authoring playbooks: [Playbooks](Playbooks.md) sections 3–12, then [Replay and Validation](Replay-and-Validation.md) for the validation rules.
3. Integrating: [CLI Reference](CLI-Reference.md), [API Reference](API-Reference.md), [Deployment](Deployment-and-Configuration.md).

## Sample data referenced by the wiki

All worked examples use the shipped samples, so you can reproduce every number:

```bash
dotnet run --project src/MapWright.Cli -- map \
  samples/systems/sales-alpha/profile.json \
  samples/systems/uw-core/profile.json \
  --ai no --out /tmp/mapping.json
```

The committed reference output is `samples/mappings/sales-alpha__uw-core/generated-mapping.json`.
