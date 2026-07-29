# Hx v4 Tool and CLI Parity

GARbro provides a native, reusable Hx v4 tool layer and exposes it through
`Onachi-GARbro.Cli.exe`. The compatibility baseline is
[`MLChinoo/hxv4_unhash_tools` commit `0ce7793`](https://github.com/MLChinoo/hxv4_unhash_tools/commit/0ce7793fad38e9f885345062fb01071aa5aa7ebf).

The implementation is independently authored. GARbro does not copy, import,
bundle, or execute that project's Python source, hash DLL, PSB decompiler, or
PBD converter. The audited upstream revision has no root license file, so it is
used only as a behavioral reference and optional test oracle.

## Operation Mapping

| Upstream operation | Native GARbro operation | CLI surface |
| --- | --- | --- |
| initial plaintexts and `duplicate_lower` | candidate initialization and lowercase expansion | automatic in both generate commands |
| `from_unobfuscated_directory` | bounded loose-tree scan with path-suffix recovery and reparse-point avoidance | `hxv4 generate --source-dir DIR` |
| `scan_psb_and_decompile` | native PSB/MDF parser and Hx v4 scenario walker | `hxv4 generate --source-dir DIR` or `--source-file FILE` |
| `from_base_stage` | structured `base.stage` parser | same |
| `from_cglist_csv` | CG, event-difference, SD, thumbnail, censored, and save-thumbnail expansion | same |
| `from_soundlist_csv` | BGM/audio sidecar expansion | same |
| `from_krkrdump_logs` | bounded `KrkrDump-*.log` mapping scan | `hxv4 generate --krkrdump-dir DIR` |
| `add_char_sys_voices` | character prefix and system-voice expansion | automatic when `charvoice.csv` is scanned |
| `from_imagediffmap_csv` | image-difference and explicit-extension expansion | automatic |
| `from_bgv_csv` | background-voice audio and sidecar expansion | automatic |
| `from_savelist_csv` | save/normal thumbnail expansion | automatic |
| `from_scenelist_csv` | scene/movie thumbnail and censored expansion | automatic |
| `find_missing_voices` | numeric sequence expansion plus optional existing-file diagnostics | automatic generation; `hxv4 find-missing-voices --voice-dir DIR` for the diagnostic list |
| `add_movies` | replay movie names, locales, resolutions, MP4, and WMV expansion | automatic |
| `from_stand_files` | stand PBD/SINFO reference scan | automatic |
| `from_pbd_files` | native PackinOne `TJS/4s0` decryptor plus framed LZ4 reader with rolling dictionary | automatic |
| `from_chthum_index_pbd` | native PackinOne `TJS/ns0` object reader and decryptor | automatic |
| hash, merge, and `HxNames.lst` write | native salted BLAKE2s/SipHash, seed merge, and atomic UTF-8 writer | `hxv4 hash`, `hxv4 generate --seed FILE` |
| `generate_clean_hxnames` | observed extracted-tree filter | `hxv4 clean` |
| `restore_dir_structure` | contained flat-name directory restoration | `hxv4 restore-structure` |
| main-script file/directory rename | contained bottom-up rename, unique file conflicts, and directory merge | `hxv4 rename` |

GARbro also exposes operations that are useful for a complete headless
workflow but are outside the upstream script's public `PlainDict` methods:

- `hxv4 schemes` discovers installed Hx v4 schemes.
- `hxv4 generate-archive` decrypts real Hx indexes and writes only candidates
  whose hashes occur in the selected game.
- `hxv4 krkrdump` prepares, launches, collects, and optionally imports the
  bundled KrkrDump runtime result without implicitly starting a resource scan.
- `hxv4 krkrdump-import` imports an already collected KrkrDump result without
  launching a game; `generate-archive` remains the explicit index-filtered name
  generation operation.

## Candidate Compatibility

Candidate parity is defined as a superset contract: for the same source data,
every file and path candidate emitted by the baseline upstream operation must
be emitted by GARbro. GARbro may add conservative candidates for more audio
extensions, common resources, or names found through native parsing. Archive
generation filters that superset against hashes actually present in the Hx
indexes, so extra candidates do not become unrelated archive names.

The deterministic CLI suite creates independently authored fixtures for:

- plaintext directories, mixed-case names, and KrkrDump logs;
- `base.stage` and every supported CSV source;
- replay movies and stand metadata;
- scenario PSB data covering the upstream scenario branches;
- plaintext and encrypted `TJS/ns0` plus multi-block `TJS/4s0` PBD objects;
- missing numeric voices, system voices, image variants, and localized movies.

When `-HxV4UpstreamRoot` is supplied, the suite invokes every upstream source
over the same fixtures and asserts that GARbro's generated table contains every
upstream file and path candidate:

```powershell
.\tests\Cli\Invoke-CliTests.ps1 `
  -Configuration Debug `
  -HxV4UpstreamRoot "C:\path\to\hxv4_unhash_tools"
```

The optional oracle imports the separate checkout at test time. It is not used
by a normal build or distributed package.

Fixed file/path hash vectors are also compared with the upstream
`KrkrHxv4Hash.dll`. Plaintext and encrypted PBD fixtures are accepted by
upstream `pbd2json.exe`, while the same objects are parsed by GARbro's native
reader.

Loose-source generation is limited to 100,000 files by default and the limit is
configurable with `--max-files`. KrkrDump seed scanning is separately bounded
to 1,024 top-level logs and 64 MiB total. Resource-level PSB, PBD, and text
bounds are documented in `krkrdump-xp3-assist.md`.

## Write Safety

`hxv4 generate` and `hxv4 clean` write through a temporary file before
committing the destination. `hxv4 restore-structure` and `hxv4 rename` support
`--dry-run`, reject paths that escape the requested root, and do not follow
directory reparse points.

File collisions use a deterministic `_1`, `_2`, and so on suffix. Directory
rename collisions merge trees bottom-up; identical duplicate files are
deduplicated and non-identical collisions receive a unique suffix. Every
planned, changed, ignored, skipped, or failed item is represented in the
structured result.

KrkrDump is a runtime-assisted workflow rather than a silent offline command.
The default launch requests Windows elevation and waits for the game to exit.
`--no-elevate` is for an already elevated or otherwise prepared environment.
Each CLI run requires a fresh destination; existing results are handled by the
separate import command so stale runtime output is never silently reused.
Canceling the CLI wait leaves the launched game running and returns a structured
`operation_canceled` result.
