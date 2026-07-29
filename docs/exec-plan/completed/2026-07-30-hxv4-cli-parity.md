# Hx v4 Tools and CLI Parity

## Context

The current native Hx v4 name generator in
`ArcFormats/KiriKiri/HxNameGenerator.cs` and
`ArcFormats/KiriKiri/HxResourceNameScanner.cs` covers the common automatic
GARbro workflow, but it is not behaviorally equivalent to
`MLChinoo/hxv4_unhash_tools`. In particular, the upstream workflow also exposes
explicit plaintext sources, general voice-sequence completion, clean-table
generation, extracted-tree restoration, and file/directory renaming.

The compatibility baseline is upstream commit
`0ce7793fad38e9f885345062fb01071aa5aa7ebf`. The user requires every relevant
capability to be available through `Onachi-GARbro.Cli.exe`, including launching
and importing KrkrDump.

Relevant local entry points:

- `ArcFormats/KiriKiri/HxNameGenerator.cs`
- `ArcFormats/KiriKiri/HxResourceNameScanner.cs`
- `ArcFormats/KiriKiri/KrkrDumpResultImporter.cs`
- `GameRes/HxV4KrkrDump.cs`
- `GUI/KrkrDumpRunner.cs`
- `Cli/CommandLine.cs`
- `Cli/CliApplication.cs`
- `tests/Cli/Invoke-CliTests.ps1`

## Acceptance Criteria

- Every public operation in upstream `PlainDict`, `main.py`,
  `generate_clean_hxnames.py`, and `restore_dir_structure.py` has an equivalent
  native GARbro operation or a documented composition of GARbro operations.
- Candidate generation matches the upstream baseline for fixed PSB, MDF,
  `base.stage`, CSV, replay, stand, PBD, directory, KrkrDump-log, and voice
  fixtures.
- The native hash output matches the upstream hash DLL for fixed file and path
  vectors.
- The CLI exposes discoverable, non-interactive `hxv4` commands for hashing,
  name-table generation, clean-table generation, directory-structure
  restoration, name-table-based rename, and KrkrDump run/import/generation.
- Destructive CLI operations support dry-run, explicit conflict policy,
  destination containment, and structured per-item results.
- GUI Hx v4 generation and CLI Hx v4 generation use the same core scanner and
  table implementation.
- `capabilities`, help, command references, machine protocol notes, README, and
  operational documentation describe the new commands and safety contract.
- The solution builds with Visual Studio MSBuild, CLI regression tests pass,
  and parity fixtures pass.

## Implementation Checklist

- [x] Inventory upstream operations and map each one to a local API and CLI
      command.
- [x] Refactor the current candidate collector into a reusable unfiltered and
      index-filtered Hx v4 name-table builder.
- [x] Replace heuristic-only `base.stage`, scenario PSB, CSV, PBD, and voice
      behavior where it differs from the upstream baseline.
- [x] Add explicit source-directory and source-file inputs.
- [x] Add clean-table, restore-structure, and safe rename operations.
- [x] Move or wrap KrkrDump execution/import behind a reusable non-WPF service.
- [x] Add the `hxv4` CLI command group and structured output.
- [x] Add deterministic compatibility fixtures and CLI end-to-end assertions.
- [x] Update user, CLI, build, and architecture documentation.

## Validation Checklist

- [x] Compare fixed candidate sets with upstream commit `0ce7793`.
- [x] Compare fixed file/path hashes with `KrkrHxv4Hash.dll`.
- [x] Restore `packages.config` dependencies through Visual Studio MSBuild.
- [x] Run the Debug Visual Studio MSBuild solution build with version stamping
      suppressed.
- [x] Run `tests/Cli/Invoke-CliTests.ps1 -Configuration Debug`.
- [x] Run Hx v4 parity fixtures.
- [x] Run `Onachi-GARbro.Cli.exe capabilities --output json
      --non-interactive` and inspect every advertised command.
- [x] Run non-destructive KrkrDump discovery/import tests; record that an
      actual elevated game launch requires an installed game sample.

## Progress

- 2026-07-30: Created the parity plan after auditing commit `2a701369` against
  upstream `0ce7793`; strict parity is not yet achieved.
- 2026-07-30: Implemented the native Hx v4 service and all CLI operations.
  Differential fixtures invoke every upstream plaintext source and assert that
  GARbro's candidate table is a superset. Debug CLI E2E passed 1,087 assertions.
- 2026-07-30: Restored packages with Visual Studio MSBuild and completed a
  Debug full-solution build with version stamping suppressed. The actual
  elevated KrkrDump/game launch remains an explicit manual boundary; runtime
  discovery, missing-runtime reporting, existing-result collection, and import
  are automated.

## Decision Log

- 2026-07-30: Keep automatic GUI generation non-destructive. Expose rename and
  directory restoration only as explicit operations with dry-run support.
- 2026-07-30: Use the upstream commit as a behavioral oracle, not as vendored
  source or binaries. The upstream repository has no root license file, so the
  implementation must remain independently authored.
- 2026-07-30: Preserve the `garbro.cli/v1` protocol by adding optional commands
  and fields rather than changing existing envelope semantics.
- 2026-07-30: Define candidate parity as a tested superset contract. Native
  archive generation filters candidates against hashes present in real Hx
  indexes, so conservative extra candidates cannot steal unrelated names.
- 2026-07-30: Place the shared KrkrDump process runner in `GameRes` because both
  the WPF host and CLI already depend on it; keep Hx resource parsing and table
  services in `ArcFormats`.

## Outcomes

Completed with a GO release review. Every audited upstream operation has a
native API/CLI mapping, the candidate differential and 1,087 CLI assertions
pass, the full Debug solution and packaged skill build cleanly, and the GUI
startup smoke remains alive for three seconds. Runtime discovery/import are
automated; an actual elevated KrkrDump/game session remains an explicitly
documented manual validation boundary.
