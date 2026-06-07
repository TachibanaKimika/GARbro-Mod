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
- `CxdecTable.bin` and `CxdecOrder.bin` from the game executable directory.

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
  discarded.

Imported KrkrDump schemes are not written to the user's local app-data
`Formats.dat`, and the generated scheme name is not saved as the default XP3
scheme or shown as a normal selectable scheme. Rerun the assistant in a later
session if the same runtime-derived parameters are needed again.

Debug GUI builds write existing `Trace` diagnostics to `bin\Debug\trace.log`.
For KrkrDump-assisted XP3 opens, the trace includes the imported log count,
`PathHash`/`NameHash` counts, and generated `HxNames.lst` path.

KrkrDump itself does not enumerate and offline-extract an arbitrary selected XP3
the way GARbro does. For selected-archive output, use GARbro's normal extract
command after the KrkrDump-imported parameters let the archive open.
