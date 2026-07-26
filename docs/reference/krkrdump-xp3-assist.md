# KrkrDump XP3 Assist

The GUI can reuse KrkrDump to recover runtime KiriKiri/XP3 archive parameters
when the built-in XP3 scheme list is not enough.

## User Flow

1. Open a protected `.xp3` archive.
2. If GARbro asks for archive parameters, choose `Use KrkrDump...` in the XP3
   parameter dialog. If opening the archive fails before that dialog, accept the
   fallback prompt.
3. Confirm the game executable. The dialog auto-selects a likely `.exe` from
   the XP3 directory when possible.
4. Start the assistant. Windows shows the normal UAC prompt because the loader
   is launched with `runas`.
5. Let the game reach the point where it loads the target archive, then close
   the game.
6. GARbro imports the dumped parameters and uses the generated scheme for the
   pending XP3 request without adding it to the visible scheme dropdown.

After a fallback run, GARbro retries the original `.xp3` automatically. The
assistant only imports parameters; normal listing and extraction continue to be
handled by GARbro after the archive opens.

## Real-time Hx v4 Name Recovery

An Hx v4 archive can have valid content keys while its path and file-name hashes
remain unresolved. After KrkrDump parameters are imported, GARbro performs a
native first-run name-recovery pass:

1. Decrypt the Hx v4 indexes from XP3 archives in the game directory and collect
   the path and file-name hashes that actually exist.
2. Reuse names observed in KrkrDump logs and decrypt scenario PSBs from the
   game's `scn`/`scenario` archives.
3. Generate candidates from scenario names, referenced files, voice sequences,
   system voices, loop voices, and common resource paths.
4. Calculate the Hx v4 salted BLAKE2s file-name hashes and SipHash-2-4 path
   hashes, retaining only candidates found in the collected indexes.
5. Write the matched mappings to the per-game result cache:

   ```text
   %LOCALAPPDATA%\Onachi\Onachi-GARbro\HxNames\<game-executable-name>\HxNames.lst
   ```

The pass runs on a worker thread so the WPF parameter dialog remains responsive.
Large games can take one or two minutes because every scenario PSB must be
decrypted and parsed. During the pass, the main window's bottom status bar
temporarily shows the index, scenario-entry, candidate-expansion, and table-write
stages with a determinate progress bar. It returns to the normal status display
with the final result when generation finishes or fails. If the same game
already has a result from an earlier
successful pass, GARbro validates and applies that result immediately, including
the optional same-directory scope, while still rebuilding it from the current
resources. The scenario scanner uses a thread-local scheme, so it does not
temporarily replace the active name mapping seen by archive browsing. On a true
first run with no prior result, unresolved names become available when the pass
finishes.

The cache is an output of generation, not a bundled or preloaded answer table.
It only provides a warm start for the next live rebuild. The status message
reports scenario/candidate counts and the exact path/file-name coverage for the
selected XP3.

The candidate strategy is compatible with the workflow documented by
[MLChinoo/hxv4_unhash_tools](https://github.com/MLChinoo/hxv4_unhash_tools).
GARbro implements the standard hash algorithms and resource scanning natively;
it does not bundle or execute that repository's Python files, hash DLL, or PSB
decompiler.

If native generation cannot run or produces no match for the selected archive,
GARbro still tries compatible external tables in this order:

1. A path explicitly supplied by the KrkrDump host result.
2. The per-game cache from an earlier successful generation.
3. `HxNames.lst` beside the selected XP3.
4. `HxNames.lst` in the game directory reported by KrkrDump.

`Import HxNames.lst manually...` is also available after selecting an Hx v4 or
KrkrDump scheme.

GARbro accepts UTF-8 `HASH:name` records with a 16-digit path hash or a 64-digit
file-name hash. Blank lines and lines beginning with `#` or `;` are ignored; an
empty value is accepted only for the root-path hash. Imported records override
older records with the same hash.

Before enabling the merged scheme, GARbro decrypts the current Hx v4 index and
reports how many path and file-name hashes the table matches. A table with no
matches is rejected, which helps catch a table from the wrong title or an
incorrect encryption scheme.

The merged scheme and optional same-directory scope last only for the current
GARbro session. The generated UTF-8 table remains in the per-game local cache,
but it is regenerated from the current resources on the next successful
KrkrDump import.

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
`PathHash`/`NameHash` counts, scenario/candidate counts, matched index hashes,
and generated `HxNames.lst` path.

KrkrDump itself does not enumerate and offline-extract an arbitrary selected XP3
the way GARbro does. For selected-archive output, use GARbro's normal extract
command after the KrkrDump-imported parameters let the archive open.
