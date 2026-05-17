---
name: garbro-build-verify
description: Use when building, restoring, smoke-testing, or diagnosing verification failures in GARbro-Mod-Onachi. Handles this legacy .NET Framework solution's Visual Studio MSBuild, NuGet packages.config restore, dotnet-build pitfalls, Perl version-stamp warning, and console/GUI smoke-test requirements.
---

# GARbro Build Verify

Use this skill to verify code changes or diagnose local build failures without
confusing environment gaps for source regressions.

## Workflow

1. Inspect toolchain:

   ```powershell
   Get-Command msbuild -ErrorAction SilentlyContinue
   Get-Command nuget -ErrorAction SilentlyContinue
   Get-Command perl -ErrorAction SilentlyContinue
   dotnet --info
   ```

2. Read `docs/reference/build-and-verify.md`.

3. Resolve MSBuild. If `msbuild` is not in `PATH`, search common Visual Studio
   locations:

   ```powershell
   Get-ChildItem -Path 'C:\Program Files','C:\Program Files (x86)' `
     -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
     Select-Object -First 1 -ExpandProperty FullName
   ```

4. Restore dependencies. If NuGet is not in `PATH`, download `nuget.exe` to a
   temp tools directory outside the repository:

   ```powershell
   $tools = Join-Path $env:TEMP 'garbro-codex-tools'
   New-Item -ItemType Directory -Force $tools | Out-Null
   $nuget = Join-Path $tools 'nuget.exe'
   if (-not (Test-Path $nuget)) {
     Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile $nuget
   }
   & $nuget restore GARbro.sln -NonInteractive
   ```

   Or, when NuGet is available:

   ```powershell
   nuget restore GARbro.sln
   ```

5. Build with Visual Studio MSBuild:

   ```powershell
   msbuild GARbro.sln /m /p:Configuration=Debug /p:Platform="Any CPU"
   ```

   For routine verification, suppress version-stamp pre-build edits so the
   working tree stays clean:

   ```powershell
   msbuild GARbro.sln /m /p:Configuration=Debug /p:Platform="Any CPU" /p:PreBuildEvent=
   ```

6. Use targeted builds for narrow changes:

   ```powershell
   msbuild GameRes\GameRes.csproj /p:Configuration=Debug /p:Platform="Any CPU"
   msbuild ArcFormats\ArcFormats.csproj /p:Configuration=Debug /p:Platform="Any CPU"
   msbuild GUI\GARbro.GUI.csproj /p:Configuration=Debug /p:Platform="Any CPU"
   ```

7. Use `dotnet build GARbro.sln -c Debug` only as a fallback or diagnostic.
   It can fail on `.NETFramework,Version=v2.0` even when Visual Studio MSBuild
   can build the solution.

8. After a successful build, run relevant smoke checks:

   ```powershell
   bin\Debug\Onachi-GARbro.Console.exe -l
   bin\Debug\Onachi-GARbro.Image.Convert.exe -l
   ```

9. For GUI startup smoke, launch briefly and stop the process:

   ```powershell
   $p = Start-Process -FilePath (Resolve-Path '.\bin\Debug\Onachi-GARbro.exe') -PassThru -WindowStyle Minimized
   Start-Sleep -Seconds 3
   if ($p.HasExited) { "GUI_EXITED_CODE=$($p.ExitCode)" } else { Stop-Process -Id $p.Id -Force; "GUI_STARTED_AND_STAYED_ALIVE" }
   ```

10. For format changes with samples, run the relevant sample command:

   ```powershell
   bin\Debug\Onachi-GARbro.Console.exe path\to\sample.arc
   bin\Debug\Onachi-GARbro.Console.exe -x path\to\sample.arc
   bin\Debug\Onachi-GARbro.Image.Convert.exe path\to\sample.image
   ```

## Reporting Rules

- Separate `source failure` from `environment blocked`.
- For environment blockers, report the first missing prerequisite and the exact
  command that exposed it.
- If samples are missing, say that format behavior was not sample-verified.
- Do not install system toolchains unless the user explicitly asks.

## Known Environment Blockers

- Missing `msbuild`: not in Developer PowerShell or Visual Studio Build Tools
  absent.
- Missing `nuget`: packages cannot be restored for `packages.config`.
- Missing `perl`: `inc-revision.pl` version stamping is skipped; Debug build can
  still pass because the event ends with `exit 0`.
- `MSB3644` for .NET Framework v2.0 under `dotnet build`: use Visual Studio
  MSBuild.
- Missing `..\packages\...\*.targets` or unresolved assemblies: local packages
  were not restored.
