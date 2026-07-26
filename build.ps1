param(
    [ValidateSet("Debug", "Prerelease", "Release")]
    [string]$Configuration = "Release",

    [switch]$Restore,
    [switch]$NoRestore,
    [switch]$NoPackage,
    [switch]$Smoke,
    [switch]$NoVersionStamp
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

function Resolve-MSBuild {
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin\MSBuild.exe" |
            Select-Object -First 1
        if ($found) {
            return $found
        }
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools or run this from a Developer PowerShell."
}

function Resolve-NuGet {
    $cmd = Get-Command nuget -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $tools = Join-Path $env:TEMP "garbro-codex-tools"
    New-Item -ItemType Directory -Force $tools | Out-Null

    $nuget = Join-Path $tools "nuget.exe"
    if (-not (Test-Path -LiteralPath $nuget)) {
        Write-Host "Downloading NuGet CLI to $nuget"
        Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nuget
    }
    return $nuget
}

function Resolve-MakeNSIS {
    $cmd = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\Bin\makensis.exe",
        "$env:ProgramFiles\NSIS\makensis.exe",
        "$env:ProgramFiles\NSIS\Bin\makensis.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "makensis.exe was not found. Install NSIS or pass -NoPackage to build without generating the installer."
}

if ($Restore -and $NoRestore) {
    throw "Use either -Restore or -NoRestore, not both."
}

$shouldPackage = -not $NoPackage
if ($shouldPackage -and $Configuration -ne "Release") {
    throw "GARbro.nsi packages bin\Release. Use -Configuration Release or pass -NoPackage."
}

$packagesDir = Join-Path $repoRoot "packages"
$shouldRestore = -not $NoRestore -and ($Restore -or -not (Test-Path -LiteralPath $packagesDir))

if ($shouldRestore) {
    $nuget = Resolve-NuGet
    Write-Host "Restoring packages..."
    & $nuget restore (Join-Path $repoRoot "GARbro.sln") -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$msbuild = Resolve-MSBuild
$buildArgs = @(
    (Join-Path $repoRoot "GARbro.sln"),
    "/m",
    "/p:Configuration=$Configuration",
    "/p:Platform=Any CPU",
    "/v:minimal"
)

if ($NoVersionStamp) {
    $buildArgs += "/p:PreBuildEvent="
}

Write-Host "Building GARbro.sln ($Configuration)..."
& $msbuild @buildArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Smoke) {
    $smokeCommands = @(
        @{ Path = Join-Path $repoRoot "bin\$Configuration\Onachi-GARbro.Console.exe"; Args = @("-l") },
        @{ Path = Join-Path $repoRoot "bin\$Configuration\Onachi-GARbro.Image.Convert.exe"; Args = @("-l") }
    )

    foreach ($command in $smokeCommands) {
        $commandPath = $command["Path"]
        $commandArgs = $command["Args"]

        if (-not (Test-Path -LiteralPath $commandPath)) {
            throw "Smoke target was not found: $commandPath"
        }

        $name = Split-Path -Leaf $commandPath
        Write-Host "Smoke testing $name..."
        & $commandPath @commandArgs | Out-Null
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
}

if ($shouldPackage) {
    $makensis = Resolve-MakeNSIS
    New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "bin\Package") | Out-Null

    Write-Host "Packaging installer with NSIS..."
    & $makensis (Join-Path $repoRoot "GARbro.nsi")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $installer = Join-Path $repoRoot "bin\Package\Onachi-GARbro-setup.exe"
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "Installer was not produced: $installer"
    }

    $hash = Get-FileHash -Algorithm SHA256 -Path $installer
    Write-Host "Installer: $installer"
    Write-Host "SHA256: $($hash.Hash)"
} else {
    Write-Host "Build output: $(Join-Path $repoRoot "bin\$Configuration")"
}
