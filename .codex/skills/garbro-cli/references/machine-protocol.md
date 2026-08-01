# Machine Protocol

GARbro CLI protocol version 1 is `garbro.cli/v1`. Negotiate it through
`capabilities`; do not infer it from the executable assembly version. New
commands, event names, and optional fields are compatible additions within v1.

The archive extraction manifest has its own file schema,
`garbro.extraction-manifest/v1`. Do not parse it as CLI stdout.

## Output channels

- stdout contains only the selected `--output` representation;
- stderr contains `--verbose` diagnostics;
- the process exit code classifies completion;
- paths supplied to `--manifest` contain file records, never stdout envelopes.

Use JSON fields and exit codes for decisions. Human `message` text may be
localized or revised.

## JSON mode

`--output json` writes exactly one envelope:

```json
{
  "schemaVersion": "garbro.cli/v1",
  "programVersion": "0.2.0.0",
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

Errors keep the envelope and add `error`:

```json
{
  "schemaVersion": "garbro.cli/v1",
  "operationId": "4885cced36de4976ba97243082952cc9",
  "command": "archive.scheme-check",
  "status": "invalid_input",
  "error": {
    "code": "xp3_scheme_check_failed",
    "message": "The selected XP3 scheme failed sampled content validation.",
    "details": {
      "archiveTag": "XP3",
      "contentValidation": {
        "status": "mismatch",
        "matchedEntries": 0
      }
    }
  }
}
```

Branch on `status`, `error.code`, structured `details` when present, and the
process exit code. Ignore unknown optional fields unless the task needs them.

Terminal JSON or JSONL envelopes can contain
`warnings[{code,message,details?}]`; nonterminal JSONL events do not. Warnings
change neither terminal status nor process exit code. Branch on each warning's
stable `code` and treat its human message as explanatory text. Archive
list/plan/extract add `large_json_response` only in non-`summary-only`
single-JSON mode when `itemCount > 1000`. Its structured details are
`{itemCount, threshold, recommendedOutput:"jsonl"}`; the suggestion to use
`--summary-only` appears only in the human message.

## JSONL stdout mode

`--output jsonl` writes one complete v1 envelope per stdout line. All lines for
one invocation share one `operationId`. Parse lines independently and tolerate
new event names. Current large workflows can emit:

```text
start
scheme
game-map
archive
entry
file
image
missing_voice
progress
result
summary
error
needs_input
```

The final line must be `summary`, `error`, or `needs_input`; do not report
success before seeing a terminal event. `--summary-only` suppresses per-entry
(including planned entries), per-file, or per-image records on commands that
support it, but keeps the terminal summary.

Typical streams:

- `archive schemes`: zero or more `scheme` and `game-map` events, then summary;
- `archive list`: one `archive`, zero or more `entry`, then summary;
- `archive plan`: one `archive`, zero or more `entry` events with status
  `planned`, then summary;
- `archive extract`: one `start`, zero or more `file`, then summary;
- `image convert-batch`: zero or more `image`, then summary;
- `hxv4 generate-archive`: throttled `progress` events, then summary or error.

Hx progress data can include `phase`, `percentage`, `message`, `elapsedMs`, and
phase-specific archive/entry/candidate counts. Events are emitted at phase
changes, completion boundaries, or approximately one-second intervals. They are
observability hints, not completion; the terminal event is authoritative.

## Summary and status interpretation

Archive extraction summaries can include:

```text
selected
planned
written
repaired
verifiedExisting
bytesVerified
skipped
failed
notAttempted
bytesWritten
observedBytes
destination
dryRun
duplicatePolicy
planFingerprint
policy
manifest
manifestWritten
```

`bytesWritten` sums bytes in committed final outputs. `bytesVerified` counts
existing bytes accepted by resume validation. `observedBytes` is charged while
streams are processed and may be higher when a failed entry emitted bytes
before failure. `verified_existing` means resume validation accepted an
existing output; `repaired` means an invalid existing output was replaced under
explicit overwrite authorization. Without that authorization, failed
verification returns top-level conflict error code
`resume_verification_failed`; it is not a file event status or manifest state.

`written` counts every output materialized during this run, including repaired
outputs. `repaired` is the subset of `written` that replaced failed resume
targets, for both archive extraction and image batches. Do not add those two
counts when computing a total.

`partial_success` is not success. Report its written, repaired, verified,
skipped, failed, and not-attempted counts separately. For a completed non-dry
archive run:

```text
selected = written + verifiedExisting + skipped + failed + notAttempted
```

A fatal per-entry policy error records later unverified entries as
`not_attempted` with `aborted_after_error`. Already prevalidated resume outputs
later in the plan remain `verified_existing`.

## Declared versus actual bytes

Archive entry and plan records deliberately distinguish metadata from measured
output:

```text
storedBytes
declaredBytes
declaredBytesSource
outputSizeKnown
materializedSizeMayDiffer
actualBytes
outputSha256
```

- `storedBytes` comes from the archive entry.
- `declaredBytes` uses nonzero unpacked size for packed entries and stored size
  otherwise.
- `declaredBytesSource` says which metadata source was used.
- Before materialization, `outputSizeKnown` is false and
  `materializedSizeMayDiffer` is true.
- `actualBytes` is measured only after materialization or successful resume
  verification.
- `outputSha256` is present when output hashing was enabled.

Use declared values only for planning and budgets. Use actual values and hashes
for provenance and downstream ingestion.

## Extraction manifest schema v1

`archive extract --manifest FILE` writes UTF-8 JSONL. Every nonempty line has:

```json
{"schemaVersion":"garbro.extraction-manifest/v1","record":"header"}
```

The records are append-friendly:

### Header

One new-run `header` contains:

```text
createdUtc
programVersion
sourceArchive.path/length/lastWriteTimeUtc/lastWriteTimeUtcTicks/sha256
handler.tag
handler.optionsIdentity
destination
archiveEntryCount
selected
duplicatePolicy
planFingerprint
outputChecksum
```

`handler.optionsIdentity` includes the canonical effective XP3 scheme identity
and artifact fingerprint for explicit or auto-detected scheme resolution. A
lazy TPM control block is snapshotted after scheme initialization and included
in that fingerprint. Equivalent option spellings normalize to semantic
identity; changed scheme material or artifact bytes do not.

### Entry

Each entry record contains:

```text
recordedUtc
entryIndex
entryName
entryType
offset
storedBytes
declaredBytes
declaredBytesSource
outputSizeKnown
materializedSizeMayDiffer
occurrence
groupSize
outputRelativePath
status
actualBytes          on written/repaired records
outputSha256         on written/repaired records when SHA-256 is enabled
error.code           on failed records
error.message        on failed records
error.code/message   on not_attempted records (`aborted_after_error`)
```

The stable `entryIndex`, occurrence/group information, and resolved output path
make duplicate logical names unambiguous.

### Summary

Each run that reaches normal or partial completion appends a `summary`
containing `recordedUtc`, terminal `status`, and a `counts` object. A resume
appends records for entries written, repaired, skipped, or failed plus a new
summary. It also records each unverified entry left after a fatal per-entry
error as `not_attempted`. A `verified_existing` output keeps its prior entry
record instead of duplicating it. Cancellation, abrupt process termination, or
an exception that escapes the extraction loop does not promise a manifest
summary. Consumers should treat the newest entry record for an index as current,
except that `not_attempted` is an audit record and must not erase an earlier
materialized state for resume verification.

A valid final record may omit its newline; resume inserts a boundary before
append. The loader tolerates only one malformed, non-newline-terminated final
record as an interrupted-write tail. A non-dry resume truncates that tail just
before append, while dry-run does not modify it. Malformed newline-terminated
records remain invalid.

Manifest text is strict UTF-8. The target and existing ancestors must not be
reparse points. A replace or resume append first detaches an existing hard link
through a same-directory temporary file and atomic replacement. Resume copies
the prior manifest bytes; a fresh manifest starts empty. Any linked peer is
preserved.

The loader validates the source archive identity, handler/options identity,
destination, selected count, duplicate policy, plan fingerprint, and each prior
entry's name/path mapping. Do not edit these fields to force a resume. Start a
new deliberate plan when they no longer match.

Unless `--checksum none` is explicit, specifying a manifest selects SHA-256.
`--resume verify-hash` always requires and produces SHA-256. `verify-size`
compares existing length to prior `actualBytes`; `verify-hash` additionally
compares the hash.

## Script JSONL is a different schema

`script extract --mode jsonl` writes message objects to the destination file.
Those rows do not contain `schemaVersion` or `operationId`. See
[script-text-modes.md](script-text-modes.md).

One command can therefore involve three distinct JSONL streams:

- generated script file: handler-defined message rows;
- stdout `--output jsonl`: `garbro.cli/v1` envelopes;
- extraction `--manifest`: `garbro.extraction-manifest/v1` records.

The batch-image `--manifest` is different again: it is an input list of source
paths. Never feed one schema to another parser.

## Exit codes

| Code | Status | Meaning |
| ---: | --- | --- |
| 0 | `success` | Complete success. |
| 2 | `usage_error` | Invalid command or option syntax. |
| 3 | `invalid_input` | Invalid path, value, resource, safety limit, manifest, unsupported script mode, cancellation, or similar input problem. |
| 4 | `unrecognized` | No handler accepted the input. |
| 5 | `needs_input` | A password, key, scheme, or other unsupported interactive parameter is required. |
| 6 | `conflict` | Existing or colliding destinations are disallowed. |
| 7 | `partial_success` | Some selected items failed, were skipped, or were not attempted after a fatal item error. |
| 8 | `io_error` | Classified filesystem or stream failure. |
| 9 | `internal_error` | Unexpected exception crossed the command boundary. |

Ctrl+C returns status `canceled`, error code `operation_canceled`, and exit code
3 after the operation observes cancellation.

## Structured Hx generation failures

`hxv4 generate-archive` failures use error code `hxv4_generation_failed` and
structured details such as:

```text
reasonCode
requestedScheme
schemeSelection
autoDetectionScope
indexArchivesTried
readableIndexCount
scannedEntryCount
candidateCount
pathMatches
nameMatches
availableSchemes
recommendedActions
```

Preserve `reasonCode`. `no_readable_index` normally recommends selecting a
scheme or running KrkrDump. `no_name_matches` normally recommends adding seeds
or inspecting name sources. Do not reduce either case to a generic scan error.

## Required-parameter failures

The CLI deliberately does not open WPF dialogs or read interactive console
answers. A handler requiring parameters without a typed provider returns exit 5
with details such as `resourceTag`, `resourceType`, `notice`, and `source`.

Report them. Do not guess a password, key, title, or scheme, and keep secrets
out of command lines and logs. XP3 is the explicit exception where the typed
`--scheme`, `--hx-names`, and `--cx-dump-dir` options are available.
