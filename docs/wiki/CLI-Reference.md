# CLI Reference

`mapwright` (`src/MapWright.Cli`) exposes the whole engine on files. Run it from the repository with
`dotnet run --project src/MapWright.Cli -- <command> …`. `mapwright --help` prints the usage below.

Exit codes: `0` success · `1` invalid spec / input / failed strict replay · `2` usage error.

## Commands

```
mapwright validate <spec.json>
mapwright render <spec.json> [--out <dir>] [--format <list>]
mapwright profile <input|dir>... --system <name> [--version <v>] [--description <text>]
                  [--root <name>] [--out <profile.json>] [--no-values]
mapwright playbook validate <playbook|dir>...
mapwright playbook test <playbook|dir>...
mapwright playbook convert <playbook|dir>... --to yaml|json [--out <dir>]
mapwright playbook detect <profile.json> [--playbooks <playbook|dir>]... [--ai ask|yes|no] [--out <report.json>]
mapwright map <source-profile.json> <target-profile.json> [--playbooks <playbook|dir>]...
                  [--ai ask|yes|no] [--id <id>] [--title <text>] [--out <mapping.json>]
mapwright replay <mapping.json> <sample|dir>... --target <target-profile.json>
                  [--playbooks <playbook|dir>]... [--out <dir>] [--xml-namespace <uri>] [--mask] [--record] [--strict]
```

### `validate`

Checks a mapping spec against `MappingSpecValidator` ([Mapping Generation §11](Mapping-Generation.md#11-spec-validation)).

### `render`

Writes the sign-off documents from a spec. `--format` is a comma-separated subset of `xlsx,html,pdf,csv`
(default all); `--out` defaults to the spec's directory.

### `profile`

Builds a [system profile](System-Profiles.md) from files and directories (`*.json`, `*.xml`, `*.xsd`, `*.wsdl`,
`*.csv`, `*.xlsx`, `*.pdf`, `*.docx`). JSON/XML files are contracts when they are a schema, OpenAPI or WSDL document,
samples otherwise. All inputs of one system must share a format. `--root` picks the XSD root element, WSDL operation
or OpenAPI `operationId` / `"POST /path"` / schema name. `--no-values` stores no values. Without `--out` the profile
goes to stdout.

### `playbook validate`

Structural and referential validation of playbook files ([Playbooks §16](Playbooks.md#16-validation-of-the-playbook-itself-pb0xx-codes)):
required header, unique ids, known statuses/kinds, attributes referenced by rules exist, expressions parse and use
only declared inputs, value-map codes are unique, process `uses` resolve, ….

### `playbook test`

Validates, then runs every playbook's `tests` (detection cases) and every derivation's `examples`
([Playbooks §15](Playbooks.md#15-tests)). Fails when an expected concept/qualifier is not detected, a `reject`
case is detected, or an example expression does not produce the expected value — e.g. `annual / 12` with
`annual: 3000000` must give `250000`.

### `playbook convert`

Rewrites playbooks between YAML and JSON (`--to yaml|json`), next to the originals or into `--out`.

### `playbook detect`

Runs the domain playbooks over each field of a profile and prints concept, attribute, qualifiers, score, evidence
and questions. `--playbooks` defaults to `./playbooks`. `--ai` controls the optional [AI](AI-Assist.md) pass over
unrecognised fields (`ask` prompts, default). `--out` writes matches, AI suggestions and remaining fields as JSON.

### `map`

Generates a mapping spec ([Mapping Generation](Mapping-Generation.md)) from two profiles. `--ai` offers AI for the
targets still unmapped after the playbooks. `--id`/`--title` default to the system names
(`sales-alpha__uw-core`). Without `--out` the spec goes to stdout and the AI prompt to stderr.

### `replay`

Runs source samples through a mapping ([Replay and Validation](Replay-and-Validation.md)). `--target` is required
(field order, types, formats). `--out` defaults to `replay/` next to the mapping. `--record` appends validation runs
to the mapping file; `--strict` exits 1 on any failing check; `--mask` masks sensitive values in written payloads.

## Environment variables (AI)

`MAPWRIGHT_VERTEX_PROJECT`, `MAPWRIGHT_VERTEX_LOCATION`, `MAPWRIGHT_VERTEX_MODEL`, `MAPWRIGHT_OLLAMA_URL`,
`MAPWRIGHT_OLLAMA_MODEL`, `MAPWRIGHT_OLLAMA_CONTEXT_TOKENS`, `MAPWRIGHT_AI_TIMEOUT_SECONDS` — see
[AI Assist](AI-Assist.md#providers-and-configuration). With none set every command runs playbooks only.

## Typical session

```bash
CLI="dotnet run --project src/MapWright.Cli --"

# 1. profile both systems
$CLI profile samples/systems/sales-alpha/samples samples/systems/sales-alpha/contracts \
     --system "SalesAlpha CRM" --root submitApplication --out out/sales-alpha.profile.json
$CLI profile samples/systems/uw-core/samples samples/systems/uw-core/contracts/uw-core-intake.xsd \
     --system "UW Core" --out out/uw-core.profile.json

# 2. check the playbooks
$CLI playbook validate playbooks
$CLI playbook test playbooks
$CLI playbook detect out/sales-alpha.profile.json --ai no

# 3. generate, validate, render
$CLI map out/sales-alpha.profile.json out/uw-core.profile.json --ai no --out out/mapping.json
$CLI validate out/mapping.json
$CLI render out/mapping.json --out out/ --format xlsx,html

# 4. prove it on data
$CLI replay out/mapping.json samples/systems/sales-alpha/samples --target out/uw-core.profile.json \
     --xml-namespace urn:uwcore:intake:4.2 --out out/replay --record --strict
```
