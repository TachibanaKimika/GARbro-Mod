# GARbro Machine CLI

`Onachi-GARbro.Cli.exe` is the stable, non-interactive command boundary for
automation and AI agents. It shares GARbro's `GameRes` catalog and MEF-discovered
format handlers, but it does not display WPF dialogs or read answers from the
console.

The first protocol version is `garbro.cli/v1`. Protocol versions are independent
from the executable's assembly version.

## Installation and discovery

The Windows installer includes an optional `Add GARbro CLI to system PATH`
component. It is unchecked by default so installation does not silently change
the machine environment. When selected, it adds the chosen installation
directory to the machine `PATH`; newly opened terminals can then resolve
`Onachi-GARbro.Cli.exe`.

The installer records ownership only when it adds a new entry. Uninstall removes
that owned entry, but leaves a matching entry alone when it existed before
installation. PATH updates use the full environment-variable value rather than
an NSIS string register, so an already long PATH is not truncated.

Repository automation should prefer
`bin\Release\Onachi-GARbro.Cli.exe`, then `bin\Debug`, before falling back to
`Get-Command Onachi-GARbro.Cli.exe` and the normal `Program Files` installation
directories. The repo-local Codex skill implements this lookup at
`.codex/skills/garbro-cli/SKILL.md`.

The full package includes `garbro-cli-skill.zip`. In the GUI, open
`Preferences -> AI integration` and use `Save SKILL ZIP...` to save a copy to a
user-selected location. The archive has one top-level `garbro-cli` directory.
Review or extract it, then place that directory under:

- `$CODEX_HOME\skills\garbro-cli` when `CODEX_HOME` is set;
- `%USERPROFILE%\.codex\skills\garbro-cli` otherwise.

The settings action copies the ZIP already bundled with GARbro; it does not
download from the network or modify the Codex skills directory. The package
contains:

```text
garbro-cli/
  SKILL.md
  agents/openai.yaml
  references/command-reference.md
  references/script-text-modes.md
  references/machine-protocol.md
  references/extraction-safety.md
```

The short entry document routes the agent to only the reference needed for the
task. In particular, `script-text-modes.md` defines `filtered`, `raw`, `dump`,
and generated-file `jsonl` separately from the CLI's stdout `--output jsonl`.
Open a new Codex task after extraction so the skill catalog can be reloaded.

## Quick start

From the repository root after a Debug build:

```powershell
$cli = ".\bin\Debug\Onachi-GARbro.Cli.exe"

& $cli capabilities --output json --non-interactive
& $cli probe "path\to\data.arc" --output json
& $cli archive list "path\to\data.arc" --output jsonl
& $cli archive extract "path\to\data.arc" `
    --destination "path\to\output" `
    --entry "scenario\*.ks" `
    --dry-run `
    --output json
```

Use `--output json` for one bounded response and `--output jsonl` when a command
can report many entries. Standard output contains only protocol objects in both
machine modes. Diagnostics produced by `--verbose` go to standard error.

The CLI is always non-interactive. `--non-interactive` is accepted so callers
can state that requirement explicitly.

## Commands

| Command | Purpose |
| --- | --- |
| `capabilities` | Report protocol, command, format-count, optional-component, and safety capabilities. |
| `formats list [--kind all\|archive\|image\|audio\|script]` | List discovered format handlers. |
| `probe PATH` | Detect an archive, image, audio, or supported script without creating files. |
| `archive list ARCHIVE` | List archive metadata and entries. |
| `archive extract ARCHIVE --destination DIR [--entry GLOB]` | Extract selected entries with bounded writes. Repeat `--entry` to use multiple globs. |
| `script extract PATH --mode MODE --destination DIR [--entry EXACT_NAME]` | Convert a physical script, or one exact entry from an archive. |
| `image info IMAGE` | Report the selected image handler and metadata. |
| `image convert IMAGE --format TAG_OR_EXTENSION --destination DIR` | Convert one image with a writable GARbro image handler. |

Script `MODE` is required and is one of `filtered`, `raw`, `dump`, or `jsonl`.
It creates `<base>.txt`, `<base>.raw.txt`, `<base>.dump.txt`, or
`<base>.jsonl`, respectively. A format may support only a subset; discover its
`textModes` through `formats list --kind script` or `probe`. Unsupported modes
return `script_mode_not_supported` instead of silently changing the request.

`--mode jsonl` controls the generated script file. It is independent of
`--output jsonl`, which controls stdout protocol envelopes; both may appear in
one command and must be parsed with different schemas.

`image convert --format` accepts either a handler tag such as `PNG` or an
extension such as `png`. The selected handler must advertise `CanWrite`.
WebP output is available as `WEBP/80` (lossy quality 80) and
`WEBP/LOSSLESS`; using the `webp` extension selects `WEBP/80`.

## Common options

```text
--output json|jsonl|text
--verbose
--non-interactive
```

Commands that write files additionally accept:

```text
--destination DIR
--overwrite never|skip|replace
--dry-run
--max-files N
--max-total-bytes N
--max-entry-bytes N
--max-depth N
```

Defaults are discoverable through `capabilities`. In v1 they are:

| Setting | Default |
| --- | ---: |
| overwrite | `never` |
| max files | 10,000 |
| max total bytes | 4 GiB |
| max bytes per entry | 1 GiB |
| max path depth | 32 |

`never` rejects an existing destination before extraction begins. `skip`
preserves existing files; any skipped item makes the final result
`partial_success`. `replace` must be explicit and replaces through a same-volume
temporary file.

## JSON envelope

JSON mode writes exactly one envelope:

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

Errors use the same envelope and add a stable `error.code`:

```json
{
  "schemaVersion": "garbro.cli/v1",
  "operationId": "4885cced36de4976ba97243082952cc9",
  "command": "probe",
  "status": "needs_input",
  "error": {
    "code": "resource_parameters_required",
    "message": "The resource requires format-specific parameters that are unavailable in non-interactive mode.",
    "details": {
      "resourceTag": "ZIP",
      "resourceType": "archive"
    }
  }
}
```

Callers should branch on `status`, `error.code`, and the process exit code.
Human-readable messages are not a stable parsing surface. New optional fields
may be added within v1.

## JSONL events

Every JSONL line is an independent v1 envelope with the same `operationId`.
Commands emit events such as `start`, `entry`, `file`, and `result`. The final
line is always a terminal `summary`, `error`, or `needs_input` event.

For example, `archive list` emits one `archive` event, one `entry` event per
item, and a final `summary`. `archive extract` emits `file` events followed by
a summary containing:

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

`bytesWritten` counts committed final files. `observedBytes` is the amount
charged to the safety budget while streams were processed and can be higher
when a failed entry produced bytes before failing.

## Exit codes

| Code | Status | Meaning |
| ---: | --- | --- |
| 0 | `success` | The complete command succeeded. |
| 2 | `usage_error` | Command or option syntax is invalid. |
| 3 | `invalid_input` | A path, option value, resource, or safety limit is invalid. |
| 4 | `unrecognized` | No handler accepted the input. |
| 5 | `needs_input` | A handler requires a password, scheme, key, or other interactive parameters. |
| 6 | `conflict` | An existing or colliding destination is disallowed. |
| 7 | `partial_success` | Some selected entries failed or were skipped. |
| 8 | `io_error` | A classified filesystem or stream error occurred. |
| 9 | `internal_error` | An unexpected exception crossed the command boundary. |

Ctrl+C returns `canceled` with exit code 3 after the current operation observes
the cancellation request.

## Non-interactive parameters

The CLI subscribes to `FormatCatalog.ParametersRequest` and deliberately leaves
`InputResult` false. A handler that would normally show a GUI therefore returns
exit code 5 with:

- the handler tag and resource type;
- its localized notice, when available;
- the source file name, when supplied by the handler.

The CLI never guesses a password, key, game scheme, or title. v1 does not yet
offer a general options-file binder. Keep secrets out of command lines and
logs; use the GUI until a typed headless provider exists for that format.

## Extraction safety

Before writing any selected archive entry, the CLI:

1. rejects empty, rooted, drive-qualified, relative-escape, invalid, ambiguous,
   and reserved Windows names;
2. resolves every final path and proves it remains below `--destination`;
3. detects case-insensitive destination collisions;
4. checks declared file, entry-size, total-size, and depth limits;
5. applies actual-byte limits while decompressed or encoded bytes are written;
6. writes to a uniquely named `.partial` file in the destination directory;
7. moves or replaces that file only after the writer completes;
8. removes its temporary file after cancellation or failure.

`--dry-run` performs selection, path validation, declared-size checks, and
conflict checks without creating the destination.

## Verification

Build and basic smoke:

```powershell
.\build.ps1 -Configuration Debug -NoPackage -NoVersionStamp -Smoke
```

Run the deterministic synthetic protocol and safety suite:

```powershell
.\tests\Cli\Invoke-CliTests.ps1 -Configuration Debug
```

Validate installer PATH handling without changing the user or machine PATH:

```powershell
.\tests\Installer\Invoke-PathRegistrationTests.ps1
```

After a Release build, validate the ZIP layout, source/package content equality,
and settings-page save/replace behavior entirely under a temporary directory:

```powershell
.\tests\Installer\Invoke-CodexSkillPackageTests.ps1 -Configuration Release
```

Add the local `pieces／渡り鳥のソムニウム` sample for real YPF and JPEG coverage:

```powershell
.\tests\Cli\Invoke-CliTests.ps1 `
  -Configuration Debug `
  -SampleRoot "I:\TempDays\[Whirlpool][201903]pieces／渡り鳥のソムニウム"
```

The test creates all outputs below a unique system temporary directory and
removes that directory in `finally`. It never modifies or copies fixtures back
into the repository. Synthetic cases cover KiriKiri script modes, ZIP path
escape, overwrite/partial behavior, and an encrypted ZIP `needs_input`
response.
