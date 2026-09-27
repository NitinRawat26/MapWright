# AI Assist

AI in MapWright is an **optional, second-line, human-reviewed** helper. Playbooks always run first; AI is only
consulted about what they left unrecognised or unmapped, only when a caller asks, and everything it says is capped
below auto-accept and routed to review. It never publishes, never edits a published playbook and never sees
sensitive values.

Code: `src/MapWright.Ai/` — `IAiProvider`, `VertexAiProvider`, `OllamaProvider`, fallback chain,
`AiFieldAssistant` (decode fields), `AiMappingAssistant` (suggest sources for unmapped targets).

## Where AI can be invoked

| Entry point | Question asked | Output |
| --- | --- | --- |
| `mapwright playbook detect … --ai ask\|yes\|no` | "Which business concept does each *unrecognised* field hold?" | Suggestions in the detect report (`--out`) |
| `POST /api/profiles/{id}/detect` `{ "useAi": true }` | same | Suggestions filed in the **AI suggestions inbox** |
| `mapwright map … --ai ask\|yes\|no` | "Which source field(s) and transformation supply each *unmapped* target?" | Extra rows in the mapping, marked AI |
| `POST /api/mappings` `{ "useAi": true }` | same | Rows + an `aiPass` summary stored with the mapping |
| `POST /api/profiles` `useAi=true` (PDF/Word) | "Which fields does the free text of this spec describe?" | Profile fields with `aiExtracted` findings |

With `ask` the CLI prints *"Do you want to use AI to decode the remaining N field(s)? Masked field details are sent to
<provider>. [y/N]"* (to stderr when `--out` is absent so stdout stays pure JSON). With no provider configured
MapWright silently uses playbooks only (the API returns 400 for `useAi: true`).

## Providers and configuration

Providers are tried in order; the first configured one is used, the next on failure.

| Provider | Variables |
| --- | --- |
| Vertex AI (Gemini) — first | `MAPWRIGHT_VERTEX_PROJECT` (enables), `MAPWRIGHT_VERTEX_LOCATION` (default `global`), `MAPWRIGHT_VERTEX_MODEL` (default `gemini-3.5-flash`); credentials via Application Default Credentials (`GOOGLE_APPLICATION_CREDENTIALS`) |
| Ollama — fallback | `MAPWRIGHT_OLLAMA_URL` (enables, e.g. `http://localhost:11434`), `MAPWRIGHT_OLLAMA_MODEL` (default `qwen3`), `MAPWRIGHT_OLLAMA_CONTEXT_TOKENS` (default 16384) |
| Both | `MAPWRIGHT_AI_TIMEOUT_SECONDS` (default 180) |

`GET /api/ai` reports whether a provider is configured, its name and the confidence cap.

## What the AI sees

For each remaining field, **masked metadata only**:

* path, name, parents, children, data type, cardinality, description, value shapes;
* short code-like values (`CORP`, `5411`, `0.5`) — free text (names, e-mails, phones, addresses) is withheld;
* for sensitive fields: **no** values, samples or ranges at all;
* the domain playbooks' concept, attributes and `aiGuidance` text, so answers are phrased in playbook vocabulary
  (`Principal.Email`) or as a proposed new concept (`newConcept: Merchant.Mcc`).

For mapping suggestions, additionally every source field (marked `used: true/false`) and the list of allowed
transformation types.

The field-decoding instruction (verbatim from `AiFieldAssistant`):

> You help map merchant-acquiring data between systems. For each field in "fields", decide what business data it
> holds. Use the concepts defined by the playbooks in "concepts" when one fits and answer with "Concept.Attribute"
> (or just "Concept" for a collection or group). When none fits, leave "concept" empty and put a short
> "Concept.Attribute" name of your own in "newConcept". Follow each playbook's guidance. Values of sensitive fields
> are withheld; never guess or invent values. Give "confidence" from 0 to 100, one or two sentences of "reasoning"
> that cite the field name, parents, children or values you relied on, and a "question" for a reviewer when you are
> unsure. Only short code-like values are shown; free-text values are withheld. Return one suggestion per field path,
> using the exact path given.

## Guard rails applied to answers

| Rule | Effect |
| --- | --- |
| Confidence cap | `min(answer, maxConfidence)` where `maxConfidence` is the process playbook's `aiAssist` step value (**70** in the starter set) — always below `autoAcceptAt` 90 |
| Review | every AI row/suggestion is `needsReview`; the AI's `question` becomes the row's `openQuestion` |
| Provenance | `aiSuggestion` evidence naming provider and model; Mapped By shows `AI (<provider>/<model>)`; reports highlight the rows in purple; the UI tags them **AI** |
| Never overrides | AI rows are added only for targets still `unmapped`; playbook rows are never replaced |
| One answer per path | duplicate or unknown paths are dropped; unknown transformation types are rejected |
| Failure | a provider error leaves the deterministic result intact (CLI) or returns 502 and saves nothing (API) |

## The suggestions inbox → playbook drafts

AI answers from detection through the API land in `GET /api/suggestions?status=pending|approved|rejected`.

* `POST /api/suggestions/{id}/approve` `{ "concept": "Concept.Attribute", "comment": "…", "create": false }`
  adds the field name as a **vocabulary term** to a *draft* of that concept's domain playbook (next minor version, or
  the open draft). With `create: true`, a concept no published playbook defines becomes a new draft playbook
  `domain/<concept>` at 0.1.0, and a missing attribute is added to the concept's playbook draft.
* `POST /api/suggestions/{id}/reject` `{ "comment": "…" }`.
* Approval never publishes: the draft still goes through validate → test → in review → publish
  ([Playbooks §2](Playbooks.md#2-file-header-and-lifecycle)). Draft change and decision are one transaction.

This is how AI knowledge becomes deterministic: the next `map` run recognises the field through the playbook, with
full evidence, and no AI call is needed.

## Relationship to rate/volume mapping

In the [worked example](Worked-Example-Volume-Mapping.md) AI is not needed for volumes: every processing field is
recognised by `domain/processing-volume` and `domain/channel-mix`. AI would only be consulted for `HighTicket`
(no source) — and there it would correctly return an empty `sources` list, because no SalesAlpha field carries a high
ticket — and for `BusinessDescription`, `Phone` etc. that no playbook covers. Accepting such a suggestion means adding
the term to a playbook, not accepting the AI row as-is.
