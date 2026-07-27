# GARbro-Mod-Onachi Agent Guide

This file is the short entry point for Codex agents working in this repository.
Keep durable details in `docs/`, `PLANS.md`, and `.codex/skills/**` instead of
turning this file into a manual.

## Mandatory Rules

- Treat the repository as a legacy Visual Studio C# solution. The primary build
  artifact is `GARbro.sln`; projects use old-style `.csproj` files,
  `packages.config`, shared output folders under `bin/`, and pre-build
  `inc-revision.pl` hooks.
- Before changing code, read the closest existing implementation in the same
  area and follow its conventions. Format handlers are highly pattern-based.
- Do not manually edit generated `*.Designer.cs` files unless the source
  `.resx`, `.settings`, or generator workflow is also part of the task.
- Preserve archive, image, and audio detection invariants. A new recognizer must
  be narrow enough to avoid stealing unrelated formats with common extensions or
  zero signatures.
- Do not treat `dotnet build` failures from missing local toolchain pieces as
  code regressions. Prefer Visual Studio MSBuild, restore `packages.config`
  dependencies with NuGet, and use `dotnet build` only as a diagnostic fallback.
- Perl is used by `inc-revision.pl` for version stamping. In Debug builds the
  current pre-build events end with `exit 0`, so missing Perl is a visible
  warning but not a build blocker.
- When behavior, supported formats, build steps, or project boundaries change,
  update the matching documentation in the same change.
- All GUI, XAML, WPF dialog, icon, and visual styling changes must preserve
  light and dark theme compatibility. Do not introduce theme-dependent literal
  colors; use shared theme resources and validate both modes.
- Keep changes focused. Do not reformat large legacy files or reorder massive
  `.csproj` item lists unless the task requires it.

## Mandatory Skill Usage

Use these repo-local skills when the task matches their trigger:

- `$garbro-build-verify`: build, restore, smoke-test, or diagnose local
  verification for this solution.
- `$garbro-format-authoring`: add or modify archive, image, audio, script, or
  scheme handlers under `GameRes`, `ArcFormats`, `Legacy`, or `Experimental`.
- `$ui-dark-mode`: add, modify, review, or document GUI, XAML, WPF dialogs,
  visual styling, icons, theme resources, or `ArcFormats` WPF option widgets.
- `$docs-sync`: check whether code changes need updates to `README.md`,
  `docs/**`, `PLANS.md`, or supported-format documentation.
- `$commit-with-reflection`: verify current changes, write a commit message,
  commit, or push.
- `$final-release-review`: review a diff before submitting or merging to the
  main branch.
- `$garbro-cli`: recognize, list, or safely extract resources through the
  versioned machine CLI; export supported scripts; inspect or convert images;
  or diagnose non-interactive CLI failures.

## Project Map

- `GameRes/`: core abstractions for resources, streams, format catalog, archive
  extraction, images, audio, and scheme serialization.
- `ArcFormats/`: current format implementations and related WPF option widgets.
- `Legacy/`: older or lower-traffic visual novel formats.
- `Experimental/`: unstable or optional format work and extra dependencies.
- `GUI/`: WPF application, assembly `Onachi-GARbro.exe`.
- `Cli/`: versioned non-interactive JSON/JSONL command interface, assembly
  `Onachi-GARbro.Cli.exe`.
- `Console/`: console archive browser and extraction utility, assembly
  `Onachi-GARbro.Console.exe`.
- `Image.Convert/`: console image metadata/conversion utility, assembly
  `Onachi-GARbro.Image.Convert.exe`.
- `SchemeTool/`: helper for editing serialized format schemes in `GameData`.
- `Net20/`: compatibility classes that require .NET 2.0.
- `docs/`: durable reference material, generated or published support docs, and
  agent-facing project knowledge.

## Documentation Entry Points

- `README.md`: user-facing overview and GUI behavior.
- `PLANS.md`: when and how to write tracked execution plans.
- `docs/architecture/project-structure.md`: stable module and dependency map.
- `docs/reference/build-and-verify.md`: local toolchain, build, and smoke
  verification procedures.
- `docs/reference/dark-mode-adaptation.md`: full light/dark theme architecture,
  migration plan, and future UI authoring rules.
- `docs/reference/script-text-extraction.md`: script extractor text modes,
  JSONL schema, and authoring rules.
- `docs/exec-plan/README.md`: storage convention for active and completed plans.

## Verification Baseline

Use the smallest check that proves the touched area:

- Docs or skills only: re-read changed docs and run skill validation for changed
  `.codex/skills/**`.
- Core or format code: restore packages, then build the changed project and its
  dependents when the local toolchain is available.
- Format behavior: run console listing plus a sample archive/image/audio smoke
  test when samples are available.
- GUI behavior: build the solution and verify the relevant WPF path manually or
  with a focused smoke run when automation is not practical.

If verification is blocked by missing local prerequisites, report the exact
missing prerequisite and the command that exposed it.
