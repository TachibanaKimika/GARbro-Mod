# Large Library Ingest

Use this workflow for a whole game, a large archive, duplicate-heavy XP3 data,
a resumable extraction, or a large image conversion set. It keeps recognition,
decryption, path mapping, finite budgets, and provenance inside GARbro while
leaving semantic interpretation to downstream systems.

Use `--output jsonl` by default for every potentially large result. Use
`--summary-only` only on commands that advertise it and only after deciding
that per-item evidence is unnecessary.

## 1. Negotiate capabilities

```powershell
$caps = & $cli capabilities --output json --non-interactive |
  ConvertFrom-Json
```

Require `garbro.cli/v1`. Confirm the expected commands, duplicate policies,
resume modes, `garbro.extraction-manifest/v1`, and finite default limits from
the response. Do not infer support from an assembly version.

## 2. Probe and resolve protected XP3

Start read-only:

```powershell
& $cli probe $archive --output json --non-interactive
```

If XP3 requires a scheme, discover exact candidates and title mappings:

```powershell
& $cli archive schemes `
  --filter $title `
  --output jsonl --non-interactive

& $cli archive scheme-info $scheme `
  --output json --non-interactive
```

Choose one explicit composition:

```text
--scheme NAME                 known title or builtin scheme
--cx-dump-dir DIR             strict KrkrDump/Cx result directory
--hx-names FILE               explicit Hx v4 name table, applied last
```

When both `--scheme` and `--cx-dump-dir` are present, the Cx dump supersedes
the base scheme for content decryption. `--hx-names` is applied to the
effective Hx v4/Cx-Hx scheme afterward. The compatibility form
`DIR|garbro-importer` is accepted, but no other modifier is valid.

The Cx directory is a strict input boundary. Relevant `PathHash` and `NameHash`
records in its KrkrDump logs are applied to the transient scheme in memory, but
an `HxNames.lst` in that directory is not loaded automatically. Pass an extra
table explicitly with `--hx-names`. Typed `scheme-check`, `plan`, and extraction
`--dry-run` resolution do not write files back into the Cx result directory.

Strict Cx input accepts at most 128 logs, 16 MiB per log and 64 MiB total; the
order file is at most 64 KiB and the table is exactly 4,096 bytes. A Cx directory
or ancestor reparse point returns `xp3_cx_dump_reparse_point`; malformed,
incomplete, or oversized Cx material returns `xp3_cx_dump_invalid`. An invalid
or oversized explicit Hx names file, whose limit is 64 MiB, returns
`xp3_hx_names_invalid`. None of these paths writes an importer cache.

If no installed scheme can open the archive and runtime collection is
authorized, collect or import Cx/Hx evidence first:

```powershell
& $cli hxv4 krkrdump $archive `
  --game-executable $gameExe `
  --destination $freshDumpRoot `
  --output jsonl --non-interactive

& $cli hxv4 krkrdump-import $archive `
  --result-dir $existingResult `
  --output json --non-interactive
```

The first command can show UAC and launch the game; use a fresh destination.
The second is the non-launching path for an existing result. Use the reported
KrkrDump result directory as `$cxDump` in the normal archive commands.

Validate before broad reads:

```powershell
& $cli archive scheme-check $archive `
  --scheme $scheme `
  --cx-dump-dir $cxDump `
  --hx-names $hxNames `
  --output json --non-interactive
```

Omit options that do not apply. Interpret the sample deliberately:

- `matched`: at least one sampled entry header matched recognizable content;
- `inconclusive`: no recognizable evidence was available; continue cautiously;
- `mismatch`: recognizable evidence failed, with reason
  `sample_magic_mismatch`;
- `mixed`: matches and failures both occurred, with reason
  `sample_magic_mixed`.

Both `mismatch` and `mixed` return `xp3_scheme_check_failed`; stop and change
the scheme or artifacts.

Save `schemeResolution.identity`, version-2 `fingerprint`, source chain, and
artifact paths/SHA-256. Reuse the same semantic options for list, plan, dry-run,
extraction, and resume. Case variants of the same resolved scheme name or the
same builtin alias (including XOR hexadecimal case), plus the optional Cx
compatibility suffix, normalize to one manifest identity. Title-versus-builtin,
explicit-versus-auto, and changed material remain distinct. For
probe/list/plan/extract, a recognition-selected installed XP3 scheme without
typed options is captured as `auto_detected`; `scheme-check` still requires
`--scheme` or `--cx-dump-dir`. If archive initialization lazily consumes a TPM
control block, GARbro records the exact 4,096 bytes as
`xp3_tpm_control_block` and recalculates the fingerprint after initialization.

## 3. Generate Hx names when needed

If content decrypts but names remain hashed, first inspect the schemes available
to the standalone generator:

```powershell
& $cli hxv4 schemes --output jsonl --non-interactive
```

Only a scheme listed by that command can be passed to the index-filtered
generator:

```powershell
& $cli hxv4 generate-archive $archive `
  --scheme $scheme `
  --destination $hxNames `
  --seed $seedNames `
  --output jsonl --non-interactive
```

Long generation emits throttled `progress` events. Record phases and counts,
but wait for the terminal event. On `hxv4_generation_failed`, preserve the
structured `reasonCode`, archive/index/candidate/match counts,
`availableSchemes`, and `recommendedActions`:

- `no_readable_index`: verify the scheme or run/import KrkrDump;
- `no_name_matches`: add a relevant seed or inspect name-bearing sources.

Do not describe either as a successful empty table. After generation, rerun
`archive scheme-check` with `--hx-names`.

A scheme imported by a separate `hxv4 krkrdump` or `krkrdump-import` process is
transient and is not available to a later `generate-archive` invocation. When
the only usable decryption context is `$cxDump`, build an unfiltered candidate
table instead, then validate and apply it in the typed Cx process:

```powershell
& $cli hxv4 generate `
  --destination $hxNames `
  --source-dir $gameRoot `
  --krkrdump-dir $cxDump `
  --include-garbro-common `
  --output json --non-interactive

& $cli archive scheme-check $archive `
  --cx-dump-dir $cxDump `
  --hx-names $hxNames `
  --output json --non-interactive
```

`hxv4 generate` does not index-filter the table. The later typed import requires
real index matches before applying it, but it does not rewrite the candidate
file into a filtered table. Keep that distinction in provenance.

## 4. Inventory with stable indexes

```powershell
& $cli archive list $archive `
  --scheme $scheme `
  --cx-dump-dir $cxDump `
  --hx-names $hxNames `
  --output jsonl --non-interactive |
  Set-Content -LiteralPath $inventory -Encoding utf8
```

Again, omit unused scheme options. Keep each `entryIndex`; it is the stable
zero-based identity for one logical occurrence in this opened archive. Name
alone is insufficient when duplicates exist.

For a bounded count-only check:

```powershell
& $cli archive list $archive `
  --scheme $scheme `
  --summary-only `
  --output json --non-interactive
```

## 5. Plan selection and duplicates

Plan creates no files:

```powershell
& $cli archive plan $archive `
  --destination $destination `
  --entry "scenario\**" `
  --entry-index 42 `
  --duplicate-policy error `
  --scheme $scheme `
  --output jsonl --non-interactive |
  Set-Content -LiteralPath $planLog -Encoding utf8
```

`--entry` and `--entry-index` are repeatable and intersect when both are
present. Remove either selector category when an intersection is not intended.

Review the terminal plan summary:

```text
selected
uniqueNormalizedPathCount
duplicateGroupCount
duplicateEntryCount
extraOccurrenceCount
destinationCollisionGroupCount
existingConflictCount
declaredTotalBytes
maximumDeclaredEntryBytes
maximumDepth
recommendedLimits
fitsDefaultLimits
duplicatePolicy
ready
planFingerprint
```

Choose the duplicate policy deliberately:

- `error` preserves the safety default and makes colliding plans not ready;
- `suffix-index` materializes every selected occurrence under deterministic
  names such as `voice.__entry-000123.ogg`.

Rerun the plan with `suffix-index` when retaining duplicates is authorized.
Keep its per-entry output mapping and `planFingerprint` as ingest provenance.

## 6. Understand the budget estimate

Planning fields have distinct meanings:

- `storedBytes`: compressed/stored entry size;
- `declaredBytes`: nonzero unpacked metadata only when the entry is marked
  packed, otherwise stored size;
- `declaredBytesSource`: which metadata supplied the estimate;
- `outputSizeKnown: false`: the final size has not been measured;
- `materializedSizeMayDiffer: true`: decoding can produce a different size.

`recommendedLimits` are finite. They cover selected count and depth and add
finite headroom to declared total and maximum-entry sizes. `--budget auto`
applies those values; it does not disable limits. Actual output bytes are still
charged while extraction runs.

Use `actualBytes`, `bytesWritten`, and `outputSha256` after materialization.
Use `observedBytes` in a completed extraction summary to understand work
charged during failed entry streams. A canceled command terminates with an
error envelope and does not promise that summary. Never substitute declared
metadata for measured output.

## 7. Dry-run the exact extraction

Use the same scheme, selectors, duplicate policy, and destination as the plan:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --scheme $scheme `
  --hx-names $hxNames `
  --dry-run `
  --output jsonl --non-interactive
```

The dry-run validates scheme resolution/index open, selection, mapped paths,
duplicate policy, existing outputs, inputs/artifact collisions, and budgets. It
does not implicitly run scheme-check content-magic sampling. It creates neither
destination files nor an extraction manifest.

For a bounded readiness gate, add `--summary-only --output json`. Use full
JSONL at least once when individual paths or duplicate mappings need review.

## 8. Extract with a checksummed manifest

For a long job, put the provenance manifest outside the extraction tree:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --scheme $scheme `
  --hx-names $hxNames `
  --manifest $manifest `
  --checksum sha256 `
  --overwrite never `
  --output jsonl --non-interactive |
  Set-Content -LiteralPath $runLog -Encoding utf8
```

`garbro.extraction-manifest/v1` is UTF-8 JSONL:

- `header` binds source archive SHA-256, handler/scheme options identity,
  destination, selection, duplicate policy, and plan fingerprint;
- `entry` binds stable index/name, occurrence, resolved output, declared-size
  evidence, and status. `written`/`repaired` records also contain actual bytes
  and optional output SHA-256; resumes append a new record when an entry reaches
  `written`, `repaired`, `skipped`, `failed`, or `not_attempted` again, even if
  its status repeats. After a fatal item error every later unverified entry is
  recorded as `not_attempted` with `aborted_after_error`;
- `summary` records status and counts for a normal or partial completion.

An interrupted non-newline-terminated final record is treated as a crash tail:
dry-run ignores it without mutation, and a writing resume removes it before
appending. Any malformed completed record still invalidates the manifest.
Manifest decoding is strict UTF-8, and its path plus existing ancestors must not
be reparse points. Fresh replace and resume append detach an existing hard link
through a same-directory temporary file and atomic replacement. Resume copies
the current bytes; a fresh manifest starts empty, and a linked peer is preserved.

Specifying a manifest defaults output hashing to SHA-256 unless
`--checksum none` is explicit. Keep `sha256` for durable ingest provenance.

Use stdout JSONL for live operation state and the manifest for durable
file-level provenance. They are different schemas.

## 9. Resume or repair

Resume with exactly the same job identity:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --duplicate-policy suffix-index `
  --budget auto `
  --scheme $scheme `
  --hx-names $hxNames `
  --resume verify-hash `
  --resume-manifest $manifest `
  --overwrite never `
  --output jsonl --non-interactive
```

- `verify-size` compares current output length with prior `actualBytes`.
- `verify-hash` also verifies SHA-256 and is preferred for corpus provenance.
- valid files become `verified_existing`;
- an invalid file without replacement authorization makes the command return
  top-level conflict error code `resume_verification_failed`; it is not an item
  or manifest status;
- only explicit `--overwrite replace` authorizes `repaired` output.

`written` includes repaired outputs and `repaired` is its subset. For every
completed non-dry archive run, check the count closure:

```text
selected = written + verifiedExisting + skipped + failed + notAttempted
```

Prevalidated outputs after a fatal current-run item still count as
`verifiedExisting`. When folding an append-only manifest, a later
`not_attempted` audit record does not erase an earlier materialized state.

Resume rejects a changed source archive, handler/scheme artifact fingerprint,
destination, selection, duplicate policy, plan fingerprint, or prior entry
mapping. Do not edit the manifest to bypass this. Start a new plan and manifest
for a changed job.

## 10. Batch-convert an image library

After safe extraction, convert many images in one initialized process:

```powershell
& $cli image convert-batch `
  --source-root $imageRoot `
  --destination $convertedRoot `
  --format PNG `
  --recursive `
  --detect-by-signature `
  --include "event/**" `
  --budget auto `
  --resume verify-decode `
  --overwrite never `
  --output jsonl --non-interactive
```

The destination must be outside `--source-root`. GARbro preserves relative
structure, changes the final extension, avoids reparse-point traversal, rejects
duplicate source rows and output collisions, prevents outputs from overlapping
the source tree or input manifest, and enforces actual encoded bytes.
Write repeatable `--include` globs against portable relative paths with `/`
separators. Treat `estimatedOutputBytes` as a finite-budget planning value and
`bytesWritten` as measured encoded output.

Instead of scanning, pass `--manifest FILE`. This batch manifest is an input
list: UTF-8 plain-text relative paths or JSONL rows containing `path` or
`sourcePath`. A JSONL value may be rooted only when it still resolves below the
source root. It is not an extraction provenance manifest. `verify-header`
checks format only; `verify-decode` fully decodes a nonempty existing target.
Repair still requires `--overwrite replace`.

If filtering and recognition select no images, the command returns
`invalid_input`/exit 3 with `no_images_selected` and does not create the
destination. WebP resume validation distinguishes `VP8 ` lossy from `VP8L`
lossless, so `WEBP/80` and `WEBP/LOSSLESS` cannot verify across presets. A lossy
bitstream does not prove the exact numeric quality used by an arbitrary encoder.

Use `--summary-only` for a count-only batch after per-image problems no longer
need inspection.

## 11. Hand off to semantic systems

GARbro's completed responsibilities are:

- handler recognition and XP3 scheme application;
- archive index/entry decoding and safe output mapping;
- script-handler text structuring;
- image decoding and re-encoding;
- finite limits, actual byte accounting, hashes, and provenance.

GARbro does not perform OCR, speech transcription, translation, entity/speaker
resolution, scene classification, cross-asset semantic linking, embeddings, or
vector-database ingestion. Those downstream jobs should consume the exported
files plus:

```text
source archive SHA-256
handler and scheme fingerprint
entryIndex and logical entry name
resolved outputRelativePath
actualBytes and outputSha256
script mode or image target format
run/manifest terminal status
```

See [content-semanticization.md](content-semanticization.md) before claiming
semantic completeness or building a retrieval corpus.
