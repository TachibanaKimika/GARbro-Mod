---
name: garbro-cli
description: Use GARbro's versioned machine CLI to recognize, inspect, plan, and safely extract visual novel archives; compose and validate XP3 schemes with Hx/Cx artifacts; resume large extraction jobs from checksummed manifests; export supported game scripts; inspect or batch-convert images; run Hx v4/KrkrDump workflows; and diagnose structured protocol failures. Use for GARbro automation and AI ingestion workflows that need stable JSON/JSONL, finite budgets, and provenance instead of legacy human-readable console output.
---

# GARbro CLI

Use `Onachi-GARbro.Cli.exe` as the automation boundary. Keep recognition,
decoding, scheme application, path validation, budgets, hashes, and file writes
inside GARbro. Keep OCR, transcription, translation, classification, linking,
and embedding in downstream tools.

## Establish the interface

1. When `GARbro.sln` exists in the current directory, treat it as a repository
   checkout. Otherwise resolve an installed CLI.
2. Locate the executable in this order: repository Release, repository Debug,
   current `PATH`, 64-bit Program Files, then 32-bit Program Files.

   ```powershell
   $candidates = @()
   if (Test-Path -LiteralPath (Join-Path $PWD "GARbro.sln")) {
       $candidates += Join-Path $PWD `
           "bin\Release\Onachi-GARbro.Cli.exe"
       $candidates += Join-Path $PWD `
           "bin\Debug\Onachi-GARbro.Cli.exe"
   }
   $command = Get-Command Onachi-GARbro.Cli.exe -ErrorAction SilentlyContinue
   if ($command) { $candidates += $command.Source }
   if ($env:ProgramFiles) {
       $candidates += Join-Path $env:ProgramFiles `
           "Onachi-GARbro\Onachi-GARbro.Cli.exe"
   }
   if (${env:ProgramFiles(x86)}) {
       $candidates += Join-Path ${env:ProgramFiles(x86)} `
           "Onachi-GARbro\Onachi-GARbro.Cli.exe"
   }
   $cli = $candidates |
       Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
       Select-Object -First 1
   ```

3. If no CLI exists in a repository checkout, use `$garbro-build-verify`.
   Outside a checkout, ask the user to install GARbro.
4. Run `& $cli capabilities --output json --non-interactive`.
5. Require `garbro.cli/v1` in `data.protocolVersions`. Stop on an incompatible
   protocol instead of guessing fields or parsing human text.

Do not parse `Onachi-GARbro.Console.exe` or
`Onachi-GARbro.Image.Convert.exe` output when the machine CLI supports the task.

## Load only the needed reference

- Read [command-reference.md](references/command-reference.md) to choose command
  syntax, discovery calls, typed XP3 options, or output fields.
- Read [machine-protocol.md](references/machine-protocol.md) when consuming
  stdout, JSONL events, summaries, progress, errors, exit codes, or extraction
  manifests.
- Read [extraction-safety.md](references/extraction-safety.md) before any
  archive extraction, resume, batch conversion, or other broad write.
- Read [large-library-ingest.md](references/large-library-ingest.md) before a
  whole-game, large archive, resumable extraction, or large image-library job.
- Read [script-text-modes.md](references/script-text-modes.md) before every
  script export or when choosing `filtered`, `raw`, `dump`, or `jsonl`.
- Read [content-semanticization.md](references/content-semanticization.md)
  before claiming semantic labels, OCR/transcription, translation readiness,
  cross-asset links, embeddings, or corpus completeness.
- Read [command-reference.md](references/command-reference.md) before an Hx v4
  or KrkrDump workflow; these commands have operation-specific runtime and
  failure semantics.

## Follow the core workflow

1. Run `probe` on unknown input.
2. For protected XP3, discover `archive schemes`, inspect a candidate, then run
   `archive scheme-check` with at least one base option: `--scheme` or
   `--cx-dump-dir`. Both may be present, in which case Cx supersedes the base
   scheme. `scheme-check` never uses parameter-free auto-detection. For
   `probe`, `archive list`, `archive plan`, and `archive extract`, a run without
   typed options preserves the recognition-selected scheme's reported
   `auto_detected` identity and fingerprint. Add `--hx-names` only as an overlay
   on an effective Hx v4/Cx-Hx scheme. A typed Cx import applies logged names in
   memory but does not auto-load or write `HxNames.lst`; pass that file
   explicitly when required. Checks, plans, and dry runs do not write back to
   the Cx directory. Reuse the same semantic composition for every later
   command.
3. Run `archive list --output jsonl` and preserve stable `entryIndex` values.
4. Run `archive plan --output jsonl` for multi-entry work. Review selection,
   resolved paths, duplicate groups, conflicts, declared sizes,
   `recommendedLimits`, `ready`, and `planFingerprint`.
5. Match the user's scope exactly. `--entry` globs and `--entry-index` values
   intersect. Do not turn one requested entry into a full extraction.
6. Keep duplicate policy `error` unless the user wants every duplicate logical
   entry. Then use `suffix-index` and report its deterministic renamed paths.
7. Run extraction with `--budget auto --dry-run`. For long-lived jobs, add a
   manifest and SHA-256 checksum. Remove `--dry-run` only after the plan matches.
8. Resume only with the prior manifest and the same archive, destination,
   selection, duplicate policy, and scheme artifacts. Prefer `verify-hash` when
   provenance matters.
9. Keep `--overwrite never` unless the user explicitly authorizes `skip` or
   `replace`. A damaged resume output is repaired only with `replace`.
10. For large results use `--output jsonl`; on commands that advertise it, add
    `--summary-only` when per-item events are unnecessary. Wait for the terminal
    event before reporting.

Read [large-library-ingest.md](references/large-library-ingest.md) for an
end-to-end command sequence.

## Preserve data meanings

Do not confuse these independent JSONL files and streams:

- `--output jsonl`: CLI stdout event envelopes using `garbro.cli/v1`.
- `--mode jsonl`: generated script message rows.
- `--manifest FILE`: extraction provenance records using
  `garbro.extraction-manifest/v1`.
- `image convert-batch --manifest FILE`: an input source list, not an
  extraction provenance manifest.

Also preserve size semantics: `declaredBytes` is a plan estimate;
`actualBytes` is measured materialized output; `observedBytes` is charged while
streams execute. Never report a declared value as measured output.

## Handle image and script jobs deliberately

- Use `image convert-batch` for a directory or source manifest. Keep its
  destination outside the source tree, skip reparse-point traversal, use a
  finite auto budget, and choose `verify-header` or `verify-decode` for resume.
  Planned outputs must not overlap the source tree or the input manifest.
- For script export, discover handler `textModes` first. Select the mode from
  the user's intended use and never silently substitute another mode.
- Treat image conversion and script extraction as decoding/structuring, not as
  semantic classification. Route semantic claims through
  [content-semanticization.md](references/content-semanticization.md).

## Handle Hx v4 and KrkrDump safely

- Use `hxv4 schemes` before `hxv4 generate-archive`; only listed installed
  schemes are accepted. A separate KrkrDump invocation imports a transient
  scheme, so a Cx-dump-only workflow must use unfiltered `hxv4 generate` and
  then typed `--cx-dump-dir` plus `--hx-names` validation.
- Consume JSONL `progress` events for long archive scans, but wait for the
  terminal event.
- On `hxv4 generate-archive` error `hxv4_generation_failed`, report
  `reasonCode`, counts, `recommendedActions`, and available schemes. Do not
  collapse `no_readable_index` and `no_name_matches` into a generic failure.
  Plain `hxv4 generate` can use the same error code without those archive-scan
  details.
- Inspect `--dry-run` before `hxv4 restore-structure` or `hxv4 rename`.
- Explain that `hxv4 krkrdump` can show Windows elevation and launch the game
  even though the CLI reads no console input.

## Stop on actionable failures

- On `needs_input`, report the handler tag, notice, and source. Do not guess a
  password, key, title, or unsupported scheme parameter.
- On `xp3_scheme_check_failed`, stop before broad extraction and distinguish
  `sample_magic_mismatch` from `sample_magic_mixed`. Treat `inconclusive` as
  insufficient evidence, not success proof.
- On manifest source, handler, destination, plan, or entry mismatch, start a
  new deliberate plan; do not edit the manifest to bypass provenance checks.
- On `resume_verification_failed`, preserve the file unless explicit repair was
  authorized.
- On `script_mode_not_supported`, report `requestedMode` and `availableModes`.
- On `conflict`, preserve existing files and request policy only when completion
  requires it.
- On `partial_success`, report written, repaired, verified, skipped, failed,
  not-attempted, actual bytes, and warnings; never call it complete success.
- On `unrecognized`, report that no GARbro handler accepted the input.
