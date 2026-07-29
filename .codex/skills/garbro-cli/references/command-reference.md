# Command Reference

Use this reference to choose syntax. Use `--output json` for bounded responses
and `--output jsonl` for large lists or event streams. Pass
`--non-interactive` explicitly in agent workflows.

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

`json` is the default. Prefer `json` or `jsonl` for automation. `text` is for
humans and is not a stable parsing surface. `--verbose` writes diagnostics to
stderr without contaminating machine stdout.

## Discovery and read-only commands

| Task | Command |
| --- | --- |
| Negotiate protocol and defaults | `capabilities` |
| List handlers | `formats list [--kind all\|archive\|image\|audio\|script]` |
| Recognize one file | `probe PATH` |
| List archive entries | `archive list ARCHIVE` |
| Inspect one image | `image info IMAGE` |

Examples:

```powershell
& $cli capabilities --output json --non-interactive
& $cli formats list --kind script --output json --non-interactive
& $cli probe $path --output json --non-interactive
& $cli archive list $archive --output jsonl --non-interactive
& $cli image info $image --output json --non-interactive
```

For script handlers, `formats list --kind script` and `probe` expose
`textModes`; configurable handlers also expose `defaultTextMode`. Treat these
fields as authoritative because mode support differs by format and can evolve.

## Archive extraction

```text
archive extract ARCHIVE
  --destination DIR
  [--entry GLOB]...
  [--overwrite never|skip|replace]
  [--dry-run]
  [--max-files N]
  [--max-total-bytes N]
  [--max-entry-bytes N]
  [--max-depth N]
```

`--entry` is repeatable and accepts GARbro's archive-entry glob matching.
Without it, all entries are selected, so never omit it when the user requested
only part of an archive.

Example:

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --entry "scenario\*.ks" `
  --overwrite never `
  --max-files 1000 `
  --max-total-bytes 1073741824 `
  --max-entry-bytes 268435456 `
  --max-depth 24 `
  --dry-run `
  --output json `
  --non-interactive
```

Read [extraction-safety.md](extraction-safety.md) before removing `--dry-run`.

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
not a glob and not repeatable. Use `archive list` to obtain that name.

Write options are `--overwrite`, `--dry-run`, and the four safety limits shown
above. The command converts only one physical script or one archive entry per
invocation.

Generated names preserve entry subdirectories and replace the final extension:

| Mode | Example input | Output |
| --- | --- | --- |
| `filtered` | `scenario\start.ks` | `scenario\start.txt` |
| `raw` | `scenario\start.ks` | `scenario\start.raw.txt` |
| `dump` | `scenario\start.ks` | `scenario\start.dump.txt` |
| `jsonl` | `scenario\start.ks` | `scenario\start.jsonl` |

Read [script-text-modes.md](script-text-modes.md) before choosing a mode.

## Image conversion

```text
image convert IMAGE --format TAG_OR_EXTENSION --destination DIR
  [--overwrite never|skip|replace]
  [--dry-run]
  [--max-files N]
  [--max-total-bytes N]
  [--max-entry-bytes N]
  [--max-depth N]
```

`--format` accepts a writable handler tag such as `PNG` or an extension such as
`png`. Use `formats list --kind image` and require `canWrite: true`.

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

Use `hxv4 schemes` first; do not guess the title or scheme name.

Clean and apply tables:

```text
hxv4 clean HXNAMES --deobfuscated-dir DIR --destination CLEAN_HXNAMES
hxv4 find-missing-voices --voice-dir DIR [--voice-dir DIR]...
hxv4 restore-structure DIR [--recursive] [--dry-run]
hxv4 rename DIR --names HXNAMES [--dry-run]
```

`find-missing-voices` follows the observed numeric sequences through maximum
plus five and reports stems whose `.ogg` file is absent. JSON includes the
bounded list; JSONL emits one `missing_voice` event per stem and a count-only
summary.

Always inspect the dry-run before a restore or rename. Paths are constrained to
the requested root. File collisions receive `_1`, `_2`, and so on; directory
collisions merge with identical-file deduplication and unique names for
non-identical conflicts.

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
for it to exit. It reads no console answers. `--no-elevate` is only for a
prepared environment. Use a new destination for each run; an existing
`.krkrdump` result returns `conflict` and should be handled with
`krkrdump-import`. Canceling the wait leaves the game running.

## Help and unsupported operations

```powershell
& $cli help --output json
```

Version 1 does not create archives, batch-convert directories, or accept a
general password/scheme options file. Do not emulate missing operations by
parsing legacy console output.
