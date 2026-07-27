[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script:Assertions = 0

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $script:Assertions++
    if (-not $Condition) {
        throw $Message
    }
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)]
        [IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) {
        throw "ZIP entry not found: $Name"
    }
    $reader = [IO.StreamReader]::new(
        $entry.Open(), [Text.UTF8Encoding]::new($false))
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$skillRoot = Join-Path $repoRoot ".codex\skills\garbro-cli"
$packagePath = Join-Path `
    $repoRoot "bin\$Configuration\garbro-cli-skill.zip"

Assert-True (Test-Path -LiteralPath $packagePath -PathType Leaf) `
    "The GARbro CLI SKILL ZIP should exist after the GUI build."
Assert-True ((Get-Item -LiteralPath $packagePath).Length -gt 0) `
    "The GARbro CLI SKILL ZIP should not be empty."

$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $required = @(
        "garbro-cli/SKILL.md",
        "garbro-cli/agents/openai.yaml",
        "garbro-cli/references/command-reference.md",
        "garbro-cli/references/script-text-modes.md",
        "garbro-cli/references/machine-protocol.md",
        "garbro-cli/references/extraction-safety.md"
    )
    foreach ($name in $required) {
        Assert-True ($name -in $entryNames) `
            "Required SKILL package entry is missing: $name"
    }

    Assert-True (@(
        $entryNames |
            Where-Object {
                -not $_.StartsWith(
                    "garbro-cli/", [StringComparison]::Ordinal)
            }
    ).Count -eq 0) "Every ZIP entry should be under garbro-cli/."
    Assert-True (@(
        $entryNames |
            Where-Object {
                $_ -match '(^|/)\.\.(/|$)' -or $_.StartsWith("/")
            }
    ).Count -eq 0) "The ZIP should not contain unsafe paths."

    $sourceFiles = @(
        Get-ChildItem -LiteralPath $skillRoot -Recurse -File
    )
    Assert-True ($sourceFiles.Count -eq $archive.Entries.Count) `
        "The ZIP should contain exactly the source SKILL files."

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($sourceFile in $sourceFiles) {
            $relative = $sourceFile.FullName.Substring(
                $skillRoot.Length).TrimStart("\").Replace("\", "/")
            $entryName = "garbro-cli/" + $relative
            $entry = $archive.GetEntry($entryName)
            Assert-True ($null -ne $entry) `
                "Source SKILL file is missing from ZIP: $relative"

            $sourceHash = $sha256.ComputeHash(
                [IO.File]::ReadAllBytes($sourceFile.FullName))
            $stream = $entry.Open()
            try {
                $entryHash = $sha256.ComputeHash($stream)
            }
            finally {
                $stream.Dispose()
            }
            Assert-True (
                [Convert]::ToBase64String($sourceHash) -eq
                [Convert]::ToBase64String($entryHash)) `
                "ZIP content differs from source: $relative"
        }
    }
    finally {
        $sha256.Dispose()
    }

    $skill = Read-ZipEntryText $archive "garbro-cli/SKILL.md"
    foreach ($reference in @(
        "command-reference.md",
        "script-text-modes.md",
        "machine-protocol.md",
        "extraction-safety.md"
    )) {
        Assert-True ($skill.Contains("references/$reference")) `
            "SKILL.md should route readers to $reference."
    }

    $scriptModes = Read-ZipEntryText `
        $archive "garbro-cli/references/script-text-modes.md"
    foreach ($term in @(
        "--mode jsonl",
        "--output jsonl",
        "filtered",
        "raw",
        "dump",
        "<base>.raw.txt",
        "<base>.dump.txt",
        "script_mode_not_supported"
    )) {
        Assert-True ($scriptModes.Contains($term)) `
            "Script mode reference should explain: $term"
    }
}
finally {
    $archive.Dispose()
}

$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("garbro-skill-package-save-test-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $assemblyPath = Join-Path `
        $repoRoot "bin\$Configuration\Onachi-GARbro.exe"
    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $packageType = $assembly.GetType(
        "GARbro.GUI.CodexSkillPackage", $true)
    $saveMethod = $packageType.GetMethod(
        "SaveTo",
        [Reflection.BindingFlags]::Static -bor
            [Reflection.BindingFlags]::NonPublic,
        $null,
        [Type[]]@([string], [string]),
        $null)
    Assert-True ($null -ne $saveMethod) `
        "CodexSkillPackage.SaveTo should be available."

    $savedPath = Join-Path $testRoot "downloaded-skill.zip"
    $saveMethod.Invoke(
        $null, [object[]]@([string]$savedPath, [string]$packagePath))
    Assert-True (Test-Path -LiteralPath $savedPath -PathType Leaf) `
        "The settings-page package service should save the ZIP."
    Assert-True (
        (Get-FileHash -Algorithm SHA256 -LiteralPath $packagePath).Hash -eq
        (Get-FileHash -Algorithm SHA256 -LiteralPath $savedPath).Hash) `
        "The saved ZIP should match the bundled package."

    [IO.File]::WriteAllText($savedPath, "replace-me")
    $saveMethod.Invoke(
        $null, [object[]]@([string]$savedPath, [string]$packagePath))
    Assert-True (
        (Get-FileHash -Algorithm SHA256 -LiteralPath $packagePath).Hash -eq
        (Get-FileHash -Algorithm SHA256 -LiteralPath $savedPath).Hash) `
        "Saving over an approved destination should replace it atomically."
}
finally {
    $resolvedTestRoot = Resolve-Path `
        -LiteralPath $testRoot -ErrorAction SilentlyContinue
    if ($resolvedTestRoot) {
        $resolvedTemp = (Resolve-Path ([IO.Path]::GetTempPath())).Path
        if (-not $resolvedTestRoot.Path.StartsWith(
            $resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove test files outside the temporary directory."
        }
        Remove-Item -LiteralPath $resolvedTestRoot.Path -Recurse -Force
    }
}

[pscustomobject]@{
    Assertions = $script:Assertions
    Configuration = $Configuration
    Package = $packagePath
    RealCodexHomeChanged = $false
} | ConvertTo-Json
