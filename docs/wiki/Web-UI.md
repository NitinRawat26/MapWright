# Web UI

`web/` is an Angular (Angular Material) application over the [API](API-Reference.md). Built, it is served by the API
at `/`.

```bash
cd web && npm ci && npm run build && npm test && cd ..     # build writes src/MapWright.Api/wwwroot
dotnet run --project src/MapWright.Api --urls http://localhost:5080   # UI at http://localhost:5080/
```

Development: run the API as above and `npm start` in `web/` (http://localhost:4200 with `/api`, `/health`, `/swagger`
proxied to 5080). Needs Node.js 22+.

## Identity

Enter your name in the toolbar before making changes; it is sent as `X-MapWright-User`. When the API requires sign-in,
the toolbar shows the identity from the sign-in proxy, or asks for an API key (kept for the browser tab, sent as
`X-Api-Key`). The UI has no OAuth login of its own. API errors are shown with their `detail` and `issues`.

## Pages

### Playbooks

Filter by status, search, add a playbook as a draft. Each version shows its concept, vocabulary and rules. Drafts are
edited as **YAML** (comments kept) and validated and tested before saving — the same checks as `mapwright playbook
validate|test`. Actions: submit for review, request changes, publish, retire, draft a new version; audit history;
side-by-side compare of any two versions. This is where a rate/volume variant (a new period term, a new derivation)
is authored — see [Playbooks §19](Playbooks.md#19-authoring-checklist).

### Profiles

Upload sample payloads and contracts to build a [profile](System-Profiles.md); browse fields, findings and inputs.
A **preset** picks the kind of system (JSON REST API, SOAP/XML service, XML file or batch, Field spec, Data
dictionary, PDF/Word spec, Samples only, Mixed/custom) and constrains the file picker, hints at Root and prefills a
description. **Run detection** shows which business concept the published playbooks recognise in each field
(optionally asking AI about the rest); the latest result is kept and flagged when stale. The AI option for reading
document text appears only for PDF/Word uploads.

### Mappings

Generate a mapping from two profiles; see coverage and confidence; filter/search rows (including an **AI** filter);
open a row to read *why* it was mapped — reasoning, evidence, transformation, risk, open question — then approve,
reject or override it; see review history; download Excel, CSV, HTML or PDF.

With an AI provider configured, the rows toolbar gains an **Ask AI** group: tick rows individually or toggle the
*high / medium / low* buttons to select a whole confidence band, then Ask AI sends just those rows to the provider.
Each answered row gains an **AI candidate** panel in its detail — provider, capped confidence, sources and reasoning —
which never replaces the playbook pairing by itself; *Use AI suggestion* applies it as a reviewer override.

For row M017 of the sample this shows: `ProcessingVolume.CardVolume`, `domain/processing-volume@1.0.0`,
`periodConversion`, `annual / 12`, `annual = $.processing.annualCardVolume`, 95 %, `autoAccepted`.

### Replay (tab on each mapping)

Upload source samples, pick the target profile, run; see each sample's passed/failed/skipped checks, the playbook
validation results and the produced target payload; optionally save the runs on the mapping.

### AI suggestions

The inbox of AI answers from detection ([AI Assist](AI-Assist.md#the-suggestions-inbox--playbook-drafts)). Approve
one (optionally changing the concept) to add the field name to a draft of the domain playbook, or reject it with a
comment. **Create if missing** (pre-ticked for concepts the AI proposed) drafts a new domain playbook or attribute
instead.
