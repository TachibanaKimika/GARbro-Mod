# KrkrDump XP3 Assist

The GUI can reuse KrkrDump to recover runtime KiriKiri/XP3 archive parameters
when the built-in XP3 scheme list is not enough.

## User Flow

1. Open a protected `.xp3` archive.
2. In the XP3 parameter dialog, leave `HxNames preset` on automatic detection
   or select an installed title explicitly. The explicit selection is useful when
   the game executable has been renamed.
3. Choose `Use KrkrDump...`. If opening the archive fails before that dialog,
   accept the fallback prompt.
4. Confirm the game executable. The dialog auto-selects a likely `.exe` from
   the XP3 directory when possible.
5. Start the assistant. Windows shows the normal UAC prompt because the loader
   is launched with `runas`.
6. Let the game reach the point where it loads the target archive, then close
   the game.
7. GARbro imports the dumped parameters and uses the generated scheme for the
   pending XP3 request without adding it to the visible scheme dropdown.

After a fallback run, GARbro retries the original `.xp3` automatically. The
assistant only imports parameters; normal listing and extraction continue to be
handled by GARbro after the archive opens.

If a working Hx v4 scheme is already available, KrkrDump is not required for
name generation. Select the scheme and choose `Generate HxNames from resources`
in the same XP3 parameter dialog. The selected or automatically detected
installed preset, current scheme name tables, and the previous generated cache
are used as seeds. `Use KrkrDump...` only imports the runtime decryption
parameters and directly logged or compatible cached names. It returns control
to the XP3 dialog immediately after import; start the resource scan explicitly
with `Generate HxNames from resources` when it is wanted.

## Real-time Hx v4 Name Recovery

An Hx v4 archive can have valid content keys while its path and file-name hashes
remain unresolved. Once an Hx v4 scheme is available, GARbro can perform a
native name-recovery pass:

1. Decrypt the Hx v4 indexes from every XP3 in the game directory and collect
   the path and file-name hashes that actually exist.
2. Reuse names observed in KrkrDump logs, known names already present in XP3
   entries, and unobfuscated loose file and directory names below the game
   directory.
3. Open every same-directory XP3 with the imported thread-local scheme and
   inspect bounded, name-bearing resources. The scanner recognizes:

   - scenario, image, motion, and other non-encrypted PSBs, including MDF
     entries already decompressed by the XP3 stream filter;
   - `base.stage`;
   - `cglist`, `soundlist`, `charvoice`, `imagediffmap`, `bgv`, `savelist`, and
     `scenelist` CSV data, including content-based detection when the CSV's own
     name is still hashed;
   - `replay.ks` movie references and `.stand` PBD/SINFO references;
   - plaintext or PackinOne-encrypted `TJS/ns0` and LZ4-compressed `TJS/4s0`
     PBD objects, including character layer TLGs and character-thumbnail
     indexes.

4. Generate additional candidates from PSB context, event/thumbnail variants,
   movie resolutions and locales, voice sequences, system and loop voices, and
   common resource paths. Newly matched Hx entry hashes are retained in memory,
   so a `.stand` reference discovered after an otherwise unnamed PBD can still
   supply that PBD's stem before layer names are finalized.
5. Calculate the Hx v4 salted BLAKE2s file-name hashes and SipHash-2-4 path
   hashes, retaining only candidates found in the collected indexes.
6. Write the matched mappings to the per-game result cache:

   ```text
   %LOCALAPPDATA%\Onachi\Onachi-GARbro\HxNames\<game-id>\HxNames.lst
   ```

   The game ID is the KrkrDump executable base name when available; a manual
   rebuild without KrkrDump uses the XP3 directory name.

The manually requested pass runs on a worker thread so the WPF parameter dialog
remains responsive.
Large games can take one or two minutes because each unresolved XP3 entry must
at least be decrypted far enough to identify its content. During the pass, the
main window's bottom status bar temporarily shows the index, loose-resource,
archive-entry, candidate-expansion, and table-write stages with a determinate
progress bar. It returns to the normal status display with the final result when
generation finishes or fails. If the same game already has a result from an
earlier successful pass, GARbro validates and applies that result immediately,
including the optional same-directory scope, while still rebuilding it from the
current resources. The resource scanner uses a thread-local scheme, so it does not
temporarily replace the active name mapping seen by archive browsing. On a true
first run with no prior result, unresolved names become available when the pass
finishes.

The cache is an output of generation and normally provides the warm start for
the next live rebuild. A recognized installed preset is applied before that cache
and is also fed into generation, so a later rebuild retains every preset mapping
that occurs in the current game's indexes. The status message reports
resource/scenario/candidate counts and the exact path/file-name coverage for the
selected XP3.

The candidate strategy is compatible with the workflow documented by
[MLChinoo/hxv4_unhash_tools](https://github.com/MLChinoo/hxv4_unhash_tools).
GARbro implements the standard hash algorithms and resource scanning natively;
it does not bundle or execute that repository's Python files, hash DLL, PSB
decompiler, or PBD converter. It never extracts or renames the game files.

Resource reads are bounded: text metadata is limited to 8 MiB, PSB metadata and
decoded TJS objects to 64 MiB, and loose-file enumeration to 100,000 files.
Directory reparse points are not followed. Encrypted TJS objects are skipped.
These limits prevent malformed or unrelated resources from turning name
generation into an unbounded scan. Name recovery is still candidate-based: a
name that is absent from logs, presets, resource metadata, and recognized naming
patterns cannot be inferred from a one-way hash alone.

If native generation cannot run or produces no match for the selected archive,
GARbro still tries compatible external tables in this order:

1. An installed preset explicitly selected in the XP3 parameter dialog, or a path
   explicitly supplied by the KrkrDump host result.
2. An installed game-specific preset when the selected executable matches it.
3. The per-game cache from an earlier successful generation.
4. `HxNames.lst` beside the selected XP3.
5. `HxNames.lst` in the game directory reported by KrkrDump.

`Apply selected preset` and `Import HxNames.lst manually...` are also available
after selecting an Hx v4 or KrkrDump scheme. The first reapplies the installed
choice without another KrkrDump run; the second accepts an arbitrary compatible
table.

GARbro accepts UTF-8 `HASH:name` records with a 16-digit path hash or a 64-digit
file-name hash. Blank lines and lines beginning with `#` or `;` are ignored; an
empty value is accepted only for the root-path hash. Imported records override
older records with the same hash.

Before enabling the merged scheme, GARbro decrypts the current Hx v4 index and
reports how many path and file-name hashes the table matches. A table with no
matches is rejected, which helps catch a table from the wrong title or an
incorrect encryption scheme.

The merged scheme and optional same-directory scope last only for the current
GARbro session. The generated UTF-8 table remains in the per-game local cache
and is regenerated from the current resources only when a manual rebuild is
requested.

### Optional Limelight Lemonade Jam preset

When installed, `GameData\HxNames-LLLJ.lst` is automatically selected when the
KrkrDump game executable has a base name beginning with `limelight_lj`,
including builds such as `limelight_lj_Crack.exe`. It is also listed explicitly as
`ライムライト・レモネードジャム (LLLJ, 99.97%)` in the XP3 parameter dialog,
so executable-name detection is not required. Selecting it before
`Use KrkrDump...` attaches the table to the imported scheme without starting a
resource scan; after a compatible Hx v4/KrkrDump scheme exists,
`Apply selected preset` can attach it without rerunning KrkrDump.

Onachi-GARbro does not distribute this table. Download it separately from
[MLChinoo/lllj_hxnames](https://github.com/MLChinoo/lllj_hxnames), rename it
to `HxNames-LLLJ.lst`, and place it beside the executable under `GameData`.
For local source builds, it can instead be placed at
`ArcFormats\Resources\HxNames-LLLJ.lst`; the ignored local file is copied to
the Debug build output by the ArcFormats post-build step. Non-Debug builds
remove it from their output so it cannot enter a release package accidentally.
If the file is absent, selecting the preset reports its expected path and other
HxNames generation/import workflows continue to work.

If the release package is missing the bundled runtime, the assistant reports
the missing architecture and shows repair guidance. The source-page button opens
the original KrkrDump repository:

```text
https://github.com/crskycode/KrkrDump
```

Users can reinstall a complete Onachi-GARbro package or place
`KrkrDumpLoader.exe` and `KrkrDump.dll` under the matching
`Tools\KrkrDump\<architecture>\` directory.

## Bundled Tool Layout

The runner copies the KrkrDump files into a per-run runtime directory before
launching them. Release builds should provide:

```text
Tools\KrkrDump\x86\KrkrDumpLoader.exe
Tools\KrkrDump\x86\KrkrDump.dll
```

The runner chooses `x86` or `x64` from the selected game executable's PE machine
type. The currently bundled KrkrDump runtime is x86; add
`Tools\KrkrDump\x64\KrkrDumpLoader.exe` and `Tools\KrkrDump\x64\KrkrDump.dll`
when a working x64 KrkrDump DLL is available. A flat `Tools\KrkrDump\`
directory is accepted as a fallback. Development builds also probe sibling
KrkrDump build outputs when this repository and `KrkrDump` share the same
parent directory.

Release packages must keep `Tools\KrkrDump\LICENSE.txt` with the runtime and
include `THIRD-PARTY-NOTICES.txt` at the application root. KrkrDump is from
`crskycode/KrkrDump` and is distributed under GPL-3.0.

## Machine CLI

The same shared runner and importer are available without the WPF assistant:

```powershell
& $cli hxv4 krkrdump "C:\game\data.xp3" `
  --game-executable "C:\game\game.exe" `
  --destination "C:\work\dump" `
  --output jsonl
```

By default this prepares the architecture-matched runtime, requests Windows
elevation, launches the game, waits for it to exit, collects the log and Cxdec
files, and imports the scheme plus directly logged names. Add `--run-only` to
stop after collection or `--same-directory` to apply the imported scheme to
sibling XP3 archives. Run `hxv4 generate-archive` explicitly when an
index-filtered resource scan is wanted. `--tool-directory` selects an explicit
runtime and `--no-elevate` is available to already elevated or specially
prepared callers. Use a fresh destination for each run. If it already contains
`.krkrdump`, the CLI returns a conflict and directs the caller to
`krkrdump-import`, preventing stale logs or Cxdec files from being mistaken for
the new run.
If the game exits without producing any log or Cxdec output, the command returns
`krkrdump_no_output` instead of reporting collection success.

An existing result can be imported without launching the game:

```powershell
& $cli hxv4 krkrdump-import "C:\game\data.xp3" `
  --result-dir "C:\work\dump\.krkrdump" `
  --game-executable "C:\game\game.exe" `
  --output json
```

The run remains a visible runtime workflow even though the CLI reads no console
input: Windows can show UAC and the game itself opens normally. Ctrl+C cancels
GARbro's wait and returns `operation_canceled`; it deliberately leaves the game
process running.

## Imported Data

The assistant writes `KrkrDump.json` automatically with KrkrDump extraction
disabled and hash dump, Hx key dump, and directory dump enabled. GARbro's
user-facing action is parameter import; the generated parameter files are kept
in the per-run local app-data directory:

```text
Onachi-GARbro\KrkrDump\<game>_<archive>_<timestamp>\.krkrdump
```

It includes:

- `KrkrDump-*.log` from the copied runtime directory.
- `CxdecTable.bin` and `CxdecOrder.bin` from the copied runtime directory or
  game executable directory when KrkrDump writes them there.

The XP3 importer converts these KrkrDump outputs into an `HxCrypt` scheme:

- `Index Key` and `Index Nonce` become the Hx index key and nonce.
- `Filter Key`, `Split Pos Mask`, `Split Pos`, and `Random Type` become Cx/Hx
  scheme fields.
- `CxdecTable.bin` becomes the GARbro Cx control block after the same bitwise
  complement conversion used by `SchemeTool`.
- `CxdecOrder.bin` or logged `Cxdec Order` lines become GARbro branch orders.
- Logged `PathHash` and `NameHash` lines are written to `HxNames.lst` and linked
  from the generated scheme only when the hash appears in the selected XP3's
  Hx index. Hashes from other archives loaded by the same game process are
  retained as seeds for the real-time name generator but are excluded from the
  selected-archive-only KrkrDump table.

Imported KrkrDump schemes are not written to the user's local app-data
`Formats.dat`, and the generated scheme name is not saved as the default XP3
scheme or shown as a normal selectable scheme. Rerun the assistant in a later
session if the same runtime-derived parameters are needed again.

Debug GUI builds write existing `Trace` diagnostics to `bin\Debug\trace.log`.
For KrkrDump-assisted XP3 opens, the trace includes the imported log count,
`PathHash`/`NameHash` counts, resource/scenario/candidate counts, matched index hashes,
and generated `HxNames.lst` path.

KrkrDump itself does not enumerate and offline-extract an arbitrary selected XP3
the way GARbro does. For selected-archive output, use GARbro's normal extract
command after the KrkrDump-imported parameters let the archive open.
