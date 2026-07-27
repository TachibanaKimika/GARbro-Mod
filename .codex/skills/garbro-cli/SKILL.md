---
name: garbro-cli
description: Use GARbro's versioned non-interactive CLI to recognize, inspect, list, or safely extract visual novel archives; export supported game scripts as filtered, raw, diagnostic dump, or structured JSONL text; inspect or convert GARbro-supported images; and diagnose format recognition, required-parameter, conflict, safety-limit, protocol, or extraction failures. Use for GARbro automation and AI workflows that need stable JSON/JSONL results instead of GUI interaction or legacy human-readable console output.
---

# GARbro CLI

Use `Onachi-GARbro.Cli.exe` as the automation boundary. Keep decoding, path
validation, limits, and file writes inside GARbro. Use this skill to select the
right command, mode, and safety policy.

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
  syntax, discovery calls, options, or output names.
- Read [script-text-modes.md](references/script-text-modes.md) before every
  script export or when choosing among `filtered`, `raw`, `dump`, and `jsonl`.
- Read [machine-protocol.md](references/machine-protocol.md) when consuming
  stdout, JSONL events, statuses, errors, or exit codes.
- Read [extraction-safety.md](references/extraction-safety.md) before any
  archive extraction or other multi-file write.

## Follow the workflow

1. Run `probe` on unknown input.
2. Run `archive list` before selecting or writing archive entries.
3. Match the user's requested scope exactly. Do not turn one requested entry
   into a full-archive extraction.
4. For script export, discover the handler's `textModes`, then select a mode
   from the user's intended use. Never silently substitute another mode.
5. For archive extraction, run `--dry-run` when multiple entries, globs,
   conflicts, paths, or limits need validation.
6. Keep `--overwrite never` unless the user explicitly authorizes `skip` or
   `replace`.
7. Pass user paths as separate PowerShell arguments. Never build a shell command
   string from user input.
8. Parse machine fields, not localized messages. Report the output destination,
   status, counts, bytes, skips, failures, warnings, and whether the result was
   only a dry-run.

## Preserve the two JSONL meanings

Never confuse these independent options:

- `--mode jsonl` makes the generated script file contain one structured message
  object per line.
- `--output jsonl` makes CLI stdout contain one machine-protocol event envelope
  per line.

A command can use either option or both. Read
[script-text-modes.md](references/script-text-modes.md) and
[machine-protocol.md](references/machine-protocol.md) before parsing them.

## Stop on actionable failures

- On `needs_input`, report the handler tag, notice, and source. Do not guess a
  password, key, title, or game scheme.
- On `script_mode_not_supported`, report `requestedMode` and `availableModes`.
- On `conflict`, preserve existing files and request policy only when completion
  requires it.
- On `partial_success`, report written, skipped, failed, and bytes; never call
  it complete success.
- On `unrecognized`, report that no GARbro handler accepted the input.
