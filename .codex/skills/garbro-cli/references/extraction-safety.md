# Extraction Safety

Read this reference before archive extraction, resume, batch image conversion,
or any broad write.

## Required archive sequence

1. Run `probe` on unknown input.
2. For protected XP3, run `archive scheme-check` with at least one base option,
   `--scheme` or `--cx-dump-dir`; both may be combined, and `--hx-names` is an
   optional overlay rather than a valid base by itself. Parameter-free
   auto-detection applies to probe/list/plan/extract, not to `scheme-check`.
3. Run `archive list --output jsonl`; inspect names, stable indexes, declared
   sizes, types, and count.
4. Select only entries authorized by the user. Remember that `--entry` and
   `--entry-index` intersect.
5. Choose an explicit destination.
6. Run `archive plan --output jsonl`; review duplicate groups, resolved paths,
   conflicts, declared totals, `recommendedLimits`, `ready`, and the plan
   fingerprint.
7. Choose `--duplicate-policy error` or the deterministic `suffix-index` policy.
8. Run `archive extract --budget auto --dry-run --output jsonl` with the same
   scheme and selection options.
9. For a long-lived job, add `--manifest FILE --checksum sha256`.
10. Remove `--dry-run` only when the plan matches the request.

Do not broaden one entry, one extension, or one directory into full-archive
extraction without authorization.

For probe/list/plan/extract, an installed XP3 scheme selected by recognition is
captured as `auto_detected`, not left implicit. If archive initialization lazily
consumes a TPM control block, the exact 4,096 bytes are recorded as
`xp3_tpm_control_block` in the version-2 fingerprint. Reuse the same semantic
scheme identity for plan, extraction, and resume; changed scheme or TPM material
must start a new job.

## Default and automatic budgets

Discover defaults through `capabilities`. Protocol v1 currently reports:

| Setting | Default |
| --- | ---: |
| overwrite | `never` |
| maximum files | 10,000 |
| maximum total bytes | 4 GiB |
| maximum bytes per entry | 1 GiB |
| maximum path depth | 32 |

`archive plan` calculates finite `recommendedLimits` from selected count,
declared bytes, and path depth with bounded headroom. `--budget auto` uses those
values. It does not disable safety checks or create an infinite budget.

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --entry "scenario\*.ks" `
  --duplicate-policy error `
  --budget auto `
  --dry-run `
  --output jsonl `
  --non-interactive
```

Set explicit tighter `--max-files`, `--max-total-bytes`,
`--max-entry-bytes`, or `--max-depth` values when the requested scope demands
them.

## Duplicate logical entries

Archives can contain more than one logical entry whose name resolves to the
same case-insensitive Windows destination.

- `error` is the default. The plan reports collision groups and `ready: false`;
  extraction fails before writing.
- `suffix-index` assigns later occurrences deterministic names containing their
  zero-based archive indexes, such as `voice.__entry-000123.ogg`; a further
  deterministic `-01`, `-02`, and so on resolves a collision with an already
  claimed suffixed name. Use the plan or manifest mapping rather than
  recreating the name yourself.
- `--entry-index N` can select one exact occurrence. If name globs are also
  present, both filters must match.

Do not treat the first duplicate as representative of all occurrences. Preserve
entry indexes in provenance and downstream inventories.

## Path protections

GARbro rejects:

- empty entry names;
- rooted, drive-qualified, or UNC paths;
- `..` traversal and normalized destination escape;
- invalid or ambiguous Windows names;
- reserved device names;
- excessive path depth;
- unresolved case-insensitive destination collisions;
- file/directory hierarchy collisions or an output parent that is an existing
  file;
- outputs that collide with an input archive, Hx/Cx artifact, or manifest.

The CLI resolves each final path and proves it remains below
`--destination`. There is no unsafe-path bypass option.

For `image convert-batch`, every source must remain below `--source-root`,
reparse-point traversal is skipped/rejected, and the destination cannot be the
source root or a descendant of it. Final outputs also cannot land back in the
source tree or overwrite the source manifest. The source root itself and every
existing destination ancestor must be free of reparse points.

## Declared and actual size protections

GARbro checks declared entry sizes before writing and counts actual bytes while
decompression or conversion runs. This protects against archives whose metadata
understates expanded size.

Keep these meanings separate:

- `storedBytes`: bytes stored in the archive;
- `declaredBytes`: nonzero unpacked metadata only for entries marked packed,
  otherwise stored size;
- `actualBytes`: measured final output after materialization or verification;
- `observedBytes`: bytes charged while streams run, including failed attempts.

`max-files`, `max-total-bytes`, `max-entry-bytes`, and `max-depth` are hard
budgets even under `--budget auto`. A failed entry can charge `observedBytes`
without committing a final file.

## Atomic writes and dry-run

Each output is written to a unique `.partial` file in the target directory.
GARbro moves or replaces it only after the writer completes. Cancellation and
failure remove the temporary file when possible.

Archive `plan` never writes. `--dry-run` performs scheme resolution/index open,
selection, path and duplicate validation, declared-size checks, resume
disposition, and conflict checks without creating the destination or manifest.
It does not implicitly run scheme-check content-magic sampling.

## Overwrite modes

- `never`: default. Reject an existing destination before extraction starts.
- `skip`: preserve existing files. A skip normally makes the final result
  `partial_success`.
- `replace`: explicit authorization only. Replace through a same-volume
  temporary file.

Do not choose `replace` merely to make a command finish.

## Manifest and resume safety

For resumable extraction, start the real run with:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --manifest $manifest `
  --checksum sha256 `
  --output jsonl --non-interactive
```

The `garbro.extraction-manifest/v1` header binds source length/time/SHA-256,
handler and scheme-option identity, destination, selection, duplicate policy,
and plan fingerprint. Each entry record binds the logical index/name to its
resolved output and status. Only `written` and `repaired` records carry
`actualBytes` and the optional output hash; `skipped` and `failed` records do
not. A fatal per-entry policy error records every later unverified entry as
`not_attempted` with `aborted_after_error` so the logical selection remains
accounted for.

Resume with the same inputs:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --resume verify-hash `
  --resume-manifest $manifest `
  --output jsonl --non-interactive
```

- `verify-size` accepts an existing output only when its length matches the
  prior `actualBytes`.
- `verify-hash` also compares SHA-256 and forces hashing for new outputs.
- A valid file becomes `verified_existing`.
- An invalid file without replacement authorization returns top-level conflict
  error code `resume_verification_failed`; it is not an item or manifest
  status. Explicit `--overwrite replace` instead authorizes a `repaired`
  output.

In terminal summaries, `written` includes every output materialized during the
run, including repairs. `repaired` is that count's subset for replaced resume
targets; do not add the two values. Image batches use the same count semantics.
For a completed non-dry archive run:

```text
selected = written + verifiedExisting + skipped + failed + notAttempted
```

Prevalidated outputs after a fatal current-run entry still count as
`verifiedExisting`. A later `not_attempted` audit row does not erase an earlier
materialized manifest state.

Do not hand-edit archive identity, scheme fingerprint, plan fingerprint, entry
index, or output path to bypass a mismatch. Create a new manifest for a changed
job. Keep the manifest outside planned output paths and do not reuse one
manifest for multiple destinations. Manifest text is strict UTF-8, and the path
plus all existing ancestors must not be reparse points. Replace and resume
append detach an existing hard link with a same-directory temporary file and
atomic replacement. Resume copies the prior bytes, while a fresh manifest starts
empty, so a linked peer is preserved.

## Batch image resume

`image convert-batch` uses a different resume model and has no extraction
output manifest:

- `verify-header` recognizes the existing target format;
- `verify-decode` fully decodes a nonempty target;
- an invalid existing output is repaired only with `--overwrite replace`;
- `--budget auto` remains finite and actual encoded bytes are charged.

Its `--manifest FILE` is an input source list, not
`garbro.extraction-manifest/v1`. Keep these concepts separate.

## Result review

For large work use JSONL and wait for the terminal event. Use `--summary-only`
only when per-item diagnostics are not needed. Review:

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
duplicatePolicy
planFingerprint
manifest
manifestWritten
```

Report individual failures and warnings. If `failed`, `skipped`, or
`notAttempted` is nonzero, do not describe the operation as fully successful.
