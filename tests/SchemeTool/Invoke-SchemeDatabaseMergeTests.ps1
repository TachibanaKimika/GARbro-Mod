[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$toolPath = Join-Path $repoRoot "bin\$Configuration\Onachi-GARbro.SchemeTool.exe"
$workflowPath = Join-Path $repoRoot "scripts\Merge-FormatsDatabase.ps1"
$windowsPowerShell = Join-Path $env:SystemRoot `
    "System32\WindowsPowerShell\v1.0\powershell.exe"
foreach ($required in $toolPath, $workflowPath, $windowsPowerShell) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required test dependency was not found: $required"
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot `
    ("garbro-scheme-merge-e2e-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:Assertions = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:Assertions++
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    $script:Assertions++
    if ($Expected -ne $Actual) {
        throw "Assertion failed: $Message (expected '$Expected', actual '$Actual')"
    }
}

function Invoke-ProcessCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $stderrPath = Join-Path $testRoot `
        ("stderr-" + [Guid]::NewGuid().ToString("N") + ".txt")
    $stdout = @(& $Executable @Arguments 2> $stderrPath)
    $exitCode = $LASTEXITCODE
    $stderr = if (Test-Path -LiteralPath $stderrPath) {
        [IO.File]::ReadAllText($stderrPath)
    }
    else {
        ""
    }
    if (Test-Path -LiteralPath $stderrPath) {
        Remove-Item -LiteralPath $stderrPath -Force
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = @($stdout)
        Stdout = [string]::Join([Environment]::NewLine, [string[]]$stdout)
        Stderr = $stderr
    }
}

function Invoke-SchemeTool {
    param([string[]]$Arguments)
    return Invoke-ProcessCapture -Executable $toolPath -Arguments $Arguments
}

function Invoke-Workflow {
    param([string[]]$Arguments)
    $hostArguments = @(
        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", $workflowPath
    ) + $Arguments + @("-ToolPath", $toolPath)
    return Invoke-ProcessCapture -Executable $windowsPowerShell -Arguments $hostArguments
}

function Read-LastJson {
    param($Result)
    $line = @($Result.Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[-1]
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "Expected JSON output, but stdout was empty. stderr: $($Result.Stderr)"
    }
    return $line | ConvertFrom-Json
}

try {
    $fixtures = Join-Path $testRoot "fixtures"
    $fixtureResult = Invoke-SchemeTool @(
        "database", "create-test-fixtures", "--directory", $fixtures)
    Assert-Equal 0 $fixtureResult.ExitCode "fixture generation exit code"
    Assert-Equal "success" (Read-LastJson $fixtureResult).status `
        "fixture generation status"
    foreach ($name in "base.dat", "ours.dat", "theirs.dat", "theirs-conflict.dat") {
        Assert-True (Test-Path -LiteralPath (Join-Path $fixtures $name) -PathType Leaf) `
            "fixture '$name' exists"
    }

    $base = Join-Path $fixtures "base.dat"
    $ours = Join-Path $fixtures "ours.dat"
    $theirs = Join-Path $fixtures "theirs.dat"
    $theirsConflict = Join-Path $fixtures "theirs-conflict.dat"
    $missingTrustReport = Join-Path $testRoot "missing-trust.json"
    $missingTrust = Invoke-SchemeTool @(
        "database", "analyze", "--base", $base, "--ours", $ours,
        "--theirs", $theirs, "--report", $missingTrustReport)
    Assert-Equal 2 $missingTrust.ExitCode "untrusted input rejection exit code"
    Assert-True ($missingTrust.Stderr -like "*--trusted-inputs*") `
        "untrusted input rejection explains the opt-in"
    Assert-True (-not (Test-Path -LiteralPath $missingTrustReport)) `
        "untrusted input rejection writes no report"

    $originalBaseHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $base).Hash
    $reportOverInput = Invoke-SchemeTool @(
        "database", "analyze", "--base", $base, "--ours", $ours,
        "--theirs", $theirs, "--report", $base, "--trusted-inputs", "--overwrite")
    Assert-Equal 2 $reportOverInput.ExitCode "report/input collision rejection exit code"
    Assert-Equal $originalBaseHash `
        (Get-FileHash -Algorithm SHA256 -LiteralPath $base).Hash `
        "report/input collision does not change the database"

    $report = Join-Path $testRoot "clean-report.json"
    $analyzeArguments = @(
        "-Mode", "Analyze", "-BasePath", $base, "-OursPath", $ours,
        "-TheirsPath", $theirs, "-ReportPath", $report)
    $analysis1 = Invoke-Workflow $analyzeArguments
    Assert-Equal 0 $analysis1.ExitCode "clean workflow analysis exit code"
    $analysisJson1 = Read-LastJson $analysis1
    Assert-Equal "clean" $analysisJson1.Status "clean workflow analysis status"
    Assert-Equal 0 $analysisJson1.Conflicts "clean workflow conflict count"
    Assert-True ($analysisJson1.Changes -ge 4) "clean workflow records semantic changes"
    Assert-True ($analysisJson1.ReportSha256 -match '^[0-9a-f]{64}$') `
        "clean workflow exposes a report approval hash"

    $analysis2 = Invoke-Workflow $analyzeArguments
    Assert-Equal 0 $analysis2.ExitCode "repeat analysis exit code"
    $analysisJson2 = Read-LastJson $analysis2
    Assert-Equal $analysisJson1.ReportSha256 $analysisJson2.ReportSha256 `
        "analysis report is deterministic"

    $merged = Join-Path $testRoot "merged.dat"
    $wrongApproval = Invoke-Workflow @(
        "-Mode", "Merge", "-BasePath", $base, "-OursPath", $ours,
        "-TheirsPath", $theirs, "-OutputPath", $merged,
        "-ReportPath", $report, "-ApprovedReportSha256", ("0" * 64))
    Assert-True ($wrongApproval.ExitCode -ne 0) "wrong approval hash is rejected"
    Assert-True (-not (Test-Path -LiteralPath $merged)) `
        "wrong approval hash writes no database"

    $sharedOutput = Join-Path $testRoot "shared-output.json"
    $outputOverReport = Invoke-SchemeTool @(
        "database", "merge", "--base", $base, "--ours", $ours,
        "--theirs", $theirs, "--output", $sharedOutput,
        "--report", $sharedOutput, "--trusted-inputs", "--overwrite")
    Assert-Equal 2 $outputOverReport.ExitCode "output/report collision rejection exit code"
    Assert-True (-not (Test-Path -LiteralPath $sharedOutput)) `
        "output/report collision writes no artifact"

    $merge = Invoke-Workflow @(
        "-Mode", "Merge", "-BasePath", $base, "-OursPath", $ours,
        "-TheirsPath", $theirs, "-OutputPath", $merged,
        "-ReportPath", $report,
        "-ApprovedReportSha256", $analysisJson1.ReportSha256)
    Assert-Equal 0 $merge.ExitCode "approved merge exit code"
    $mergeJson = Read-LastJson $merge
    Assert-Equal "clean" $mergeJson.Status "approved merge status"
    Assert-Equal $analysisJson1.ReportSha256 $mergeJson.ReportSha256 `
        "approved report hash remains stable while writing"
    Assert-True (Test-Path -LiteralPath $merged -PathType Leaf) `
        "approved merge writes a database"

    $inspectReport = Join-Path $testRoot "inspect.json"
    $inspect = Invoke-SchemeTool @(
        "database", "inspect", "--input", $merged, "--report", $inspectReport,
        "--trusted-inputs")
    Assert-Equal 0 $inspect.ExitCode "merged database inspection exit code"
    $inspectJson = Read-LastJson $inspect
    $mergeReport = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Assert-Equal 13 $inspectJson.version "merged version increments the maximum input version"
    Assert-Equal 1 $inspectJson.schemeCount "merged scheme count"
    Assert-Equal 3 $inspectJson.gameMapCount "merged game map count"
    Assert-Equal $mergeReport.result.semanticHash $inspectJson.semanticHash `
        "round-tripped output matches the approved semantic result"
    $changePaths = @($mergeReport.changes | ForEach-Object path)
    Assert-True ($changePaths -contains 'schemeMap["TEST"].Values["oursOnly"]') `
        "report records the ours-only nested key"
    Assert-True ($changePaths -contains 'schemeMap["TEST"].Values["theirsOnly"]') `
        "report records the theirs-only nested key"

    $sameOutputReport = Join-Path $testRoot "same-output-report.json"
    $sameOutput = Invoke-SchemeTool @(
        "database", "merge", "--base", $base, "--ours", $ours,
        "--theirs", $theirs, "--output", $ours, "--report", $sameOutputReport,
        "--trusted-inputs")
    Assert-Equal 2 $sameOutput.ExitCode "input overwrite rejection exit code"

    $conflictReport = Join-Path $testRoot "conflict-report.json"
    $conflictAnalysis = Invoke-Workflow @(
        "-Mode", "Analyze", "-BasePath", $base, "-OursPath", $ours,
        "-TheirsPath", $theirsConflict, "-ReportPath", $conflictReport)
    Assert-Equal 3 $conflictAnalysis.ExitCode "semantic conflict analysis exit code"
    $conflictJson = Read-LastJson $conflictAnalysis
    Assert-Equal "conflict" $conflictJson.Status "semantic conflict status"
    Assert-True ($conflictJson.Conflicts -ge 1) "semantic conflict is reported"
    $conflictDocument = Get-Content -LiteralPath $conflictReport -Raw | ConvertFrom-Json
    Assert-True (@($conflictDocument.conflicts).Count -ge 1) `
        "conflict report contains reviewable paths"

    $conflictOutput = Join-Path $testRoot "conflict-output.dat"
    $directConflictReport = Join-Path $testRoot "direct-conflict-report.json"
    $conflictMerge = Invoke-SchemeTool @(
        "database", "merge", "--base", $base, "--ours", $ours,
        "--theirs", $theirsConflict, "--output", $conflictOutput,
        "--report", $directConflictReport, "--trusted-inputs")
    Assert-Equal 3 $conflictMerge.ExitCode "semantic conflict merge exit code"
    Assert-True (-not (Test-Path -LiteralPath $conflictOutput)) `
        "semantic conflict writes no database"

    [pscustomobject]@{
        Assertions = $script:Assertions
        CleanChanges = $analysisJson1.Changes
        ConflictCount = $conflictJson.Conflicts
        DeterministicApproval = $true
        OutputSemanticHash = $inspectJson.semanticHash
    } | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notlike "garbro-scheme-merge-e2e-*") {
            throw "Refusing to clean an unexpected test directory."
        }
        [IO.Directory]::Delete($resolved, $true)
    }
}
