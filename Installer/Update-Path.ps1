[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Add", "Remove")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Machine", "User", "Process")]
    [string]$Scope,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TargetPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Normalize-PathEntry {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $expanded = [Environment]::ExpandEnvironmentVariables(
        $Value.Trim().Trim('"'))
    $root = [IO.Path]::GetPathRoot($expanded)
    if (-not [string]::Equals(
        $expanded, $root, [StringComparison]::OrdinalIgnoreCase)) {
        $expanded = $expanded.TrimEnd('\', '/')
    }
    return $expanded
}

try {
    $scopeValue = [EnvironmentVariableTarget][Enum]::Parse(
        [EnvironmentVariableTarget], $Scope, $true)
    $target = Normalize-PathEntry $TargetPath
    if ([string]::IsNullOrWhiteSpace($target)) {
        throw "TargetPath resolves to an empty value."
    }

    $current = [Environment]::GetEnvironmentVariable("Path", $scopeValue)
    if ($null -eq $current) {
        $current = ""
    }
    $entries = $current.Split(
        [char[]]@(';'), [StringSplitOptions]::None)
    $matches = @(
        $entries | Where-Object {
            [string]::Equals(
                (Normalize-PathEntry $_),
                $target,
                [StringComparison]::OrdinalIgnoreCase)
        }
    )

    if ($Action -eq "Add") {
        if ($matches.Count -gt 0) {
            Write-Output "AlreadyPresent"
            exit 10
        }
        if ([string]::IsNullOrEmpty($current)) {
            $updated = $target
        }
        elseif ($current.EndsWith(";", [StringComparison]::Ordinal)) {
            $updated = $current + $target
        }
        else {
            $updated = $current + ";" + $target
        }
        [Environment]::SetEnvironmentVariable(
            "Path", $updated, $scopeValue)
        Write-Output "Added"
        exit 0
    }

    if ($matches.Count -eq 0) {
        Write-Output "NotPresent"
        exit 0
    }
    $remaining = @(
        $entries | Where-Object {
            -not [string]::Equals(
                (Normalize-PathEntry $_),
                $target,
                [StringComparison]::OrdinalIgnoreCase)
        }
    )
    [Environment]::SetEnvironmentVariable(
        "Path", ($remaining -join ";"), $scopeValue)
    Write-Output "Removed"
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
