[CmdletBinding()]
param(
    [ValidateSet("Debug", "Prerelease", "Release")]
    [string]$Configuration = "Debug",

    [string]$SampleRoot
)

$ErrorActionPreference = "Stop"
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cliPath = Join-Path $repoRoot "bin\$Configuration\Onachi-GARbro.Cli.exe"
if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
    throw "GARbro CLI was not found: $cliPath"
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempBase ("garbro-cli-e2e-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null

$script:assertions = 0
$script:cliPath = $cliPath
$script:testRoot = $testRoot

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )
    $script:assertions++
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Equal {
    param(
        $Expected,
        $Actual,
        [string]$Message
    )
    $script:assertions++
    if ($Expected -ne $Actual) {
        throw "Assertion failed: $Message (expected '$Expected', actual '$Actual')"
    }
}

function Invoke-Cli {
    param([string[]]$Arguments)

    $stderrPath = Join-Path $script:testRoot (
        "stderr-" + [guid]::NewGuid().ToString("N") + ".txt")
    $stdout = @(& $script:cliPath @Arguments 2> $stderrPath)
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
        Arguments = $Arguments
        ExitCode = $exitCode
        Lines = @($stdout)
        Stdout = [string]::Join([Environment]::NewLine, [string[]]$stdout)
        Stderr = $stderr
    }
}

function Read-JsonEnvelope {
    param($Result)

    Assert-Equal 1 @($Result.Lines).Count (
        "JSON mode must write exactly one stdout line: " + ($Result.Arguments -join " "))
    try {
        $value = $Result.Stdout | ConvertFrom-Json
    }
    catch {
        throw "Invalid JSON stdout for '$($Result.Arguments -join " ")': $($Result.Stdout)"
    }
    Assert-Equal "garbro.cli/v1" $value.schemaVersion "schema version"
    Assert-True (-not [string]::IsNullOrWhiteSpace($value.operationId)) "operationId"
    return $value
}

function Read-JsonLines {
    param($Result)

    $values = @()
    foreach ($line in @($Result.Lines)) {
        try {
            $value = $line | ConvertFrom-Json
        }
        catch {
            throw "Invalid JSONL stdout line: $line"
        }
        Assert-Equal "garbro.cli/v1" $value.schemaVersion "JSONL schema version"
        $values += $value
    }
    Assert-True ($values.Count -gt 0) "JSONL output must not be empty"
    $operationIds = @($values | Select-Object -ExpandProperty operationId -Unique)
    Assert-Equal 1 $operationIds.Count "JSONL operationId must be stable"
    Assert-True ($values[-1].event -in @("summary", "error", "needs_input")) (
        "JSONL must end with a terminal event")
    return $values
}

function New-MaliciousZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry("../escape.txt")
        $stream = $entry.Open()
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes("escape")
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $zip.Dispose()
        $file.Dispose()
    }
}

function New-PartialZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @("a.txt", "b.txt")) {
            $entry = $zip.CreateEntry($name)
            $stream = $entry.Open()
            try {
                $bytes = [Text.Encoding]::UTF8.GetBytes("content-" + $name)
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $stream.Dispose()
            }
        }
    }
    finally {
        $zip.Dispose()
        $file.Dispose()
    }
}

function New-UnderdeclaredZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry(
            "large.txt", [IO.Compression.CompressionLevel]::Optimal)
        $stream = $entry.Open()
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes(("A" * 10000))
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $zip.Dispose()
        $file.Dispose()
    }

    $bytes = [IO.File]::ReadAllBytes($Path)
    $localSignature = [byte[]](0x50, 0x4b, 0x03, 0x04)
    $centralSignature = [byte[]](0x50, 0x4b, 0x01, 0x02)
    $localOffset = Find-ByteSignature -Data $bytes -Signature $localSignature
    $centralOffset = Find-ByteSignature -Data $bytes -Signature $centralSignature
    if ($localOffset -lt 0 -or $centralOffset -lt 0) {
        throw "Could not locate generated ZIP headers."
    }
    [BitConverter]::GetBytes([uint32]1).CopyTo($bytes, $localOffset + 22)
    [BitConverter]::GetBytes([uint32]1).CopyTo($bytes, $centralOffset + 24)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Find-ByteSignature {
    param(
        [byte[]]$Data,
        [byte[]]$Signature
    )

    for ($offset = 0; $offset -le $Data.Length - $Signature.Length; $offset++) {
        $matched = $true
        for ($index = 0; $index -lt $Signature.Length; $index++) {
            if ($Data[$offset + $index] -ne $Signature[$index]) {
                $matched = $false
                break
            }
        }
        if ($matched) {
            return $offset
        }
    }
    return -1
}

function New-EncryptedZip {
    param(
        [string]$Path,
        [string]$SharpZipLibPath
    )

    [void][Reflection.Assembly]::LoadFrom($SharpZipLibPath)
    $file = [IO.File]::Create($Path)
    $zip = [ICSharpCode.SharpZipLib.Zip.ZipOutputStream]::new($file)
    try {
        $zip.Password = "e2e-secret"
        $entry = [ICSharpCode.SharpZipLib.Zip.ZipEntry]::new("secret.txt")
        $entry.DateTime = [DateTime]::Now
        $zip.PutNextEntry($entry)
        $bytes = [Text.Encoding]::UTF8.GetBytes("classified")
        $zip.Write($bytes, 0, $bytes.Length)
        $zip.CloseEntry()
        $zip.Finish()
    }
    finally {
        $zip.Dispose()
        $file.Dispose()
    }
}

try {
    $capabilitiesResult = Invoke-Cli -Arguments @(
        "capabilities", "--output", "json", "--non-interactive")
    Assert-Equal 0 $capabilitiesResult.ExitCode "capabilities exit code"
    Assert-True ([string]::IsNullOrEmpty($capabilitiesResult.Stderr)) (
        "capabilities must not write diagnostics on success")
    $capabilities = Read-JsonEnvelope $capabilitiesResult
    Assert-Equal "success" $capabilities.status "capabilities status"
    Assert-True ($capabilities.data.commands -contains "archive.extract") (
        "capabilities command list")
    Assert-True $capabilities.data.safety.pathContainment "path containment capability"
    Assert-True $capabilities.data.safety.actualByteCounting "actual byte counting capability"

    $formatsResult = Invoke-Cli -Arguments @(
        "formats", "list", "--kind", "script", "--output", "jsonl")
    Assert-Equal 0 $formatsResult.ExitCode "formats list exit code"
    $formats = Read-JsonLines $formatsResult
    Assert-Equal "summary" $formats[-1].event "formats terminal event"
    Assert-True ($formats[-1].data.count -gt 0) "script format count"

    $unknownPath = Join-Path $testRoot "unknown.unknown-resource"
    [IO.File]::WriteAllBytes(
        $unknownPath, [byte[]](222, 173, 190, 239, 17, 34, 51, 68, 85))
    $unknownResult = Invoke-Cli -Arguments @(
        "probe", $unknownPath, "--output", "json")
    Assert-Equal 4 $unknownResult.ExitCode "unknown probe exit code"
    $unknown = Read-JsonEnvelope $unknownResult
    Assert-Equal "unrecognized" $unknown.status "unknown probe status"
    Assert-Equal "format_not_recognized" $unknown.error.code "unknown probe error"

    $scriptPath = Join-Path $testRoot "sample.ks"
    @(
        '@nm name="Alice"',
        'Hello[r]',
        '@nm name="Bob"',
        'World'
    ) | Set-Content -LiteralPath $scriptPath -Encoding utf8

    foreach ($mode in @("filtered", "raw", "dump", "jsonl")) {
        $modeDestination = Join-Path $testRoot ("script-" + $mode)
        $scriptResult = Invoke-Cli -Arguments @(
            "script", "extract", $scriptPath,
            "--mode", $mode,
            "--destination", $modeDestination,
            "--output", "json")
        Assert-Equal 0 $scriptResult.ExitCode "script $mode exit code"
        $scriptEnvelope = Read-JsonEnvelope $scriptResult
        Assert-Equal "KiriKiri/Script" $scriptEnvelope.data.formatTag (
            "script $mode handler")
        Assert-True (Test-Path -LiteralPath $scriptEnvelope.data.destination -PathType Leaf) (
            "script $mode output exists")
        Assert-True ((Get-Item -LiteralPath $scriptEnvelope.data.destination).Length -gt 0) (
            "script $mode output is non-empty")
        if ($mode -eq "jsonl") {
            $rows = @(
                Get-Content -LiteralPath $scriptEnvelope.data.destination |
                    ForEach-Object { $_ | ConvertFrom-Json }
            )
            Assert-Equal 2 $rows.Count "script JSONL row count"
            Assert-Equal "Hello`r`n" $rows[0].message "script first message"
        }
    }

    $maliciousZip = Join-Path $testRoot "malicious.zip"
    New-MaliciousZip -Path $maliciousZip
    $maliciousDestination = Join-Path $testRoot "malicious-out"
    $maliciousResult = Invoke-Cli -Arguments @(
        "archive", "extract", $maliciousZip,
        "--destination", $maliciousDestination,
        "--output", "json")
    Assert-Equal 3 $maliciousResult.ExitCode "malicious archive exit code"
    $malicious = Read-JsonEnvelope $maliciousResult
    Assert-Equal "unsafe_output_path" $malicious.error.code "malicious path error"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot "escape.txt"))) (
        "malicious entry must not escape destination")

    $underdeclaredZip = Join-Path $testRoot "underdeclared.zip"
    New-UnderdeclaredZip -Path $underdeclaredZip
    $underdeclaredListResult = Invoke-Cli -Arguments @(
        "archive", "list", $underdeclaredZip, "--output", "json")
    Assert-Equal 0 $underdeclaredListResult.ExitCode "underdeclared list exit code"
    $underdeclaredList = Read-JsonEnvelope $underdeclaredListResult
    Assert-Equal 1 $underdeclaredList.data.entries[0].unpackedSize (
        "underdeclared fixture index size")
    $underdeclaredDestination = Join-Path $testRoot "underdeclared-out"
    $underdeclaredResult = Invoke-Cli -Arguments @(
        "archive", "extract", $underdeclaredZip,
        "--destination", $underdeclaredDestination,
        "--max-entry-bytes", "100",
        "--output", "json")
    Assert-Equal 3 $underdeclaredResult.ExitCode (
        "actual output byte limit exit code")
    $underdeclared = Read-JsonEnvelope $underdeclaredResult
    Assert-Equal "entry_size_limit_exceeded" $underdeclared.error.code (
        "actual output byte limit code")
    Assert-True (-not (Test-Path -LiteralPath (
        Join-Path $underdeclaredDestination "large.txt"))) (
        "actual output limit must not commit a file")
    $underdeclaredPartials = @(
        Get-ChildItem -LiteralPath $testRoot -Recurse -File |
            Where-Object { $_.Name.EndsWith(".partial") }
    )
    Assert-Equal 0 $underdeclaredPartials.Count (
        "actual output limit must clean temporary files")

    $partialZip = Join-Path $testRoot "partial.zip"
    New-PartialZip -Path $partialZip
    $partialDestination = Join-Path $testRoot "partial-out"
    New-Item -ItemType Directory -Path $partialDestination | Out-Null
    Set-Content -LiteralPath (Join-Path $partialDestination "a.txt") -Value "existing"
    $partialResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialZip,
        "--destination", $partialDestination,
        "--overwrite", "skip",
        "--output", "json")
    Assert-Equal 7 $partialResult.ExitCode "partial extraction exit code"
    $partial = Read-JsonEnvelope $partialResult
    Assert-Equal "partial_success" $partial.status "partial extraction status"
    Assert-Equal 1 $partial.data.written "partial extraction written count"
    Assert-Equal 1 $partial.data.skipped "partial extraction skipped count"
    Assert-Equal "existing" (
        Get-Content -LiteralPath (Join-Path $partialDestination "a.txt") -Raw
    ).Trim() "skip must preserve existing file"
    Assert-True (Test-Path -LiteralPath (Join-Path $partialDestination "b.txt")) (
        "partial extraction writes non-conflicting file")

    $sharpZipLib = Join-Path (
        Split-Path -Parent $cliPath) "ICSharpCode.SharpZipLib.dll"
    Assert-True (Test-Path -LiteralPath $sharpZipLib -PathType Leaf) (
        "SharpZipLib dependency exists")
    $encryptedZip = Join-Path $testRoot "encrypted.zip"
    New-EncryptedZip -Path $encryptedZip -SharpZipLibPath $sharpZipLib
    $needsInputResult = Invoke-Cli -Arguments @(
        "probe", $encryptedZip, "--output", "jsonl", "--non-interactive")
    Assert-Equal 5 $needsInputResult.ExitCode "needs_input exit code"
    $needsInputLines = Read-JsonLines $needsInputResult
    $needsInput = $needsInputLines[-1]
    Assert-Equal "needs_input" $needsInput.event "needs_input terminal event"
    Assert-Equal "needs_input" $needsInput.status "needs_input status"
    Assert-Equal "resource_parameters_required" $needsInput.error.code (
        "needs_input error code")
    Assert-Equal "ZIP" $needsInput.error.details.resourceTag (
        "needs_input resource tag")

    if (-not [string]::IsNullOrWhiteSpace($SampleRoot)) {
        $resolvedSampleRoot = [IO.Path]::GetFullPath($SampleRoot)
        Assert-True (Test-Path -LiteralPath $resolvedSampleRoot -PathType Container) (
            "sample root exists")
        $archivePath = Join-Path $resolvedSampleRoot "pac\update1.ypf"
        Assert-True (Test-Path -LiteralPath $archivePath -PathType Leaf) (
            "update1.ypf sample exists")

        $probeResult = Invoke-Cli -Arguments @(
            "probe", $archivePath, "--output", "json")
        Assert-Equal 0 $probeResult.ExitCode "sample probe exit code"
        $probe = Read-JsonEnvelope $probeResult
        Assert-Equal "YPF" $probe.data.tag "sample archive tag"

        $listResult = Invoke-Cli -Arguments @(
            "archive", "list", $archivePath, "--output", "json")
        Assert-Equal 0 $listResult.ExitCode "sample list exit code"
        $list = Read-JsonEnvelope $listResult
        Assert-True ($list.data.entryCount -gt 0) "sample archive entries"
        $selectedEntry = $list.data.entries[0]

        $listJsonlResult = Invoke-Cli -Arguments @(
            "archive", "list", $archivePath, "--output", "jsonl")
        Assert-Equal 0 $listJsonlResult.ExitCode "sample JSONL list exit code"
        $listJsonl = Read-JsonLines $listJsonlResult
        Assert-Equal $list.data.entryCount @(
            $listJsonl | Where-Object event -eq "entry"
        ).Count "sample JSONL entry count"
        Assert-Equal "summary" $listJsonl[-1].event (
            "sample JSONL list terminal event")

        $dryDestination = Join-Path $testRoot "sample-dry"
        $dryResult = Invoke-Cli -Arguments @(
            "archive", "extract", $archivePath,
            "--destination", $dryDestination,
            "--entry", $selectedEntry.name,
            "--dry-run",
            "--output", "jsonl")
        Assert-Equal 0 $dryResult.ExitCode "sample dry-run exit code"
        $dryLines = Read-JsonLines $dryResult
        $dry = $dryLines[-1]
        Assert-Equal "summary" $dry.event "sample dry-run terminal event"
        Assert-Equal 1 $dry.data.planned "sample dry-run planned count"
        Assert-True (-not (Test-Path -LiteralPath $dryDestination)) (
            "dry-run must not create destination")

        $extractDestination = Join-Path $testRoot "sample-extract"
        $extractResult = Invoke-Cli -Arguments @(
            "archive", "extract", $archivePath,
            "--destination", $extractDestination,
            "--entry", $selectedEntry.name,
            "--output", "json")
        Assert-Equal 0 $extractResult.ExitCode "sample extract exit code"
        $extract = Read-JsonEnvelope $extractResult
        Assert-Equal 1 $extract.data.written "sample written count"
        $extractedPath = $extract.data.files[0].path
        Assert-True (Test-Path -LiteralPath $extractedPath -PathType Leaf) (
            "sample extracted file exists")
        Assert-Equal $extract.data.files[0].actualBytes (
            Get-Item -LiteralPath $extractedPath).Length (
            "sample actual byte count")

        $conflictResult = Invoke-Cli -Arguments @(
            "archive", "extract", $archivePath,
            "--destination", $extractDestination,
            "--entry", $selectedEntry.name,
            "--output", "json")
        Assert-Equal 6 $conflictResult.ExitCode "sample overwrite conflict exit code"
        $conflict = Read-JsonEnvelope $conflictResult
        Assert-Equal "conflict" $conflict.status "sample overwrite conflict status"

        $limitResult = Invoke-Cli -Arguments @(
            "archive", "extract", $archivePath,
            "--destination", (Join-Path $testRoot "sample-limit"),
            "--entry", $selectedEntry.name,
            "--max-entry-bytes", "1",
            "--output", "json")
        Assert-Equal 3 $limitResult.ExitCode "sample entry limit exit code"
        $limit = Read-JsonEnvelope $limitResult
        Assert-Equal "entry_size_limit_exceeded" $limit.error.code (
            "sample entry limit code")

        $partialFiles = @(
            Get-ChildItem -LiteralPath $testRoot -Recurse -File |
                Where-Object { $_.Name.EndsWith(".partial") }
        )
        Assert-Equal 0 $partialFiles.Count "partial files must be cleaned"

        $imagePath = Get-ChildItem -LiteralPath $resolvedSampleRoot -Recurse -File |
            Where-Object { $_.Extension -in @(".jpg", ".jpeg", ".png", ".bmp") } |
            Select-Object -First 1
        Assert-True ($null -ne $imagePath) "sample image exists"

        $imageInfoResult = Invoke-Cli -Arguments @(
            "image", "info", $imagePath.FullName, "--output", "json")
        Assert-Equal 0 $imageInfoResult.ExitCode "sample image info exit code"
        $imageInfo = Read-JsonEnvelope $imageInfoResult
        Assert-True ($imageInfo.data.width -gt 0) "sample image width"
        Assert-True ($imageInfo.data.height -gt 0) "sample image height"

        $imageDestination = Join-Path $testRoot "sample-image"
        $imageConvertResult = Invoke-Cli -Arguments @(
            "image", "convert", $imagePath.FullName,
            "--format", "png",
            "--destination", $imageDestination,
            "--output", "json")
        Assert-Equal 0 $imageConvertResult.ExitCode "sample image convert exit code"
        $imageConvert = Read-JsonEnvelope $imageConvertResult
        Assert-True (Test-Path -LiteralPath $imageConvert.data.destination -PathType Leaf) (
            "sample converted image exists")
        Assert-True ((Get-Item -LiteralPath $imageConvert.data.destination).Length -gt 0) (
            "sample converted image is non-empty")
    }

    [pscustomobject]@{
        status = "success"
        configuration = $Configuration
        assertions = $script:assertions
        usedExternalSamples = -not [string]::IsNullOrWhiteSpace($SampleRoot)
        sampleRoot = $SampleRoot
    } | ConvertTo-Json -Compress
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $leaf = Split-Path -Leaf $resolvedTestRoot
    if ($resolvedTestRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        $leaf.StartsWith("garbro-cli-e2e-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
