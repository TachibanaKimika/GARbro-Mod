# Machine Protocol

GARbro CLI protocol version 1 is `garbro.cli/v1`. Negotiate it through
`capabilities`; do not infer it from the executable assembly version.

## Output channels

- stdout contains only the selected `--output` representation.
- stderr contains `--verbose` diagnostics.
- the process exit code classifies completion.

Use JSON fields and exit codes for decisions. Human `message` text may be
localized or revised.

## JSON mode

`--output json` writes exactly one envelope:

```json
{
  "schemaVersion": "garbro.cli/v1",
  "programVersion": "0.1.0.0",
  "operationId": "96f88f1be7d34a208a36031a7100ad15",
  "command": "probe",
  "status": "success",
  "data": {
    "kind": "archive",
    "tag": "YPF"
  },
  "durationMs": 742
}
```

Treat new optional fields as compatible within v1.

Errors keep the envelope and add `error`:

```json
{
  "schemaVersion": "garbro.cli/v1",
  "operationId": "4885cced36de4976ba97243082952cc9",
  "command": "probe",
  "status": "needs_input",
  "error": {
    "code": "resource_parameters_required",
    "message": "The resource requires format-specific parameters.",
    "details": {
      "resourceTag": "ZIP",
      "resourceType": "archive"
    }
  }
}
```

Branch on `status`, `error.code`, structured `details`, and the process exit
code.

## JSONL stdout mode

`--output jsonl` writes one complete v1 envelope per stdout line. All lines for
one invocation share one `operationId`. Commands can emit:

```text
start
archive
entry
file
format
result
summary
error
needs_input
```

Parse each line independently. The final line must be `summary`, `error`, or
`needs_input`; do not report success before seeing a terminal event.

For `archive list`, expect one `archive` event, zero or more `entry` events, and
a final `summary`. For extraction, file events are followed by a summary with:

```text
selected
planned
written
skipped
failed
bytesWritten
observedBytes
destination
```

`bytesWritten` counts committed final files. `observedBytes` is charged while
streams are processed and may be higher when a failed entry emitted bytes
before failure.

## Script JSONL is a different schema

`script extract --mode jsonl` writes message objects to the destination file.
Those rows do not contain `schemaVersion` or `operationId`. See
[script-text-modes.md](script-text-modes.md).

`--mode jsonl --output jsonl` therefore produces two JSONL streams:

- generated destination file: script message rows;
- stdout: CLI protocol event envelopes.

Never feed one into the parser for the other.

## Exit codes

| Code | Status | Meaning |
| ---: | --- | --- |
| 0 | `success` | Complete success. |
| 2 | `usage_error` | Invalid command or option syntax. |
| 3 | `invalid_input` | Invalid path, value, resource, safety limit, unsupported script mode, cancellation, or similar input problem. |
| 4 | `unrecognized` | No handler accepted the input. |
| 5 | `needs_input` | A password, key, scheme, or other interactive parameter is required. |
| 6 | `conflict` | Existing or colliding destinations are disallowed. |
| 7 | `partial_success` | Some selected items failed or were skipped. |
| 8 | `io_error` | Classified filesystem or stream failure. |
| 9 | `internal_error` | Unexpected exception crossed the command boundary. |

Ctrl+C returns status `canceled`, error code `operation_canceled`, and exit code
3 after the operation observes cancellation.

## Required-parameter failures

The CLI deliberately does not open WPF dialogs or read interactive console
answers. A handler requiring parameters returns exit 5 with available details
such as:

```text
resourceTag
resourceType
notice
source
```

Report them. Do not guess a password, key, title, or scheme, and keep secrets
out of command lines and logs.

## Reporting checklist

After a read-only command, report recognized kind/tag and relevant counts or
metadata. After a write, report:

```text
status
destination
written/planned/skipped/failed
bytesWritten
warnings
dryRun
```

Describe exit 7 as partial success, not success. State explicitly when dry-run
created no files.
