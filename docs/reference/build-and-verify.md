# Build and Verify

This repository uses legacy .NET Framework project files. Use Visual Studio
MSBuild for real verification; plain `dotnet build` is only a diagnostic
fallback and can fail even when Visual Studio MSBuild succeeds.

## Prerequisites

Install or expose these tools before expecting a full build to pass:

- Visual Studio Build Tools or full Visual Studio with .NET desktop build
  workload.
- .NET Framework 4.8.1 Developer Pack.
- NuGet CLI or Visual Studio package restore for `packages.config` dependencies.
- Perl in `PATH` if release/version stamping must run cleanly.

`packages/` is ignored by git. Restore it locally; do not commit restored
packages.

Debug builds can complete without Perl because current pre-build events invoke
`perl ...` and then `exit 0`. Missing Perl still means assembly version stamping
did not run.

## Tool Discovery

Use these checks before diagnosing build failures:

```powershell
Get-Command msbuild -ErrorAction SilentlyContinue
Get-Command nuget -ErrorAction SilentlyContinue
Get-Command perl -ErrorAction SilentlyContinue
dotnet --info
```

If `msbuild` is not in `PATH`, use a Developer PowerShell for Visual Studio or
resolve it with `vswhere.exe`.

Known working MSBuild discovery on this machine:

```powershell
Get-ChildItem -Path 'C:\Program Files','C:\Program Files (x86)' `
  -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
  Select-Object -First 1 -ExpandProperty FullName
```

On 2026-05-17 this resolved to:

```text
C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
```

## Dev Launcher

For day-to-day local startup from the repository root, use:

```powershell
.\dev.ps1
```

The script locates Visual Studio MSBuild, builds `GARbro.sln` in `Debug`, and
launches `bin\Debug\Onachi-GARbro.exe`. It restores NuGet packages only when
`packages/` is missing or when `-Restore` is passed.

Useful variants:

```powershell
.\dev.ps1 -NoLaunch
.\dev.ps1 -NoBuild
.\dev.ps1 -App Console
.\dev.ps1 -App ImageConvert
```

## Release Build Script

For the common release build and installer package path, use:

```powershell
.\build.ps1
```

The script locates Visual Studio MSBuild, restores NuGet packages when
`packages/` is missing or when `-Restore` is passed, builds `GARbro.sln` in
`Release`, runs `GARbro.nsi` through NSIS, and prints the installer SHA-256
hash.

Useful variants:

```powershell
.\build.ps1 -Restore
.\build.ps1 -Smoke
.\build.ps1 -NoPackage
.\build.ps1 -Configuration Debug -NoPackage -NoVersionStamp
```

`build.ps1` preserves the pre-build version stamping by default. Use
`-NoVersionStamp` only for local verification builds where dirtying
`Properties/AssemblyInfo.cs` is undesirable; do not use it for final release
packages.

## Restore

Preferred restore:

```powershell
nuget restore GARbro.sln
```

If `nuget` is not in `PATH`, bootstrap it outside the repository and run restore:

```powershell
$tools = Join-Path $env:TEMP 'garbro-codex-tools'
New-Item -ItemType Directory -Force $tools | Out-Null
$nuget = Join-Path $tools 'nuget.exe'
if (-not (Test-Path $nuget)) {
  Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget
}
& $nuget restore GARbro.sln -NonInteractive
```

If only MSBuild is available, try:

```powershell
msbuild GARbro.sln /t:Restore /p:RestorePackagesConfig=true
```

Plain `dotnet restore` may report that no packages are restorable because these
are old-style `packages.config` projects.

## Build

Full debug build:

```powershell
msbuild GARbro.sln /m /p:Configuration=Debug /p:Platform="Any CPU"
```

For routine verification where you do not want `inc-revision.pl` to dirty
`Properties/AssemblyInfo.cs`, suppress pre-build version stamping:

```powershell
msbuild GARbro.sln /m /p:Configuration=Debug /p:Platform="Any CPU" /p:PreBuildEvent=
```

When `msbuild` is not in `PATH`, call the discovered executable directly:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  GARbro.sln /m /p:Configuration=Debug /p:Platform="Any CPU" /p:PreBuildEvent= /v:minimal
```

Targeted builds:

```powershell
msbuild GameRes\GameRes.csproj /p:Configuration=Debug /p:Platform="Any CPU"
msbuild ArcFormats\ArcFormats.csproj /p:Configuration=Debug /p:Platform="Any CPU"
msbuild GUI\GARbro.GUI.csproj /p:Configuration=Debug /p:Platform="Any CPU"
```

Use `Prerelease` or `Release` only when the task specifically requires packaging
or release behavior.

## Packaging

The preferred packaging entry point is:

```powershell
.\build.ps1
```

The manual equivalent is to build `Release` before generating a distributable
installer:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  GARbro.sln /m /p:Configuration=Release /p:Platform="Any CPU" /v:minimal
```

The repository includes `GARbro.nsi` for NSIS installer generation. Install NSIS
so `makensis.exe` is available, then run:

```powershell
& 'C:\Program Files (x86)\NSIS\makensis.exe' GARbro.nsi
```

The installer is written to `bin\Package\Onachi-GARbro-setup.exe`.

The GUI project first stages the repo-local Codex skill under
`bin\<Configuration>\Skills\garbro-cli`, then creates the distributable
`bin\<Configuration>\garbro-cli-skill.zip`. The ZIP must contain:

```text
garbro-cli\SKILL.md
garbro-cli\agents\openai.yaml
garbro-cli\references\command-reference.md
garbro-cli\references\script-text-modes.md
garbro-cli\references\machine-protocol.md
garbro-cli\references\extraction-safety.md
garbro-cli\references\large-library-ingest.md
garbro-cli\references\content-semanticization.md
```

`large-library-ingest.md` owns the scheme-check, archive-plan, deterministic
duplicate, finite auto-budget, manifest/resume, and batch-image workflow.
`content-semanticization.md` states the boundary between GARbro's decoding and
provenance responsibilities and downstream OCR, transcription, translation,
classification, linking, or embedding systems. Keep both references in the ZIP
when any of those workflows change.

`GARbro.nsi` installs the ZIP next to the executables. The settings page saves
an atomic copy to a user-selected path; it does not inspect or modify the
current user's Codex home. After building, verify the package without touching
the real Codex home:

```powershell
.\tests\Installer\Invoke-CodexSkillPackageTests.ps1 -Configuration Release
```

The components page offers an initially unchecked option to add the installation
directory, which contains `Onachi-GARbro.Cli.exe`, to the machine `PATH`.
`Installer\Update-Path.ps1` performs the add/remove operation without the NSIS
string-length limit. The uninstaller removes the directory only when the
installer recorded that it added the entry. Exercise the helper safely in a
child process before packaging:

```powershell
.\tests\Installer\Invoke-PathRegistrationTests.ps1
.\tests\Installer\Invoke-CodexSkillPackageTests.ps1 -Configuration Release
```

Do not run an installer merely to verify this component on a development
machine: installation closes running GARbro processes and changes machine
state. Compile `GARbro.nsi`, inspect the component in verbose compiler output,
and reserve a real install/uninstall smoke for a disposable Windows test
environment.

The XP3 KrkrDump assistant expects bundled KrkrDump runtime files next to the
GUI executable. The repository currently bundles the x86 KrkrDump runtime:

```text
bin\<Configuration>\Tools\KrkrDump\x86\KrkrDumpLoader.exe
bin\<Configuration>\Tools\KrkrDump\x86\KrkrDump.dll
```

The NSIS installer must install `bin\Release\Tools\KrkrDump\` recursively. A
packaged installation should contain:

```text
Tools\KrkrDump\LICENSE.txt
Tools\KrkrDump\README.md
Tools\KrkrDump\x86\KrkrDumpLoader.exe
Tools\KrkrDump\x86\KrkrDump.dll
THIRD-PARTY-NOTICES.txt
```

`GameData\HxNames-LLLJ.lst` is an optional user-installed preset and is not
part of the repository or release package. A local source checkout may keep a
copy at `ArcFormats\Resources\HxNames-LLLJ.lst`; that exact path is ignored by
Git and the ArcFormats post-build resource copy places it in the Debug
`GameData` directory. Non-Debug builds explicitly remove this optional local
file from their output before packaging.

Add `bin\<Configuration>\Tools\KrkrDump\x64\KrkrDumpLoader.exe` and
`bin\<Configuration>\Tools\KrkrDump\x64\KrkrDump.dll` when a working x64
KrkrDump DLL is available. A flat `Tools\KrkrDump\` directory also works, but
separate architecture folders avoid injecting a DLL with the wrong process
architecture. Local development can also use sibling KrkrDump build outputs
under `..\KrkrDump\Release`, `..\KrkrDump\Win32\Release`,
`..\KrkrDump\x86\Release`, or `..\KrkrDump\x64\Release`.

If these files are missing at runtime, the KrkrDump assistant shows repair
guidance and opens the original KrkrDump repository page on request.

GUI builds add a rolling file trace listener at startup and write diagnostic
output to:

```text
%LOCALAPPDATA%\Onachi\Onachi-GARbro\trace-YYYYMMDD.log
```

Debug GUI builds also write the same diagnostics to
`bin\Debug\trace-YYYYMMDD.log`. Each log directory rolls to
`trace-YYYYMMDD-1.log`, `trace-YYYYMMDD-2.log`, and so on once a file reaches
50 MiB, and keeps the newest 5 `trace-*.log` files.

Script preview and extraction logs include selection counts, entry type counts,
dialog filter flags, filtered counts, per-entry extraction start/done/skip
events, detected script format, requested text mode, output name, file creation
failures, and exception stack traces. KrkrDump XP3 imports log the discovered
KrkrDump artifacts and Hx filename-map hit/miss counters there as well.

## Known Environment Failure Signatures

These usually indicate local toolchain setup, not source regressions:

- `MSB3644` for `.NETFramework,Version=v2.0` under `dotnet build`: use Visual
  Studio MSBuild. The VS MSBuild path above can build `Net20` on this machine.
- Missing `..\packages\...\*.targets`: packages were not restored with NuGet.
- `perl is not recognized`: `inc-revision.pl` cannot run; Debug builds may still
  complete, but version stamping was skipped.
- Many unresolved package assemblies such as `NAudio` or `Newtonsoft.Json`:
  `packages/` is absent or incomplete.

When reporting these, include the exact command and the first blocking error.

## Smoke Checks

After a successful build, use the smallest relevant smoke check:

```powershell
bin\Debug\Onachi-GARbro.Cli.exe capabilities --output json --non-interactive
bin\Debug\Onachi-GARbro.Console.exe -l
bin\Debug\Onachi-GARbro.Image.Convert.exe -l
```

For the machine CLI protocol, extraction safety, synthetic script/ZIP cases,
and optional local YPF/JPEG samples:

```powershell
.\tests\Cli\Invoke-CliTests.ps1 -Configuration Debug

.\tests\Cli\Invoke-CliTests.ps1 `
  -Configuration Debug `
  -SampleRoot "I:\TempDays\[Whirlpool][201903]pieces／渡り鳥のソムニウム"

.\tests\Cli\Invoke-CliTests.ps1 `
  -Configuration Debug `
  -HxV4UpstreamRoot "C:\path\to\hxv4_unhash_tools"

.\tests\Installer\Invoke-PathRegistrationTests.ps1
```

For a `Formats.dat` conflict, first keep Git stages 1/2/3 intact and build
`SchemeTool`. Analyze the database semantically, review every decision, and
bind the write to the reviewed report hash:

```powershell
.\scripts\Merge-FormatsDatabase.ps1 -Mode Analyze -Configuration Debug

.\scripts\Merge-FormatsDatabase.ps1 `
  -Mode Merge `
  -Configuration Debug `
  -ApprovedReportSha256 <reviewed-report-sha256>

.\tests\SchemeTool\Invoke-SchemeDatabaseMergeTests.ps1 -Configuration Debug
```

The script extracts binary conflict stages with `git cat-file`, never through a
text pipeline. Analysis does not change the worktree binary. Merge mode reruns
the deterministic analysis and refuses to write if the approved SHA-256 no
longer matches. It also refuses semantic conflicts and never runs `git add`.
Inputs must be trusted repository artifacts because the legacy database uses
`BinaryFormatter`. The complete Agent review and explicit three-file form are
documented in
`.codex/skills/garbro-format-authoring/references/scheme-database-merge.md`.

When invoked from PowerShell Core, the CLI E2E script relaunches itself under
Windows PowerShell 5.1. The generated XP3 fixtures exercise legacy .NET
Framework serialization behavior that newer PowerShell runtimes no longer
enable; this relaunch is expected and does not change the requested test scope.

The test script creates output only below a unique system temporary directory.
The external sample path is read-only test input and no sample data is added to
the repository. `-HxV4UpstreamRoot` uses a separate checkout as a behavioral
oracle and does not copy its source or binaries into GARbro. See
`docs/reference/cli-machine-interface.md` for the command and protocol contract.

Validated on 2026-08-01 for CLI large-workflow hardening:

- The complete Debug solution build passed under Visual Studio MSBuild with
  version stamping suppressed. The only warning was the pre-existing unresolved
  `Microsoft.Win32.Primitives` reference in `Experimental`; there were no build
  errors.
- The comprehensive CLI E2E passed 2,289 assertions in 195.4 seconds without
  external samples. It included a 50,001-entry XP3 JSONL workflow below the
  512 MiB peak-working-set ceiling, duplicate/path/budget enforcement,
  extraction-manifest crash/resume/repair cases, image batch conversion, Hx/Cx
  validation, GUI inline-name precedence, and explicit plus auto-detected lazy
  TPM fresh/resume/change-rejection cases.
- The repo-local skill passed `quick_validate.py`; the Debug packaged-skill test
  passed 96 assertions, confirmed exact source/ZIP content, atomic save/replace,
  and did not change the real Codex home.
- CLI capabilities, Console listing (659 lines), and Image.Convert listing
  (442 lines) smoke checks all exited 0.
- Read-only commercial-sample checks opened and content-validated a 118-entry
  protected XP3 without modifying its Cx inputs, planned and dry-ran a
  51,248-entry voice XP3 without creating output, and recognized an
  extensionless 2,120-by-1,280 PNG in batch signature-detection mode.

Validated on 2026-08-01 for the upstream semantic database merge workflow:

- A real three-way `Formats.dat` conflict produced a deterministic 44-change,
  zero-conflict report; a second analysis produced the same approval SHA-256.
- Agent review preserved three fork decisions and 41 upstream decisions. The
  merged database round-tripped at version 153 with 80 schemes and 1,151 game
  mappings, and its semantic hash matched the approved report.
- The scheme merge E2E passed 40 assertions, including stale/wrong approval,
  path-collision rejection, and conflict no-output behavior. The full CLI E2E
  passed 2,289 assertions, the packaged-skill test passed 96 assertions, and Console,
  Image.Convert, and CLI capability smoke checks exited 0.

Validated on 2026-05-23 after NuGet restore and MSBuild Debug build:

- `Onachi-GARbro.Console.exe -l`: 656 output lines.
- `Onachi-GARbro.Image.Convert.exe -l`: 440 output lines.
- `Onachi-GARbro.exe`: started and stayed alive for 3 seconds in a controlled
  smoke run.

Validated on 2026-07-27 for the machine CLI:

- Release solution build and CLI/Console/Image.Convert smoke passed.
- Debug and Release CLI E2E each passed 173 assertions.
- The Release NSIS package included the CLI executable and config.
- The installer PATH helper passed 7 process-scoped assertions without changing
  the user or machine environment, and NSIS compiled both add and uninstall
  cleanup paths.
- The packaged Codex skill ZIP test passed 39 assertions, including exact
  source/package content equality, required multi-document references, safe ZIP
  paths, save-to-disk behavior, and atomic replacement under a temporary
  directory. It did not change the real Codex home.

Validated on 2026-07-30 for native Hx v4 CLI parity:

- Visual Studio MSBuild package restore and the complete Debug solution build
  passed with version stamping suppressed.
- CLI E2E passed 1,102 assertions, including all advertised Hx v4 commands,
  hash vectors, source/seed generation, clean/restore/rename safety, KrkrDump
  discovery and import without implicit resource generation, native PSB/PBD
  fixtures (including encrypted PackinOne `TJS/4s0`), and the optional upstream
  candidate differential.
- An actual elevated KrkrDump game launch was not automated because it requires
  an installed compatible game and visible UAC/game interaction.

For a changed archive handler:

```powershell
bin\Debug\Onachi-GARbro.Console.exe path\to\sample.arc
bin\Debug\Onachi-GARbro.Console.exe -x path\to\sample.arc
```

For a changed image handler:

```powershell
bin\Debug\Onachi-GARbro.Image.Convert.exe path\to\sample.image
bin\Debug\Onachi-GARbro.Image.Convert.exe -t PNG path\to\sample.image
```

Do not invent arbitrary files and claim they validate a format handler. The CLI
suite's generated ZIP/KiriKiri cases are deterministic protocol and safety
fixtures; use real samples for format-specific behavior, or state that it was
build-verified only.
