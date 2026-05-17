param(
    [ValidateSet("GUI", "Console", "ImageConvert")]
    [string]$App = "GUI",

    [string]$Configuration = "Debug",

    [switch]$Restore,
    [switch]$NoBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

function Resolve-MSBuild {
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = Get-ChildItem -Path "C:\Program Files", "C:\Program Files (x86)" `
        -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\MSBuild\Current\Bin\MSBuild.exe" } |
        Sort-Object FullName |
        Select-Object -ExpandProperty FullName

    if ($candidates) {
        return $candidates[0]
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

$packagesDir = Join-Path $repoRoot "packages"
$shouldRestore = $Restore -or -not (Test-Path -LiteralPath $packagesDir)

if ($shouldRestore) {
    $nuget = Resolve-NuGet
    Write-Host "Restoring packages..."
    & $nuget restore (Join-Path $repoRoot "GARbro.sln") -NonInteractive
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not $NoBuild) {
    $msbuild = Resolve-MSBuild
    Write-Host "Building GARbro.sln ($Configuration)..."
    & $msbuild (Join-Path $repoRoot "GARbro.sln") `
        /m `
        "/p:Configuration=$Configuration" `
        "/p:Platform=Any CPU" `
        /p:PreBuildEvent= `
        /v:minimal
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if ($NoLaunch) {
    exit 0
}

$exeName = switch ($App) {
    "GUI"          { "Onachi-GARbro.exe" }
    "Console"      { "Onachi-GARbro.Console.exe" }
    "ImageConvert" { "Onachi-GARbro.Image.Convert.exe" }
}

$exe = Join-Path $repoRoot "bin\$Configuration\$exeName"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Executable was not found: $exe"
}

Write-Host "Launching $exeName..."
if ($App -eq "GUI") {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) | Out-Null
} else {
    & $exe
}
