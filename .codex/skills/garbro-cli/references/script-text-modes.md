# Script Text Modes

Read this reference before every `script extract`. The selected mode changes the
contents and filename of the generated file; it does not change the CLI stdout
protocol.

## Do not confuse mode with output

These options are independent:

```text
--mode filtered|raw|dump|jsonl
--output json|jsonl|text
```

- `--mode jsonl` creates a `.jsonl` script export containing translatable
  messages.
- `--output jsonl` streams CLI progress/result envelopes to stdout.

For example, this creates a structured script file while returning one bounded
JSON result envelope:

```powershell
& $cli script extract $script `
  --mode jsonl `
  --destination $destination `
  --output json `
  --non-interactive
```

Using both JSONL options is valid: the destination file contains message rows,
while stdout contains protocol events. Parse them with different schemas.

## Choose a mode

| Mode | Choose it for | Typical contents | It does not promise | Filename |
| --- | --- | --- | --- | --- |
| `filtered` | Reading, review, quick translation | Displayed dialogue, narration, choices, and selected names | Complete control flow, internal strings, or source fidelity | `<base>.txt` |
| `raw` | Seeing more decoded context | Decoded source-like text or a less-filtered string stream | Original bytes, recompilable source, or identical semantics across formats | `<base>.raw.txt` |
| `dump` | Reverse engineering and handler diagnosis | Disassembly, opcodes, offsets, line numbers, object data, metadata, or internal state | A clean translation corpus or engine source reconstruction | `<base>.dump.txt` |
| `jsonl` | Translation tools and structured regression checks | One message object per line with optional speaker and voice metadata | All commands, bytecode, or lossless round-trip data | `<base>.jsonl` |

Use `filtered` when the user only asks for readable text. Use `jsonl` when the
consumer needs message boundaries, speakers, or voice identifiers. Use `raw`
when filtered output appears incomplete or surrounding decoded context matters.
Use `dump` only for diagnosis, reverse engineering, or explicit requests for
low-level detail.

The CLI has no `both` mode. To obtain filtered and raw output, invoke
`script extract` twice with separate modes.

## Discover supported modes

Do not assume every handler supports all four modes:

```powershell
$formats = & $cli formats list `
  --kind script --output json --non-interactive |
  ConvertFrom-Json

$probe = & $cli probe $script `
  --output json --non-interactive |
  ConvertFrom-Json
```

Read `textModes` from the selected handler. Examples in the current catalog
include:

- filtered-only legacy/simple handlers;
- CMVS scripts with `filtered` and `raw`;
- System-NNN SPT with `filtered`, `raw`, and `dump`;
- Whale scripts with `filtered`, `raw`, and `jsonl`;
- KiriKiri, BGI, Majiro, Silky's, Softpal, and AdvHD handlers with all four.

The runtime discovery response is authoritative. If the requested mode is not
listed, the CLI returns exit code 3, `invalid_input`, with error code
`script_mode_not_supported` and details:

```json
{
  "formatTag": "PS3/CMVS",
  "requestedMode": "jsonl",
  "availableModes": ["filtered", "raw"]
}
```

Report the available modes instead of silently falling back.

## JSONL script-file schema

The generated `.jsonl` file is UTF-8 without a BOM. Each non-empty line is one
independent JSON object representing one translatable message:

```json
{"name":"Character","voice":"voice\\sample_0001.ogg","message":"Message text"}
```

Fields:

- `message`: required readable dialogue, narration, choice, or message body.
- `name`: optional single speaker/name associated with the message.
- `names`: optional array when multiple pending names genuinely apply.
- `voice`: optional voice asset path or identifier when the format provides an
  unambiguous association.

Do not require optional fields. Do not infer missing names or voices. Preserve
file order; handlers emit messages in their best approximation of display
order. Empty messages are omitted.

This schema belongs to the generated file. CLI stdout envelopes instead use
`schemaVersion`, `operationId`, `command`, `status`, `event`, `data`, and
`error`; see [machine-protocol.md](machine-protocol.md).

## KiriKiri details

The KiriKiri handler recognizes plain `.ks`, supported scrambled `.txt`, and
PSB-backed `.scn` scenarios.

For text KAG scripts:

- `filtered` removes commands and keeps readable names, dialogue, narration,
  and recognized choices.
- `raw` writes decoded KAG text, including commands and surrounding source
  context.
- `dump` writes the decoded source with diagnostic line numbers.
- `jsonl` emits message objects derived from recognized KAG name/message/voice
  relationships.

For PSB `.scn` scenarios:

- `filtered` emits the filtered scenario text stream.
- `raw` emits a less-filtered decoded scenario string stream, not KAG source.
- `dump` emits decoded PSB object data and diagnostics such as scenes, texts,
  jumps, post-eval data, compiled lines, message-time environment snapshots,
  and full voice descriptor arrays.
- `jsonl` emits messages with the directly associated PSB speaker and voice
  identifier when available.

The PSB dump is diagnostic decoded object data; it is not reconstructed KAG and
is not intended for direct translation import.

## Physical files and archive entries

Export one physical file:

```powershell
& $cli script extract $script `
  --mode filtered `
  --destination $destination `
  --overwrite never `
  --output json `
  --non-interactive
```

Export one exact archive entry:

```powershell
& $cli archive list $archive --output jsonl --non-interactive

& $cli script extract $archive `
  --entry "scenario\start.ks" `
  --mode jsonl `
  --destination $destination `
  --overwrite never `
  --output json `
  --non-interactive
```

Archive-entry selection is exact and case-aware first; a unique
case-insensitive match is accepted. Multiple case-insensitive matches return
`ambiguous_entry`. Missing entries return `entry_not_found`.

## Write behavior

Use `--dry-run` to validate recognition, mode support, output path, safety
limits, and conflicts without creating the destination. `--overwrite never` is
the default. `skip` returns `partial_success` when the output exists. `replace`
must be explicit and uses GARbro's atomic writer.

On success, read these machine-result fields:

```text
sourcePath
entry
formatTag
mode
destination
dryRun
status
bytesWritten
```

For dry-run, `status` is `planned` and no file is created. For a committed
write, `status` is `written`.
