# Command Reference

Use this reference to choose syntax. Use `--output json` for bounded responses
and `--output jsonl` for large lists, plans, batches, or event streams. Add
`--summary-only` when a supported large-result command should suppress per-item
events. Pass `--non-interactive` explicitly in agent workflows.

## Global syntax

```text
Onachi-GARbro.Cli.exe COMMAND [ACTION] [ARGUMENTS] [OPTIONS]
```

Common options:

```text
--output json|jsonl|text
--verbose
--non-interactive
--help
```

`json` is the default. `text` is for humans and is not a stable parsing
surface. `--verbose` writes diagnostics to stderr without contaminating machine
stdout.

## Discovery and read-only commands

| Task | Command |
| --- | --- |
| Negotiate protocol and defaults | `capabilities` |
| List handlers | `formats list [--kind all\|archive\|image\|audio\|script]` |
| Recognize one file | `probe PATH [XP3_SCHEME_OPTIONS]` |
| List archive entries | `archive list ARCHIVE [XP3_SCHEME_OPTIONS] [--summary-only]` |
| Plan archive outputs | `archive plan ARCHIVE --destination DIR [SELECTION_OPTIONS]` |
| List XP3 schemes/title mappings | `archive schemes [--tag XP3] [--filter TEXT]` |
| Inspect one scheme | `archive scheme-info NAME` |
| Validate one scheme composition | `archive scheme-check ARCHIVE (--scheme NAME and/or --cx-dump-dir DIR) [--hx-names FILE]`; the names table alone is invalid. |
| Inspect one image | `image info IMAGE` |

Examples:

```powershell
& $cli capabilities --output json --non-interactive
& $cli formats list --kind script --output jsonl --non-interactive
& $cli probe $path --output json --non-interactive
& $cli archive list $archive --output jsonl --non-interactive
& $cli image info $image --output json --non-interactive
```

For script handlers, `formats list --kind script` and `probe` expose
`textModes`; configurable handlers also expose `defaultTextMode`. Treat runtime
discovery as authoritative.

## Typed XP3 schemes

The same XP3 scheme options are accepted by `probe`, `archive list`, `archive
plan`, `archive extract`, and `archive scheme-check`:

```text
--scheme NAME
--hx-names FILE
--cx-dump-dir DIR
```

- `--scheme` resolves an exact case-insensitive scheme name or builtin alias
  `__NOCRYPT__`, `__YUZUCRYPT__`, or `__XOR-XX__`, where `XX` is exactly two
  hexadecimal digits.
- `--cx-dump-dir` imports an explicit strict KrkrDump/Cx result directory and
  supersedes `--scheme` for content decryption. `DIR|garbro-importer` is the
  only accepted compatibility modifier. Logged name hashes are applied in
  memory; `HxNames.lst` is neither loaded nor written there automatically, and
  typed checks, plans, and dry runs do not write back to the directory.
- `--hx-names` requires an effective Hx v4/Cx-Hx scheme and is applied last.

Discover before guessing:

```powershell
& $cli archive schemes --filter $title --output jsonl --non-interactive
& $cli archive scheme-info $scheme --output json --non-interactive
& $cli archive scheme-check $archive `
  --scheme $scheme `
  --hx-names $names `
  --output json --non-interactive
```

`archive schemes` emits `scheme` and `game-map` events in JSONL mode. Scheme
check inspects the first 32 entries for recognizable header evidence after
opening the index. Treat `contentValidation.status: matched` as evidence and
`inconclusive` as no proof. Both `mismatch` and `mixed` return
`xp3_scheme_check_failed`; their reason codes are `sample_magic_mismatch` and
`sample_magic_mixed`.

Machine results include `schemeResolution.identity`, `fingerprint`, source
chain, effective/base scheme details, and SHA-256 artifact records. Reuse the
exact same options through plan and extraction. Fingerprint version 2 commits
to the effective serialized scheme material and the exact imported artifact
snapshots while exposing digests rather than raw keys. Strict Cx import accepts
at most 128 logs (16 MiB each, 64 MiB cumulative), a 64 MiB Hx names file, a
64 KiB order file, and an exact 4,096-byte table. Cx path reparse points return
`xp3_cx_dump_reparse_point`; malformed or oversized Cx material returns
`xp3_cx_dump_invalid`; invalid or oversized explicit Hx names return
`xp3_hx_names_invalid`.

For probe/list/plan/extract, recognition-selected installed XP3 schemes without
typed options are captured as `auto_detected`; `scheme-check` still requires
`--scheme` or `--cx-dump-dir`. A lazily consumed 4,096-byte TPM control block is
snapshotted as `xp3_tpm_control_block` after archive initialization and
participates in the version-2 fingerprint. Case variants of the same resolved
scheme name or the same builtin alias (including XOR hexadecimal case), plus the
optional Cx `|garbro-importer` suffix, normalize to one semantic manifest
identity. Title-versus-builtin and explicit-versus-auto resolutions remain
distinct.

## Archive plan and extraction

Plan first:

```text
archive plan ARCHIVE
  --destination DIR
  [--entry GLOB]...
  [--entry-index N]...
  [--duplicate-policy error|suffix-index]
  [--summary-only]
  [XP3_SCHEME_OPTIONS]
```

`--entry-index` selects the stable zero-based archive position. `--entry` and
`--entry-index` intersect when both are present. The default duplicate policy
is `error`. `suffix-index` deterministically maps later duplicate occurrences
to names such as `voice.__entry-000123.ogg`.

```powershell
& $cli archive plan $archive `
  --destination $destination `
  --entry "scenario\*.ks" `
  --duplicate-policy suffix-index `
  --output jsonl --non-interactive
```

The terminal summary includes duplicate and collision counts, declared totals,
maximum depth, `recommendedLimits`, `fitsDefaultLimits`, `ready`, and a
`planFingerprint`.

Extract with the same selectors and duplicate policy:

```text
archive extract ARCHIVE
  --destination DIR
  [--entry GLOB]...
  [--entry-index N]...
  [--duplicate-policy error|suffix-index]
  [--budget auto]
  [--manifest FILE]
  [--checksum none|sha256]
  [--resume verify-size|verify-hash]
  [--resume-manifest FILE]
  [--summary-only]
  [XP3_SCHEME_OPTIONS]
  [WRITE_OPTIONS]
```

For large jobs:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --manifest $manifest `
  --checksum sha256 `
  --dry-run `
  --output jsonl --non-interactive
```

`--budget auto` applies finite limits recommended by the plan. A manifested run
defaults to SHA-256 unless `--checksum none` is explicit. `verify-hash` forces
SHA-256. Resume requires either `--resume-manifest FILE` or the same path in
`--manifest FILE`. Resume preserves a valid unterminated final JSONL record and
repairs only an interrupted, malformed non-terminated tail before append:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --resume verify-hash `
  --resume-manifest $manifest `
  --output jsonl --non-interactive
```

Read [extraction-safety.md](extraction-safety.md) and
[large-library-ingest.md](large-library-ingest.md) before removing `--dry-run`.

## Script export

Physical script:

```text
script extract SCRIPT --mode MODE --destination DIR [WRITE_OPTIONS]
```

One script inside an archive:

```text
script extract ARCHIVE --entry EXACT_NAME
  --mode MODE --destination DIR [WRITE_OPTIONS]
```

`--mode` is required and is one of `filtered`, `raw`, `dump`, or `jsonl`.
Unlike archive extraction, script `--entry` is one exact archive entry name,
not a glob and not repeatable. Use `archive list --output jsonl` to obtain it.

Generated names preserve entry subdirectories and replace the final extension:

| Mode | Example input | Output |
| --- | --- | --- |
| `filtered` | `scenario\start.ks` | `scenario\start.txt` |
| `raw` | `scenario\start.ks` | `scenario\start.raw.txt` |
| `dump` | `scenario\start.ks` | `scenario\start.dump.txt` |
| `jsonl` | `scenario\start.ks` | `scenario\start.jsonl` |

Read [script-text-modes.md](script-text-modes.md) before choosing a mode and
[content-semanticization.md](content-semanticization.md) before treating an
export as a semantically complete corpus.

## Image conversion

One image:

```text
image convert IMAGE --format TAG_OR_EXTENSION --destination DIR [WRITE_OPTIONS]
```

`--format` accepts a writable handler tag such as `PNG`, `WEBP/80`, or
`WEBP/LOSSLESS`, or an extension whose selected handler advertises
`canWrite: true`.

Directory or source manifest:

```text
image convert-batch
  --source-root DIR
  --destination DIR
  --format FORMAT
  [--manifest FILE]
  [--recursive]
  [--detect-by-signature]
  [--include GLOB]...
  [--resume verify-header|verify-decode]
  [--budget auto]
  [--summary-only]
  [WRITE_OPTIONS]
```

```powershell
& $cli image convert-batch `
  --source-root $images `
  --destination $webp `
  --format WEBP/80 `
  --recursive `
  --detect-by-signature `
  --budget auto `
  --output jsonl --non-interactive
```

The batch `--manifest` is an input source list: UTF-8 plain-text relative paths
or JSONL rows with `path` or `sourcePath`. JSONL paths may be relative or rooted
but must resolve below the source root. It is unrelated to the archive
extraction provenance manifest. The destination must be outside the source
root; relative structure is preserved, reparse-point traversal and duplicate
sources are rejected, and output collisions fail. An output may not land back
inside the source root or overwrite the input manifest, including under
`--overwrite replace`. Repeatable `--include` globs
match portable relative paths with `/` separators. `verify-header` checks the
target format; `verify-decode` fully decodes a nonempty output. Repair requires
`--overwrite replace`. No selected images returns `invalid_input`/exit 3 with
`no_images_selected` and does not create the destination.

For WebP, resume validation distinguishes `VP8 ` lossy from `VP8L` lossless,
preventing cross-preset acceptance between `WEBP/80` and `WEBP/LOSSLESS`.
The container does not prove the exact numeric quality used for an arbitrary
lossy file.

## Common write options

```text
--overwrite never|skip|replace
--dry-run
--max-files N
--max-total-bytes N
--max-entry-bytes N
--max-depth N
```

`never` is the default. Explicit limits remain hard budgets; archive and batch
auto budgets are finite recommendations, not bypasses. Actual decompressed or
encoded bytes are charged while streams execute.

## Hx v4 names and KrkrDump

Discovery and hashing:

```text
hxv4 schemes
hxv4 hash VALUE --kind file|path
```

Generate from loose sources, explicit files, logs, and existing tables:

```text
hxv4 generate --destination HxNames.lst
  [--source-dir DIR]...
  [--source-file FILE]...
  [--krkrdump-dir DIR]...
  [--seed HXNAMES]...
  [--max-files N]
  [--include-garbro-common]
```

Generate only mappings found in real Hx indexes:

```text
hxv4 generate-archive ARCHIVE --scheme NAME --destination HxNames.lst
  [--seed HXNAMES]...
```

Use `hxv4 schemes` first; `generate-archive` accepts only a listed installed Hx
scheme. A scheme imported by a separate KrkrDump CLI process is transient. For
a Cx-dump-only workflow, use unfiltered `hxv4 generate`, then pass its table to
`archive scheme-check --cx-dump-dir DIR --hx-names FILE`. In JSONL mode,
archive generation emits throttled `progress` events with phase, percentage,
message, elapsed time, and phase details. On failure, inspect
`error.details.reasonCode`, counts, `availableSchemes`, and
`recommendedActions`.

Clean and apply tables:

```text
hxv4 clean HXNAMES --deobfuscated-dir DIR --destination CLEAN_HXNAMES
hxv4 find-missing-voices --voice-dir DIR [--voice-dir DIR]...
hxv4 restore-structure DIR [--recursive] [--dry-run]
hxv4 rename DIR --names HXNAMES [--dry-run]
```

Always inspect dry-run before restore or rename. Paths stay below the requested
root. File collisions receive `_1`, `_2`, and so on; directory collisions merge
with identical-file deduplication and unique names for non-identical conflicts.

Run and import KrkrDump:

```text
hxv4 krkrdump ARCHIVE
  --game-executable EXE
  --destination DIR
  [--tool-directory DIR]
  [--no-elevate]
  [--same-directory]
  [--run-only]

hxv4 krkrdump-import ARCHIVE
  --result-dir DIR
  [--game-executable EXE]
  [--same-directory]
```

The run command normally shows Windows elevation, launches the game, and waits
for it to exit. It reads no console answers. Use a new destination for each run;
an existing result should be handled with `krkrdump-import`. Canceling the wait
leaves the game running.

## Help and unsupported operations

```powershell
& $cli help --output json --non-interactive
& $cli help archive extract --output json --non-interactive
```

Protocol v1 does not create archives or accept a general password/scheme option
file. It decodes, extracts, structures supported script text, and converts
images; it does not perform OCR, transcription, translation, semantic labeling,
cross-asset linking, or embedding. Do not emulate missing operations by parsing
legacy console output.
