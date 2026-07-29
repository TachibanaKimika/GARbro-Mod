GARbro
======

Visual Novels resource browser.

Requires .NET Framework v4.7.2 or newer (https://dotnet.microsoft.com/en-us/download/dotnet-framework/net472)

[Supported formats](https://morkt.github.io/GARbro/supported.html)

[Download latest release](https://github.com/crskycode/GARbro/releases)

Operation
---------

Browse through the file system to a file of interest.  If you think it's an
archive, try to 'enter' inside by pressing 'Enter' on it.  If GARbro
recognizes format its contents will be displayed just like regular file
system.  Some archives are encrypted, so you will be asked for credentials or
a supposed game title.  If game is not listed among presented options then
most likely archive could not be opened by current GARbro version.

Files could be extracted from archives by pressing 'F4', with all images and
audio converted to common formats in the process, of course if game format
itself is recognized.
The image conversion selector includes `WebP 80%` for smaller lossy output and
`WebP lossless` for exact lossless output; both modes preserve transparency.

When multiple archive files are selected in the file system, 'F4' extracts
them sequentially in the current list order using the same destination and
conversion options.
The extraction dialog can remember an option to open the destination folder
after a successful extraction.

Supported script text extractors can output filtered text, raw text, diagnostic
dumps, JSONL, or both in the GUI. The machine CLI requires one explicit
`--mode`; run it twice when both filtered and raw files are needed.
When a script supports this choice, the preview panel shows a Script selector
for switching between supported text modes.
Extractor behavior and JSONL field conventions are documented in
`docs/reference/script-text-extraction.md`.

Machine CLI
-----------

`Onachi-GARbro.Cli.exe` provides a versioned, non-interactive command interface
for automation and AI agents. It can report capabilities and formats, probe and
list resources, safely extract selected archive entries, export supported
scripts in four text modes, and inspect or convert images.

Machine output uses `garbro.cli/v1` JSON or JSONL on standard output. Extraction
defaults to no overwrite and enforces destination containment, atomic temporary
writes, file/byte/depth limits, and dry-run planning. Formats that require a GUI
password or scheme return a structured `needs_input` result instead of opening
a dialog or guessing a value.

The CLI also exposes the complete native Hx v4 workflow: scheme discovery and
hashing; name-table generation from loose files, PSB/MDF, stage/CSV, stand,
replay, PBD, seed-table, and KrkrDump-log sources; index-filtered archive
generation; clean-table creation; safe directory restoration and hashed-tree
rename; missing-voice diagnostics; plus KrkrDump launch or import. The candidate
rules are differentially tested against every public source in
`MLChinoo/hxv4_unhash_tools` commit `0ce7793`. See
`docs/reference/hxv4-unhash-tools-parity.md` for the operation matrix and
verification contract.

The Windows installer offers an optional, initially unchecked component that
adds the selected installation directory to the system `PATH`. Open a new
terminal after selecting it. The uninstaller removes only the entry that the
installer itself added; otherwise invoke the CLI by its full installed path.

The full package also bundles `garbro-cli-skill.zip`. Open
`Preferences -> AI integration` and choose `Save SKILL ZIP...` to save a copy
where you want it. The ZIP contains one top-level `garbro-cli` directory with a
short `SKILL.md`, Codex UI metadata, and separate references for commands,
script text modes, the machine protocol, and extraction safety. Review it, then
extract that directory to `$CODEX_HOME\skills` or
`%USERPROFILE%\.codex\skills`. GARbro does not modify the Codex skills
directory itself.

For script export, `--mode filtered|raw|dump|jsonl` controls the generated file:
readable text, decoded/source-like context, diagnostic data, or structured
message rows. It is independent of `--output json|jsonl|text`, which controls
the CLI response written to standard output. A command may use both JSONL
options, but their schemas and destinations are different.

See `docs/reference/cli-machine-interface.md` for commands, schema, exit codes,
safety rules, and end-to-end verification.

KiriKiri/XP3 script preview and extraction recognize `.ks`, scrambled `.txt`,
and PSB-backed `.scn` scenario entries.  Filtered mode extracts readable
dialogue/choice text; raw mode keeps the decoded script text, or all strings
under `.scn` scenes.  PSB JSONL output preserves the speaker, message, and
associated voice identifier.  Diagnostic dump mode includes PSB scene metadata,
control flow, compiled lines, full voice descriptors, and environment snapshots;
for text KAG scripts it emits decoded source with line numbers.

For protected KiriKiri/XP3 archives that require runtime parameters, the XP3
archive-parameter dialog includes a `Use KrkrDump...` assistant. It starts the
game executable through the bundled KrkrDump loader with Windows elevation when
needed, and imports the dumped Hx/Cx parameters as a temporary XP3 scheme for
the current session. If opening an `.xp3` fails, the GUI can offer the same
assistant and retry the archive after import. If a package is missing the
bundled KrkrDump runtime, the assistant shows repair instructions and a link to
the original KrkrDump repository. Operational details are in
`docs/reference/krkrdump-xp3-assist.md`.

For Hx v4 archives whose contents decrypt but whose hashed names remain
unresolved, GARbro can generate `HxNames.lst` itself as soon as an Hx v4
encryption scheme is available. This can be the scheme selected in the XP3
dialog or one imported by KrkrDump; use `Generate HxNames from resources` for a
manual rebuild. KrkrDump import only installs the recovered decryption
parameters and any directly logged or already cached names; it does not start
the full resource scan automatically.
It decrypts the indexes and scans every XP3 in the game directory, along with
available loose files. The native scanner reads all name-bearing PSBs plus
`base.stage`, CG/sound/voice/image/save/scene CSV data, `replay.ks`, `.stand`,
and PackinOne `TJS/ns0` or LZ4-compressed `TJS/4s0` PBD metadata, including
the encrypted ChaCha8/12/20 stream variants. It also expands voice sequences,
system voices, movie variants, PBD layer names, and common resource paths.
Every generated candidate is checked against a hash that actually occurs in
the game indexes before it is written.

Manual generation runs in the background and its output is saved in the
per-game local cache for inspection or reuse. On later manual rebuilds, the last
generated result is applied immediately while GARbro scans the current
resources.
The bottom status bar shows loose-file, archive-entry, candidate, and write
progress. The table is validated against the current archive and can be applied
to other XP3 archives in the same directory for the current session. GARbro
does not extract or rename the source files during this process. Compatible
external tables can still be imported manually as a fallback.

The XP3 parameter dialog also supports an optional game-specific preset for
*Limelight Lemonade Jam*. Automatic mode recognizes `limelight_lj*.exe`;
selecting the title explicitly also works for executables with an unrelated
name. The selection is applied when `Use KrkrDump...` finishes, or can be
reapplied with `Apply selected preset` after an Hx v4/KrkrDump scheme is
available. GARbro uses an installed `GameData\HxNames-LLLJ.lst` before the
local generated cache and to seed subsequent live rebuilds. The table is not
distributed with Onachi-GARbro; users can obtain it from
[MLChinoo/lllj_hxnames](https://github.com/MLChinoo/lllj_hxnames) and install
it locally. See `docs/reference/krkrdump-xp3-assist.md` for details.

BGI/Ethornell archive script entries recognize `._bp` scripts and v1 bytecode
with the `BurikoCompiledScriptVer1.00` header.  Filtered mode extracts
character names, messages, and choices; raw mode also includes referenced
internal strings.  For v1 scripts, JSONL associates the directly preceding
`_PlayVoice` identifier with its message in the optional `voice` field.

AdvHD `.ws2` scripts can be opened as script archives and extracted as
filtered text, raw text, JSONL, or diagnostic bytecode dumps.  Filtered mode
extracts character names, messages, and choices while removing AdvHD text
control codes.

Silky's/AI6WIN `.mes` and `.map` scripts support filtered text, raw text,
JSONL, and diagnostic dumps.  MES extraction recognizes both AI6WIN and
Silky's+ bytecode, pairs character names with messages in JSONL, and decodes
line breaks and ruby markers; MAP extraction reads its UTF-16 message table.

Softpal `Sv20` `.src` scripts are recognized when `POINT.DAT` and `TEXT.DAT`
are available in the same directory.  Filtered mode extracts character names,
messages, and choices; raw mode preserves Softpal text markers, while JSONL
keeps name/message structure and dump mode emits decoded bytecode diagnostics.
Voice mapping is not performed.

Preferences -> Experimental -> Auto-select extraction path makes the extract
dialog default to the last extraction parent directory plus the nearest parent
directory name that contains an `.exe` file.

When displaying file system contents GARbro assigns types to files based on
their names extension (so it's not always correct).  If types are misapplied,
it could be changed by selecting files and assigning type manually via context
menu 'Assign file type'.

GUI Hotkeys
-----------

<table>
<tr><td><kbd>Enter</kbd></td><td>                   Try to open selected file as archive -OR- playback audio file</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>PgDn</kbd></td><td>    Try to open selected file as archive</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>E</kbd></td><td>       Open current folder in Windows Explorer</td></tr>
<tr><td><kbd>Backspace</kbd></td><td>               Go back</td></tr>
<tr><td><kbd>Alt</kbd>+<kbd>&rarr;</kbd></td><td>   Go forward</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>PgUp</kbd></td><td>    Go to parent directory</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>O</kbd></td><td>       Choose file to open as archive</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>A</kbd></td><td>       Select all files</td></tr>
<tr><td><kbd>Space</kbd></td><td>                   Select next file</td></tr>
<tr><td><kbd>Numpad +</kbd></td><td>                Select files matching specified mask</td></tr>
<tr><td><kbd>F3</kbd></td><td>                      Create archive</td></tr>
<tr><td><kbd>F4</kbd></td><td>                      Extract selected files</td></tr>
<tr><td><kbd>F5</kbd></td><td>                      Refresh view</td></tr>
<tr><td><kbd>F6</kbd></td><td>                      Convert selected files</td></tr>
<tr><td><kbd>Delete</kbd></td><td>                  Delete selected files</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>H</kbd></td><td>       Fit window to a displayed image</td></tr>
<tr><td><kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>M</kbd></td><td>   Hide menu bar</td></tr>
<tr><td><kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>T</kbd></td><td>   Hide tool bar</td></tr>
<tr><td><kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd></td><td>   Hide status bar</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>S</kbd></td><td>       Toggle scaling of large images</td></tr>
<tr><td><kbd>Ctrl</kbd>+<kbd>Q</kbd></td><td>       Exit</td></tr>
</table>

Author
------

Written by [morkt](https://github.com/morkt/GARbro) under [MIT License](https://github.com/morkt/GARbro/blob/master/LICENSE).

The bundled KrkrDump helper is from
[crskycode/KrkrDump](https://github.com/crskycode/KrkrDump) and is distributed
under GPL-3.0. See `THIRD-PARTY-NOTICES.txt` in release packages and
`Tools/KrkrDump/LICENSE.txt` for the full license text.

The optional *Limelight Lemonade Jam* HxNames preset is not distributed with
Onachi-GARbro. Users can obtain it separately from
[MLChinoo/lllj_hxnames](https://github.com/MLChinoo/lllj_hxnames).

Korean translation by [mireado](https://github.com/mireado), [overworks](https://github.com/overworks)

Simplified Chinese translation by [elasticblitz](https://github.com/elasticblitz), [PeratX](https://github.com/PeratX) and [taroxd](https://github.com/taroxd)

Japanese translation by [haniwa55](https://github.com/haniwa55)

Contributors
------

<a href="https://github.com/crskycode/GARbro/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=crskycode/GARbro" />
</a>
