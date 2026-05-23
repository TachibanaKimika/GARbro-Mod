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

Build `Release` before generating a distributable installer:

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

The XP3 KrkrDump assistant expects bundled KrkrDump runtime files next to the
GUI executable. The repository currently bundles the x86 KrkrDump runtime:

```text
bin\<Configuration>\Tools\KrkrDump\x86\KrkrDumpLoader.exe
bin\<Configuration>\Tools\KrkrDump\x86\KrkrDump.dll
```

Add `bin\<Configuration>\Tools\KrkrDump\x64\KrkrDumpLoader.exe` and
`bin\<Configuration>\Tools\KrkrDump\x64\KrkrDump.dll` when a working x64
KrkrDump DLL is available. A flat `Tools\KrkrDump\` directory also works, but
separate architecture folders avoid injecting a DLL with the wrong process
architecture. Local development can also use sibling KrkrDump build outputs
under `..\KrkrDump\Release`, `..\KrkrDump\Win32\Release`,
`..\KrkrDump\x86\Release`, or `..\KrkrDump\x64\Release`.

Debug GUI builds add a `TextWriterTraceListener` at startup and write diagnostic
output to `bin\Debug\trace.log`. KrkrDump XP3 imports log the discovered
KrkrDump artifacts and Hx filename-map hit/miss counters there.

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
bin\Debug\Onachi-GARbro.Console.exe -l
bin\Debug\Onachi-GARbro.Image.Convert.exe -l
```

Validated on 2026-05-23 after NuGet restore and MSBuild Debug build:

- `Onachi-GARbro.Console.exe -l`: 656 output lines.
- `Onachi-GARbro.Image.Convert.exe -l`: 440 output lines.
- `Onachi-GARbro.exe`: started and stayed alive for 3 seconds in a controlled
  smoke run.

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

Do not invent sample files. If samples are unavailable, state that behavior was
build-verified only.
