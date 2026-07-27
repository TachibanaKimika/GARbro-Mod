[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$script:Assertions = 0

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        $Expected,

        [Parameter(Mandatory = $true)]
        $Actual,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $script:Assertions++
    if ($Expected -ne $Actual) {
        throw "$Message Expected: '$Expected'. Actual: '$Actual'."
    }
}

function Invoke-PathHelper {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Add", "Remove")]
        [string]$Action,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $output = & $script:WindowsPowerShell `
        -NoLogo `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $script:PathHelper `
        -Action $Action `
        -Scope Process `
        -TargetPath $TargetPath 2>&1

    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = (@($output) -join [Environment]::NewLine).Trim()
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$script:PathHelper = Join-Path $repoRoot "Installer\Update-Path.ps1"
$script:WindowsPowerShell =
    Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

if (-not (Test-Path -LiteralPath $script:PathHelper -PathType Leaf)) {
    throw "PATH helper not found: $script:PathHelper"
}
if (-not (Test-Path -LiteralPath $script:WindowsPowerShell -PathType Leaf)) {
    throw "Windows PowerShell not found: $script:WindowsPowerShell"
}

$originalPath = $env:Path
$uniqueTarget = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("garbro-cli-path-test-" + [Guid]::NewGuid().ToString("N"))
$existingTarget = @(
    $env:Path.Split(";") |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)[0]

$added = Invoke-PathHelper -Action Add -TargetPath $uniqueTarget
Assert-Equal 0 $added.ExitCode "Adding a new process PATH entry should succeed."
Assert-Equal "Added" $added.Output "The add result should be machine-readable."

$alreadyPresent = Invoke-PathHelper -Action Add -TargetPath $existingTarget
Assert-Equal 10 $alreadyPresent.ExitCode `
    "Adding an existing process PATH entry should report an owned no-op."
Assert-Equal "AlreadyPresent" $alreadyPresent.Output `
    "The existing-entry result should be machine-readable."

$removed = Invoke-PathHelper -Action Remove -TargetPath $existingTarget
Assert-Equal 0 $removed.ExitCode `
    "Removing an existing process PATH entry should succeed."
Assert-Equal "Removed" $removed.Output `
    "The remove result should be machine-readable."

Assert-Equal $originalPath $env:Path `
    "Child-process tests must not change the caller's PATH."

[pscustomobject]@{
    Assertions = $script:Assertions
    Scope = "Process"
    SystemPathChanged = $false
} | ConvertTo-Json
