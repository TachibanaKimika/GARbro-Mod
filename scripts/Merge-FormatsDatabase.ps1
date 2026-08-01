[CmdletBinding()]
param(
    [ValidateSet("Analyze", "Merge")]
    [string]$Mode = "Analyze",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$ConflictPath = "ArcFormats/Resources/Formats.dat",
    [string]$BasePath,
    [string]$OursPath,
    [string]$TheirsPath,
    [string]$OutputPath,
    [string]$ReportPath,
    [string]$ApprovedReportSha256,
    [string]$ToolPath
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path, [string]$BaseDirectory)

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Path))
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & git -C $RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return @($output)
}

function Export-GitBlob {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ObjectId,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if ($ObjectId -notmatch '^[0-9a-fA-F]{40,64}$') {
        throw "Invalid git object id."
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = "git"
    $start.WorkingDirectory = $RepositoryRoot
    $start.Arguments = "cat-file blob $ObjectId"
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) {
        throw "Could not start git cat-file."
    }
    try {
        $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $process.StandardOutput.BaseStream.CopyTo($stream)
        }
        finally {
            $stream.Dispose()
        }
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file failed: $errorText"
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-ConflictInputs {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TemporaryDirectory
    )

    $rows = Invoke-GitText -RepositoryRoot $RepositoryRoot -Arguments @("ls-files", "-u", "--", $Path)
    $objects = @{}
    foreach ($row in $rows) {
        if ($row -match '^[0-7]+\s+([0-9a-fA-F]{40,64})\s+([123])\t') {
            $objects[[int]$Matches[2]] = $Matches[1]
        }
    }
    foreach ($stage in 1, 2, 3) {
        if (-not $objects.ContainsKey($stage)) {
            throw "Git conflict stage $stage is missing for $Path."
        }
    }

    $paths = @{
        Base = Join-Path $TemporaryDirectory "base.dat"
        Ours = Join-Path $TemporaryDirectory "ours.dat"
        Theirs = Join-Path $TemporaryDirectory "theirs.dat"
    }
    Export-GitBlob -RepositoryRoot $RepositoryRoot -ObjectId $objects[1] -Destination $paths.Base
    Export-GitBlob -RepositoryRoot $RepositoryRoot -ObjectId $objects[2] -Destination $paths.Ours
    Export-GitBlob -RepositoryRoot $RepositoryRoot -ObjectId $objects[3] -Destination $paths.Theirs
    return $paths
}

function Invoke-SchemeTool {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $lines = & $Executable @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = @($lines)
    }
}

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = (Invoke-GitText -RepositoryRoot $scriptDirectory `
    -Arguments @("rev-parse", "--show-toplevel") | Select-Object -First 1).Trim()
$repositoryRoot = [IO.Path]::GetFullPath($repositoryRoot)

if (-not $ToolPath) {
    $ToolPath = Join-Path $repositoryRoot "bin\$Configuration\Onachi-GARbro.SchemeTool.exe"
}
$ToolPath = Resolve-FullPath -Path $ToolPath -BaseDirectory $repositoryRoot
if (-not (Test-Path -LiteralPath $ToolPath -PathType Leaf)) {
    throw "SchemeTool was not found at $ToolPath. Build the solution first."
}

if (-not $ReportPath) {
    $ReportPath = Join-Path $repositoryRoot ".git\garbro-formats-merge-report.json"
}
$ReportPath = Resolve-FullPath -Path $ReportPath -BaseDirectory $repositoryRoot
$reportDirectory = Split-Path -Parent $ReportPath
if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
    throw "Report directory does not exist: $reportDirectory"
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) `
    ("garbro-formats-merge-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

try {
    $explicitInputs = @($BasePath, $OursPath, $TheirsPath) | Where-Object { $_ }
    $explicitCount = @($explicitInputs).Count
    if ($explicitCount -ne 0 -and $explicitCount -ne 3) {
        throw "Provide all of -BasePath, -OursPath, and -TheirsPath, or none of them."
    }
    if ($explicitCount -eq 3) {
        $inputs = @{
            Base = Resolve-FullPath -Path $BasePath -BaseDirectory $repositoryRoot
            Ours = Resolve-FullPath -Path $OursPath -BaseDirectory $repositoryRoot
            Theirs = Resolve-FullPath -Path $TheirsPath -BaseDirectory $repositoryRoot
        }
    }
    else {
        $inputs = Get-ConflictInputs -RepositoryRoot $repositoryRoot `
            -Path $ConflictPath -TemporaryDirectory $temporaryDirectory
    }

    if ($Mode -eq "Analyze") {
        $result = Invoke-SchemeTool -Executable $ToolPath -Arguments @(
            "database", "analyze",
            "--base", $inputs.Base,
            "--ours", $inputs.Ours,
            "--theirs", $inputs.Theirs,
            "--report", $ReportPath,
            "--trusted-inputs", "--overwrite")
        if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
            throw "SchemeTool did not produce an analysis report: $($result.Lines -join [Environment]::NewLine)"
        }
        $reportHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ReportPath).Hash.ToLowerInvariant()
        $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
        [pscustomobject]@{
            Status = $report.status
            Mode = "Analyze"
            ReportPath = $ReportPath
            ReportSha256 = $reportHash
            Changes = $report.summary.changes
            Conflicts = $report.summary.conflicts
            NextStep = if ($result.ExitCode -eq 0) {
                "Review the report, then rerun with -Mode Merge -ApprovedReportSha256 $reportHash"
            } else {
                "Resolve semantic conflicts before producing output."
            }
        } | ConvertTo-Json -Compress
        exit $result.ExitCode
    }

    if ($ApprovedReportSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Merge mode requires the SHA-256 of a reviewed clean report in -ApprovedReportSha256."
    }
    if (-not $OutputPath) {
        if ($explicitCount -eq 3) {
            throw "Explicit inputs require -OutputPath in Merge mode."
        }
        $OutputPath = $ConflictPath
    }
    $OutputPath = Resolve-FullPath -Path $OutputPath -BaseDirectory $repositoryRoot

    $freshReport = Join-Path $temporaryDirectory "fresh-analysis.json"
    $analysis = Invoke-SchemeTool -Executable $ToolPath -Arguments @(
        "database", "analyze",
        "--base", $inputs.Base,
        "--ours", $inputs.Ours,
        "--theirs", $inputs.Theirs,
        "--report", $freshReport,
        "--trusted-inputs")
    if ($analysis.ExitCode -ne 0) {
        throw "Fresh analysis is not clean: $($analysis.Lines -join [Environment]::NewLine)"
    }
    $freshHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $freshReport).Hash.ToLowerInvariant()
    if (-not [string]::Equals($freshHash, $ApprovedReportSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Reviewed report hash does not match the current merge inputs. Expected $ApprovedReportSha256, current $freshHash."
    }

    $merge = Invoke-SchemeTool -Executable $ToolPath -Arguments @(
        "database", "merge",
        "--base", $inputs.Base,
        "--ours", $inputs.Ours,
        "--theirs", $inputs.Theirs,
        "--output", $OutputPath,
        "--report", $ReportPath,
        "--trusted-inputs", "--overwrite")
    if ($merge.ExitCode -ne 0) {
        throw "Scheme database merge failed: $($merge.Lines -join [Environment]::NewLine)"
    }
    $mergedReportHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ReportPath).Hash.ToLowerInvariant()
    if (-not [string]::Equals($mergedReportHash, $freshHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Merge report changed after approval; output must not be staged."
    }
    $mergeResult = $merge.Lines | Select-Object -Last 1 | ConvertFrom-Json
    [pscustomobject]@{
        Status = "clean"
        Mode = "Merge"
        ReportPath = $ReportPath
        ReportSha256 = $mergedReportHash
        OutputPath = $OutputPath
        OutputSha256 = $mergeResult.outputSha256
        ResultSemanticHash = $mergeResult.resultSemanticHash
        NextStep = "Inspect and build the merged tree; git add the binary only after review."
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
        $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemporary.StartsWith($resolvedTempRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTemporary) -notlike "garbro-formats-merge-*") {
            throw "Refusing to clean an unexpected temporary directory."
        }
        [IO.Directory]::Delete($resolvedTemporary, $true)
    }
}
