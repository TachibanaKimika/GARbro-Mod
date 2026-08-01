# GARbro Machine CLI

`Onachi-GARbro.Cli.exe` is the stable, non-interactive command boundary for
automation and AI agents. It shares GARbro's `GameRes` catalog and
MEF-discovered format handlers, but it does not display WPF dialogs or read
answers from the console.

The machine protocol remains `garbro.cli/v1`. New commands and optional fields
extend version 1 without changing its envelope. Archive extraction manifests
are a separate UTF-8 JSONL file protocol named
`garbro.extraction-manifest/v1`.

## Installation and discovery

The Windows installer includes an optional `Add GARbro CLI to system PATH`
component. It is unchecked by default so installation does not silently change
the machine environment. When selected, it adds the installation directory to
the machine `PATH`; newly opened terminals can then resolve
`Onachi-GARbro.Cli.exe`.

Repository automation should prefer
`bin\Release\Onachi-GARbro.Cli.exe`, then `bin\Debug`, before falling back to
`Get-Command Onachi-GARbro.Cli.exe` and the normal `Program Files` installation
directories. The repo-local Codex skill implements this lookup at
`.codex/skills/garbro-cli/SKILL.md`.

The full package includes `garbro-cli-skill.zip`. In the GUI, open
`Preferences -> AI integration` and use `Save SKILL ZIP...` to save a copy to a
user-selected location. The archive has one top-level `garbro-cli` directory:

```text
garbro-cli/
  SKILL.md
  agents/openai.yaml
  references/command-reference.md
  references/script-text-modes.md
  references/machine-protocol.md
  references/extraction-safety.md
  references/large-library-ingest.md
  references/content-semanticization.md
```

Review or extract it, then place that directory under
`$CODEX_HOME\skills\garbro-cli`, or under
`%USERPROFILE%\.codex\skills\garbro-cli` when `CODEX_HOME` is not set. GARbro
does not download a skill or modify the Codex skill directory itself. Open a
new Codex task after extraction so its skill catalog can be reloaded.

## Quick start

From the repository root after a Debug build:

```powershell
$cli = ".\bin\Debug\Onachi-GARbro.Cli.exe"

& $cli capabilities --output json --non-interactive
& $cli probe "C:\game\data.xp3" --output json --non-interactive
& $cli archive schemes --filter "game title" --output jsonl --non-interactive
& $cli archive scheme-check "C:\game\data.xp3" `
    --scheme "Exact Scheme Name" `
    --output json --non-interactive
& $cli archive plan "C:\game\data.xp3" `
    --destination "C:\work\extract" `
    --scheme "Exact Scheme Name" `
    --duplicate-policy suffix-index `
    --output jsonl --non-interactive
& $cli archive extract "C:\game\data.xp3" `
    --destination "C:\work\extract" `
    --scheme "Exact Scheme Name" `
    --duplicate-policy suffix-index `
    --budget auto `
    --manifest "C:\work\extract.manifest.jsonl" `
    --checksum sha256 `
    --dry-run `
    --output jsonl --non-interactive
```

Use `--output json` for one bounded response. Use `--output jsonl` by default
for scheme catalogs, archive entries, plans, extraction runs, Hx scans, and
image batches. Add `--summary-only` to supported large-result commands when
only aggregate totals are needed. Standard output contains only protocol
objects; `--verbose` diagnostics go to standard error.

The CLI is always non-interactive. `--non-interactive` is accepted so callers
can state that requirement explicitly.

## Commands

| Command | Purpose |
| --- | --- |
| `capabilities` | Report protocol, command, format-count, optional-component, manifest, duplicate, resume, and safety capabilities. |
| `formats list [--kind all\|archive\|image\|audio\|script]` | List discovered format handlers. |
| `probe PATH [XP3_SCHEME_OPTIONS]` | Detect a resource; typed XP3 options can open protected archives without a GUI. |
| `archive list ARCHIVE [XP3_SCHEME_OPTIONS] [--summary-only]` | List metadata and stable zero-based entry indexes. |
| `archive plan ARCHIVE --destination DIR [SELECTION_OPTIONS]` | Resolve output paths, duplicate groups, existing conflicts, declared sizes, and finite recommended limits without writing. |
| `archive extract ARCHIVE --destination DIR [SELECTION_OPTIONS] [RESUME_OPTIONS]` | Safely extract the selected logical entries with deterministic duplicate and resume semantics. |
| `archive schemes [--tag XP3] [--filter TEXT]` | List XP3 schemes and game-title lookup mappings. |
| `archive scheme-info NAME` | Inspect one exact scheme and its lookup-name mappings. |
| `archive scheme-check ARCHIVE (--scheme NAME and/or --cx-dump-dir DIR) [--hx-names FILE]` | Force a scheme composition, open the index, and inspect the first 32 entries for recognizable header evidence. `--hx-names` alone is invalid. |
| `script extract PATH --mode MODE --destination DIR [--entry EXACT_NAME]` | Convert one physical script or one exact archive entry. |
| `image info IMAGE` | Report the selected image handler and metadata. |
| `image convert IMAGE --format TAG_OR_EXTENSION --destination DIR` | Convert one image with a writable handler. |
| `image convert-batch --source-root DIR --destination DIR --format FORMAT` | Convert a directory or UTF-8 text/JSONL source manifest in one initialized process. |
| `hxv4 schemes` | List installed Hx v4 schemes usable by archive-filtered generation. |
| `hxv4 hash VALUE --kind file\|path` | Calculate a native Hx v4 file-name or path hash. |
| `hxv4 generate --destination FILE [SOURCE_OPTIONS]` | Build an unfiltered `HxNames.lst` from loose sources, seeds, and KrkrDump logs. |
| `hxv4 generate-archive ARCHIVE --scheme NAME --destination FILE` | Scan game resources and retain candidates that occur in actual Hx indexes. |
| `hxv4 clean HXNAMES --deobfuscated-dir DIR --destination FILE` | Reduce a table to names observed in an extracted tree. |
| `hxv4 find-missing-voices --voice-dir DIR` | Report sequence-derived voice stems whose `.ogg` file is absent. |
| `hxv4 restore-structure DIR [--recursive] [--dry-run]` | Restore flattened underscore-separated directory components. |
| `hxv4 rename DIR --names HXNAMES [--dry-run]` | Rename hashed files and directories from a table. |
| `hxv4 krkrdump ARCHIVE --game-executable EXE --destination DIR` | Launch, collect, and optionally import KrkrDump runtime data. |
| `hxv4 krkrdump-import ARCHIVE --result-dir DIR` | Import an existing KrkrDump result without launching a game. |

Run `help` or `help COMMAND ACTION --output json` for the executable's
authoritative option list.

## XP3 scheme workflow

Protected XP3 handling is a typed CLI workflow rather than a general password
or arbitrary options-file binder:

1. Run `archive schemes --filter TEXT --output jsonl`. Scheme events describe
   `name`, `displayName`, `algorithmType`, `family`, `supportsHxNames`, and
   `source`; `game-map` events connect executable/title lookup names to exact
   scheme names.
2. Use `archive scheme-info NAME` when a candidate needs closer inspection.
3. Compose an explicit resolution with one or more of:
   - `--scheme NAME` for an exact known title or builtin alias such as
     `__NOCRYPT__`, `__YUZUCRYPT__`, or `__XOR-XX__`, where `XX` is exactly
     two hexadecimal digits;
   - `--hx-names FILE` after an Hx v4 or Cx/Hx scheme;
   - `--cx-dump-dir DIR` for an explicit KrkrDump result directory.
4. Run `archive scheme-check` before a broad read. It opens the index and
   inspects the first 32 entries for recognizable header evidence. `matched` is
   positive evidence. `mismatch` means all recognizable evidence failed;
   `mixed` means at least one match and at least one failure. Both return
   `xp3_scheme_check_failed`, with reason codes `sample_magic_mismatch` or
   `sample_magic_mixed`. `inconclusive` means the sample had no recognizable
   header evidence; it is not proof of correctness.
5. Reuse exactly the same options for `probe`, `archive list`, `archive plan`,
   and `archive extract`.

`--cx-dump-dir` supersedes `--scheme` for content decryption when both are
present; `--hx-names` is then applied last. The directory is treated as an
explicit strict result boundary. GARbro imports only the relevant KrkrDump log,
Cx table, and Cx order it consumes there, and records those artifacts' absolute
paths and SHA-256 hashes in `schemeResolution.artifacts`. `PathHash` and
`NameHash` records embedded in the selected logs are applied to the transient
scheme in memory. GARbro neither reads nor writes an `HxNames.lst` in that
directory automatically; pass any additional table explicitly with
`--hx-names`. Typed resolution for `scheme-check`, `plan`, and `--dry-run` does
not write back to the Cx result directory. Raw keys are never accepted as
command-line arguments. The optional suffix `|garbro-importer` is accepted only
as a compatibility modifier and is stripped before resolving the directory.
Cx directory or ancestor reparse points return `xp3_cx_dump_reparse_point`.
Malformed, incomplete, or oversized Cx logs/table/order data returns
`xp3_cx_dump_invalid`; an invalid or oversized explicit `--hx-names` file
returns `xp3_hx_names_invalid`.

Every effective resolution reports a stable identity and fingerprint. Archive
plans and manifests include that handler-options identity, so changing a
scheme, Hx table, or Cx artifact invalidates a later resume instead of silently
mixing incompatible output. Fingerprint version 2 binds the serialized material
of the effective `ICrypt` implementation plus SHA-256 snapshots of the exact
artifact bytes consumed by the importer. Only digests are exposed; keys and
other scheme material are never returned in machine output.

For `probe`, `archive list`, `archive plan`, and `archive extract`, when no
typed option is supplied and the XP3 recognizer chooses an installed scheme,
the result still records its canonical effective scheme with source
`auto_detected`. `archive scheme-check` is intentionally different: it requires
`--scheme` or `--cx-dump-dir`, otherwise it returns usage error
`xp3_scheme_required`. If an effective scheme lazily reads a TPM control block
while opening the archive, GARbro snapshots the exact 4,096 consumed bytes as
artifact kind `xp3_tpm_control_block` and recalculates the version-2 fingerprint
after scheme initialization. A changed TPM therefore invalidates resume. Case
variants of the same resolved scheme name or the same builtin alias (including
XOR hexadecimal case), plus the optional Cx compatibility suffix, normalize to
one semantic manifest identity. A title scheme versus a builtin alias, explicit
versus `auto_detected` resolution, and materially different scheme or artifact
bytes remain distinct.

See [krkrdump-xp3-assist.md](krkrdump-xp3-assist.md) for collection and import
details.

## Archive planning, selection, and duplicates

`archive list` and `archive plan` expose `entryIndex`, a stable zero-based
position in the opened archive. The selectors are:

```text
--entry GLOB        repeatable archive-name glob
--entry-index N     repeatable zero-based index
```

When both selector types are present they intersect. Index selection is useful
when an archive contains duplicate logical names that a name glob cannot
distinguish.

`archive plan` emits one `archive` event and one `entry` event with status
`planned` per selected entry in JSONL mode, then a terminal summary with
duplicate groups, destination collisions, existing conflicts, declared totals,
maximum depth, `recommendedLimits`, `ready`, and a `planFingerprint`. It creates
no destination and writes no output files.

The duplicate policy is explicit:

- `--duplicate-policy error` is the default. Case-insensitive normalized
  destination collisions make the plan not ready and extraction fails safely.
- `--duplicate-policy suffix-index` keeps the first occurrence and gives later
  occurrences deterministic names containing the stable archive index, for
  example `voice.__entry-000123.ogg`. If that name collides with another
  reserved archive-derived path, including an unselected occurrence, a
  deterministic `-01`, `-02`, and so on is added. The mapping is visible in the
  plan and manifest.

A file path cannot simultaneously serve as another output's parent directory.
Such hierarchy collisions, an existing file in an output's parent chain, a
destination root that is itself a file, and reparse-point destination
ancestors fail during planning before any write.

Run a plan before broad extraction. Use JSONL for per-entry review or
`--summary-only --output json` for bounded totals.

## Extraction, automatic budgets, and manifests

Archive extraction and script/image conversion commands accept `--overwrite
never|skip|replace`, `--dry-run`, and finite file/byte/depth limits. Hx v4
commands use their operation-specific options instead. Archive extraction
additionally accepts:

```text
--budget auto
--manifest FILE
--checksum none|sha256
--resume verify-size|verify-hash
--resume-manifest FILE
--summary-only
```

`--budget auto` uses the archive plan's selected count, declared sizes, and
path depth plus finite headroom. It does not disable limits and does not trust
metadata at write time: actual decompressed bytes are still charged while data
is produced. Explicit numeric limits can make the policy tighter for a
particular job.

`--dry-run` performs the same scheme resolution/index-open, selection, path,
duplicate, existing-output, and budget checks without creating the destination.
It does not run `archive scheme-check` content-magic sampling implicitly. For
large plans and runs use `--output jsonl`; use `--summary-only` when per-file
events would be too large. In non-`summary-only` JSON mode, archive
list/plan/extract responses with more than 1,000 items include warning code
`large_json_response` rather than silently presenting the materialized
collection as a bounded choice.

`--manifest FILE` writes UTF-8 JSONL using
`garbro.extraction-manifest/v1`. Unless `--checksum none` is explicit, a
manifested run computes SHA-256 for materialized outputs. The file contains:

- one `header` record with source path/length/time/SHA-256, handler tag and
  options identity, destination, selected count, duplicate policy, plan
  fingerprint, and checksum mode;
- initial-run `entry` records for handled logical entries, including index,
  logical name, stored and declared bytes, declared-size source, occurrence,
  resolved output path, and status; materialized records add `actualBytes` and
  optional `outputSha256`, while failed records add structured error code and
  message fields; when a fatal per-entry policy error stops the loop, every
  later unverified entry receives `not_attempted` with error code
  `aborted_after_error`; resumes append a record for each entry that reaches
  `written`, `repaired`, `skipped`, `failed`, or `not_attempted` again, while
  `verified_existing` entries retain their prior materialized record;
- on normal or partial completion, a `summary` record containing terminal
  status and counts. Cancellation, process termination, or an exception that
  escapes the extraction loop does not promise a manifest summary.

Resume requires the original manifest and the same source archive identity,
handler/scheme artifacts, destination, selectors, duplicate policy, and plan
fingerprint:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --scheme $scheme `
  --duplicate-policy suffix-index `
  --budget auto `
  --resume verify-hash `
  --resume-manifest $manifest `
  --output jsonl --non-interactive
```

`verify-size` compares an existing output with the prior `actualBytes`.
`verify-hash` also verifies SHA-256 and forces output checksums. A verified file
is reported as `verified_existing`, contributes to `bytesVerified`, and is not
rewritten. If verification fails without explicit `--overwrite replace`, the
command returns top-level conflict error code `resume_verification_failed`; it
is not an item status or manifest state. With replacement authorized, the
output is reported as `repaired`. Summary `written` includes all outputs
materialized in the run, while `repaired` is its subset that replaced failed
resume targets; do not add the counts. A resumed run appends new entry and
summary records when it reaches normal or partial completion, so the newest
entry record for an index is normally authoritative. A later `not_attempted`
audit record never erases an earlier materialized record when resume state is
folded. Prevalidated files after a fatal current-run entry are still counted as
`verifiedExisting`; only the remaining unverified entries are
`notAttempted`. For a completed non-dry run the logical counts close as:

```text
selected = written + verifiedExisting + skipped + failed + notAttempted
```

`repaired` remains a subset of `written` and is not added to that equation.

Crash-tail handling is narrow and append-safe. A valid final JSON record does
not need a trailing newline; resume inserts the missing boundary before its
first appended record. A malformed final record is ignored only when it is the
non-newline-terminated tail left by an interrupted writer. A non-dry resume
removes that tail immediately before append; malformed completed lines remain
`invalid_extraction_manifest`. Dry-run validation never mutates the manifest.
Manifest decoding is strict UTF-8. The manifest path and all existing
ancestors must not be reparse points. Before replacing or appending, GARbro
detaches the manifest pathname through a same-directory temporary file and
atomic replacement. Resume copies the existing bytes into that private file;
a fresh manifest starts it empty. A hard-linked peer therefore keeps its own
bytes and is never truncated or appended through the shared file identity.

## Declared and actual sizes

Archive metadata and final materialized output are different facts:

- `storedBytes` is the archive entry's stored size.
- `declaredBytes` is the best pre-write estimate, using nonzero unpacked size
  for packed entries and stored size otherwise.
- `declaredBytesSource` identifies that choice.
- `outputSizeKnown: false` and `materializedSizeMayDiffer: true` warn callers
  not to treat a plan as an exact output-size inventory.
- Per-file `actualBytes` exists after materialization or successful resume
  verification. Summary `bytesWritten` is always numeric and can be zero.
- `observedBytes` is charged while streams run and can exceed committed bytes
  when an entry fails after producing data.

Use declared values for planning, actual values and hashes for provenance, and
never present declared metadata as measured output.

## Batch image conversion

`image convert-batch` keeps catalog initialization in one process and preserves
relative directory structure while changing each final extension:

```powershell
& $cli image convert-batch `
  --source-root "C:\work\images" `
  --destination "C:\work\converted-png" `
  --format PNG `
  --recursive `
  --detect-by-signature `
  --include "event/**" `
  --budget auto `
  --resume verify-decode `
  --output jsonl --non-interactive
```

Selection can come from directory scanning or `--manifest FILE`. A manifest is
UTF-8 text with one relative path per line, or JSONL with `path` or
`sourcePath`. Every source must remain below `--source-root`; duplicate source
rows, reparse-point traversal, output collisions, and a destination equal to or
below the source root are rejected. The source root and destination ancestors
are also checked for reparse points. Planned outputs cannot land back in the
source tree or overwrite the input manifest, even with `--overwrite replace`.
`--recursive` enables subdirectories,
`--include` is repeatable and matches portable relative paths with `/`
separators. `--detect-by-signature` adds otherwise unknown extensions only when
GARbro recognizes an image signature.

`--resume verify-header` checks that an existing target has the requested
format. `verify-decode` fully decodes a nonempty target. Invalid targets are
repaired only with `--overwrite replace`. `--budget auto` remains finite and
actual encoded bytes are enforced. `estimatedOutputBytes` is a planning value;
`bytesWritten` is measured encoded output. Use `--summary-only` for a
count-only large batch result. If filtering and recognition select no images,
the command returns status `invalid_input`, exit code 3, and error code
`no_images_selected` without creating the destination, instead of reporting an
empty success.

For WebP resume verification, GARbro distinguishes a `VP8 ` lossy bitstream
from a `VP8L` lossless bitstream, so outputs from `WEBP/80` and
`WEBP/LOSSLESS` cannot be accepted across presets. The bitstream proves the
lossy/lossless class; it cannot prove that an arbitrary lossy file was encoded
with the numeric quality value 80.

Conversion decodes and re-encodes images. It does not classify characters,
infer scene semantics, run OCR, or create embeddings; see
[content-semanticization.md](../../.codex/skills/garbro-cli/references/content-semanticization.md)
for the downstream responsibility boundary.

## Script and Hx v4 workflows

Script `--mode` is required and is one of `filtered`, `raw`, `dump`, or
`jsonl`. It controls the generated file and is independent of stdout
`--output jsonl`. Discover handler `textModes` through `formats list --kind
script` or `probe`; unsupported modes return `script_mode_not_supported`.

Hx v4 generation accepts repeatable `--source-dir`, `--source-file`,
`--krkrdump-dir`, and `--seed` options. `hxv4 generate-archive` requires an
explicit installed scheme listed by `hxv4 schemes`; a transient scheme imported
by a separate KrkrDump CLI process is not available to it. For a Cx-dump-only
workflow, use unfiltered `hxv4 generate`, then validate/apply that table with
`archive scheme-check --cx-dump-dir ... --hx-names ...` in one invocation.
Archive generation emits throttled `progress` events in JSONL mode at phase
changes, completion boundaries, or roughly one-second intervals. A failure
returns `hxv4_generation_failed` with structured details including
`reasonCode`, requested and available schemes, archive/index/candidate/match
counts, and `recommendedActions`. In particular, `no_readable_index` suggests
scheme selection or KrkrDump, while `no_name_matches` suggests adding seeds or
inspecting candidate sources.

`hxv4 krkrdump` remains a visible runtime-assisted command: it can display a
UAC prompt, launches the selected game, and waits for that game to exit. The CLI
itself still reads no console answers. Use a fresh destination, or import an
existing result with `hxv4 krkrdump-import`.

## Common write options

Defaults are discoverable through `capabilities`. In protocol v1 they are:

| Setting | Default |
| --- | ---: |
| overwrite | `never` |
| max files | 10,000 |
| max total bytes | 4 GiB |
| max bytes per entry | 1 GiB |
| max path depth | 32 |

`never` rejects existing destinations. `skip` preserves them and normally makes
the result `partial_success`. `replace` must be explicit and commits through a
same-volume temporary file. `--budget auto` is an opt-in planned finite budget,
not an unlimited mode.

## JSON envelope and JSONL events

JSON mode writes exactly one envelope:

```json
{
  "schemaVersion": "garbro.cli/v1",
  "programVersion": "0.2.0.0",
  "operationId": "96f88f1be7d34a208a36031a7100ad15",
  "command": "probe",
  "status": "success",
  "data": { "kind": "archive", "tag": "YPF" },
  "durationMs": 742
}
```

Errors use the same envelope and add stable `error.code`; `error.details` is an
optional structured object when that failure has additional machine-readable
context. Callers should branch on `status`, `error.code`, details when present,
and the process exit code. Human-readable messages are not a stable parsing
surface. New optional fields and event names are compatible additions within
v1.

Only a terminal JSON or JSONL envelope can add
`warnings[{code,message,details?}]`. A warning changes neither terminal status
nor exit code. `large_json_response` details are exactly `itemCount`,
`threshold`, and `recommendedOutput: "jsonl"`; the human message also suggests
`--summary-only`.

Every JSONL line is an independent v1 envelope with the same `operationId`.
Large commands may emit `start`, `scheme`, `game-map`, `archive`, `entry`,
`file`, `image`, `missing_voice`, `progress`, and `result` events. The final
line is a terminal `summary`, `error`, or `needs_input` event. Do not report
success before consuming it. `--summary-only` suppresses supported per-item
events, not the terminal summary.

The extraction manifest is not a CLI stdout envelope: its rows use
`schemaVersion: garbro.extraction-manifest/v1` and `record:
header|entry|summary`, and do not share stdout's `operationId`.

## Exit codes

| Code | Status | Meaning |
| ---: | --- | --- |
| 0 | `success` | The complete command succeeded. |
| 2 | `usage_error` | Command or option syntax is invalid. |
| 3 | `invalid_input` | A path, option value, resource, safety limit, manifest, or requested mode is invalid. |
| 4 | `unrecognized` | No handler accepted the input. |
| 5 | `needs_input` | A handler still requires unsupported interactive parameters. |
| 6 | `conflict` | An existing or colliding destination is disallowed. |
| 7 | `partial_success` | Some selected items failed, were skipped, or were not attempted after a fatal item error. |
| 8 | `io_error` | A classified filesystem or stream error occurred. |
| 9 | `internal_error` | An unexpected exception crossed the command boundary. |

Ctrl+C returns `canceled` with exit code 3 after the current operation observes
the cancellation request.

## Responsibility boundary

GARbro owns resource recognition, scheme application, index and entry decoding,
path-safe extraction, script-handler text export, image decoding/encoding,
limits, hashes, and provenance records. It does not promise OCR, audio
transcription, translation, speaker/entity resolution, scene classification,
cross-asset semantic links, embeddings, or vector-database ingestion. Feed
GARbro's structured outputs and manifests into separate downstream tools for
those jobs, retaining source, entry index, handler/scheme fingerprint, output
path, actual byte count, and SHA-256 as provenance.

## Verification

Build and basic smoke:

```powershell
.\build.ps1 -Configuration Debug -NoPackage -NoVersionStamp -Smoke
```

Run the deterministic synthetic protocol and safety suite:

```powershell
.\tests\Cli\Invoke-CliTests.ps1 -Configuration Debug
```

After a GUI build, validate the ZIP layout, routing references, source/package
content equality, and settings-page save/replace behavior under a temporary
directory:

```powershell
.\tests\Installer\Invoke-CodexSkillPackageTests.ps1 -Configuration Debug
```

See [build-and-verify.md](build-and-verify.md) for the supported toolchain and
larger test matrix.
