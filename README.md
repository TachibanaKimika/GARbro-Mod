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
unresolved, GARbro can generate `HxNames.lst` itself after KrkrDump finishes.
It decrypts Hx indexes and scenario PSBs, builds resource-name candidates,
computes the Hx v4 hashes, and retains only candidates that occur in the game
indexes. Generation runs in the background and its output is saved in the
per-game local cache for inspection or reuse. On later runs, the last generated
result is applied immediately while GARbro rebuilds it from the current
resources in the background. The bottom status bar shows the current archive,
scenario-entry, candidate, and write progress while this rebuild is running.
The table is validated against the current archive
and can be applied to other XP3 archives in the same directory for the current
session. Compatible external tables can still be imported manually as a
fallback.

BGI/Ethornell archive script entries recognize `._bp` scripts and v1 bytecode
with the `BurikoCompiledScriptVer1.00` header.  Filtered mode extracts
character names, messages, and choices; raw mode also includes referenced
internal strings.

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

Korean translation by [mireado](https://github.com/mireado), [overworks](https://github.com/overworks)

Simplified Chinese translation by [elasticblitz](https://github.com/elasticblitz), [PeratX](https://github.com/PeratX) and [taroxd](https://github.com/taroxd)

Japanese translation by [haniwa55](https://github.com/haniwa55)

Contributors
------

<a href="https://github.com/crskycode/GARbro/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=crskycode/GARbro" />
</a>
