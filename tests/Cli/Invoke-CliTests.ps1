[CmdletBinding()]
param(
    [ValidateSet("Debug", "Prerelease", "Release")]
    [string]$Configuration = "Debug",

    [string]$SampleRoot,

    [string]$HxV4UpstreamRoot
)

# GARbro and Formats.dat use the .NET Framework BinaryFormatter wire format.
# PowerShell 7.6 runs on a runtime where BinaryFormatter has been removed, so
# transparently execute this Windows-only legacy solution's E2E suite under the
# in-box Windows PowerShell host when the caller used PowerShell Core.
if ($PSVersionTable.PSEdition -eq "Core") {
    $desktopPowerShell = Join-Path $env:SystemRoot `
        "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $desktopPowerShell -PathType Leaf)) {
        throw "Windows PowerShell 5.1 is required for the GARbro CLI E2E suite."
    }
    $desktopArguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath,
        "-Configuration", $Configuration
    )) {
        $desktopArguments.Add($argument)
    }
    if (-not [string]::IsNullOrWhiteSpace($SampleRoot)) {
        $desktopArguments.Add("-SampleRoot")
        $desktopArguments.Add($SampleRoot)
    }
    if (-not [string]::IsNullOrWhiteSpace($HxV4UpstreamRoot)) {
        $desktopArguments.Add("-HxV4UpstreamRoot")
        $desktopArguments.Add($HxV4UpstreamRoot)
    }
    & $desktopPowerShell $desktopArguments.ToArray()
    exit $LASTEXITCODE
}

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
$xp3BuilderSource = Join-Path $PSScriptRoot "Xp3FixtureBuilder.cs"
$script:xp3BuilderType = @(
    Add-Type -Path $xp3BuilderSource -PassThru |
        Where-Object FullName -eq "GarbroCliTests.Xp3FixtureBuilder"
)[0]

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
    Assert-MachineResultContract -Result $Result -Terminal $value
    return $value
}

function Read-JsonLines {
    param($Result)

    $values = [Collections.Generic.List[object]]::new()
    foreach ($line in @($Result.Lines)) {
        try {
            $value = $line | ConvertFrom-Json
        }
        catch {
            throw "Invalid JSONL stdout line: $line"
        }
        Assert-Equal "garbro.cli/v1" $value.schemaVersion "JSONL schema version"
        $values.Add($value)
    }
    Assert-True ($values.Count -gt 0) "JSONL output must not be empty"
    $operationIds = @($values | Select-Object -ExpandProperty operationId -Unique)
    Assert-Equal 1 $operationIds.Count "JSONL operationId must be stable"
    Assert-True ($values[-1].event -in @("summary", "error", "needs_input")) (
        "JSONL must end with a terminal event")
    Assert-MachineResultContract -Result $Result -Terminal $values[-1]
    return $values.ToArray()
}

function Assert-MachineResultContract {
    param(
        $Result,
        $Terminal
    )

    Assert-True ([string]::IsNullOrWhiteSpace($Result.Stderr)) (
        "machine mode must keep stderr empty: " + ($Result.Arguments -join " "))
    Assert-True ($Result.ExitCode -ne 1) "machine mode must never return exit code 1"
    $allowedStatuses = switch ([int]$Result.ExitCode) {
        0 { @("success") }
        2 { @("usage_error") }
        3 { @("invalid_input", "canceled") }
        4 { @("unrecognized") }
        5 { @("needs_input") }
        6 { @("conflict") }
        7 { @("partial_success") }
        8 { @("io_error") }
        9 { @("internal_error") }
        default { @() }
    }
    Assert-True ($allowedStatuses.Count -gt 0) (
        "machine mode returned an undocumented exit code: $($Result.ExitCode)")
    Assert-True ($allowedStatuses -contains $Terminal.status) (
        "terminal status '$($Terminal.status)' must match exit code " +
        "$($Result.ExitCode): $($Result.Arguments -join ' ')")
}

function New-Xp3Fixture {
    param(
        [string]$Path,
        [object[]]$Records
    )

    $names = [string[]]::new($Records.Count)
    $contents = [byte[][]]::new($Records.Count)
    for ($index = 0; $index -lt $Records.Count; $index++) {
        $names[$index] = [string]$Records[$index].Name
        $value = $Records[$index].Content
        $contents[$index] = if ($value -is [byte[]]) {
            $value
        }
        else {
            [Text.Encoding]::UTF8.GetBytes([string]$value)
        }
    }
    $arguments = [object[]]::new(3)
    $arguments[0] = $Path
    $arguments[1] = $names
    $arguments[2] = $contents
    [void]$script:xp3BuilderType.GetMethod("Create").Invoke($null, $arguments)
}

function New-LargeXp3 {
    param(
        [string]$Path,
        [int]$Count
    )

    $arguments = [object[]]::new(2)
    $arguments[0] = $Path
    $arguments[1] = $Count
    [void]$script:xp3BuilderType.GetMethod("CreateEmpty").Invoke(
        $null, $arguments)
}

function Write-TpmFixture {
    param(
        [string]$Path,
        [byte]$Mutation
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $bytes = [byte[]]::new(4104)
    if ($null -eq $script:tpmControlBlockFixtureBytes -or
        $script:tpmControlBlockFixtureBytes.Length -ne 4096) {
        throw "A valid TPM control-block fixture has not been selected."
    }
    [Array]::Copy(
        $script:tpmControlBlockFixtureBytes, 0, $bytes, 0,
        $script:tpmControlBlockFixtureBytes.Length)
    $bytes[4095] = $bytes[4095] -bxor $Mutation
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Get-XorResolutionFingerprint {
    param([uint32]$Key)

    $arguments = [object[]]::new(3)
    $arguments[0] = [string]$script:cliPath
    $arguments[1] = [string](Join-Path (
        Split-Path -Parent $script:cliPath) "ArcFormats.dll")
    $arguments[2] = [uint32]$Key
    return [string]$script:xp3BuilderType.GetMethod(
        "CreateXorResolutionFingerprint").Invoke($null, $arguments)
}

function Test-HxInlineNamesCopy {
    $arguments = [object[]]::new(1)
    $arguments[0] = [string](Join-Path (
        Split-Path -Parent $script:cliPath) "ArcFormats.dll")
    return [bool]$script:xp3BuilderType.GetMethod(
        "VerifyInlineNamesCopy").Invoke($null, $arguments)
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]'\') {
            ++$backslashes
            continue
        }
        if ($character -eq [char]'"') {
            for ($index = 0; $index -lt ($backslashes * 2 + 1); $index++) {
                [void]$builder.Append([char]'\')
            }
            [void]$builder.Append([char]'"')
            $backslashes = 0
            continue
        }
        for ($index = 0; $index -lt $backslashes; $index++) {
            [void]$builder.Append([char]'\')
        }
        $backslashes = 0
        [void]$builder.Append($character)
    }
    for ($index = 0; $index -lt ($backslashes * 2); $index++) {
        [void]$builder.Append([char]'\')
    }
    [void]$builder.Append([char]'"')
    return $builder.ToString()
}

function Invoke-CliJsonlToFile {
    param(
        [string[]]$Arguments,
        [string]$Stem
    )

    $stdoutPath = Join-Path $script:testRoot ($Stem + ".stdout.jsonl")
    $stderrPath = Join-Path $script:testRoot ($Stem + ".stderr.txt")
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:cliPath
    $startInfo.Arguments = [string]::Join(
        " ", [string[]]@($Arguments | ForEach-Object {
            ConvertTo-NativeArgument ([string]$_)
        }))
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutFile = $null
    $stderrFile = $null
    [long]$peakWorkingSet = 0
    try {
        $stdoutFile = [IO.File]::Create($stdoutPath)
        $stderrFile = [IO.File]::Create($stderrPath)
        if (-not $process.Start()) {
            throw "Failed to start the streamed GARbro CLI process."
        }
        $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
        $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
        while (-not $process.WaitForExit(25)) {
            try {
                $process.Refresh()
                $peakWorkingSet = [Math]::Max(
                    $peakWorkingSet, [long]$process.WorkingSet64)
            }
            catch {
            }
        }
        $process.WaitForExit()
        [Threading.Tasks.Task]::WaitAll(
            [Threading.Tasks.Task[]]@($stdoutCopy, $stderrCopy))
        $stdoutFile.Flush()
        $stderrFile.Flush()
        $process.Refresh()
        [int]$capturedExitCode = $process.ExitCode
        try {
            $peakWorkingSet = [Math]::Max(
                $peakWorkingSet, [long]$process.PeakWorkingSet64)
        }
        catch {
        }
        return [pscustomobject]@{
            Arguments = $Arguments
            ExitCode = $capturedExitCode
            StdoutPath = $stdoutPath
            StderrPath = $stderrPath
            PeakWorkingSetBytes = $peakWorkingSet
        }
    }
    finally {
        if ($null -ne $stdoutFile) {
            $stdoutFile.Dispose()
        }
        if ($null -ne $stderrFile) {
            $stderrFile.Dispose()
        }
        $process.Dispose()
    }
}

function Read-JsonlFileSummary {
    param(
        [string]$Path,
        [string]$ExpectedCommand
    )

    [long]$lineCount = 0
    [long]$entryCount = 0
    $operationId = $null
    $schemaValid = $true
    $operationIdValid = $true
    $commandValid = $true
    $terminal = $null
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $reader = [IO.StreamReader]::new($Path, $encoding, $false)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            ++$lineCount
            try {
                $value = $line | ConvertFrom-Json
            }
            catch {
                throw "Invalid JSONL output at line $lineCount`: $line"
            }
            if ($value.schemaVersion -ne "garbro.cli/v1") {
                $schemaValid = $false
            }
            if ($null -eq $operationId) {
                $operationId = $value.operationId
            }
            elseif ($value.operationId -ne $operationId) {
                $operationIdValid = $false
            }
            if ($value.command -ne $ExpectedCommand) {
                $commandValid = $false
            }
            if ($value.event -eq "entry") {
                ++$entryCount
            }
            $terminal = $value
        }
    }
    finally {
        $reader.Dispose()
    }
    Assert-True ($lineCount -gt 0) "streamed JSONL output is non-empty"
    Assert-True $schemaValid "streamed JSONL schema is stable"
    Assert-True (-not [string]::IsNullOrWhiteSpace($operationId)) (
        "streamed JSONL operation id")
    Assert-True $operationIdValid "streamed JSONL operation id is stable"
    Assert-True $commandValid "streamed JSONL command is stable"
    Assert-True ($terminal.event -in @("summary", "error", "needs_input")) (
        "streamed JSONL has a terminal event")
    return [pscustomobject]@{
        LineCount = $lineCount
        EntryCount = $entryCount
        Terminal = $terminal
    }
}

function Convert-HexToBytes {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or 0 -ne ($Value.Length % 2)) {
        throw "Invalid fixture hexadecimal value."
    }
    $bytes = [byte[]]::new($Value.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($Value.Substring($index * 2, 2), 16)
    }
    return ,$bytes
}

function Get-HxFixtureSchemes {
    if ($null -ne $script:hxFixtureSchemes) {
        return $script:hxFixtureSchemes
    }

    $assemblyDirectory = Split-Path -Parent $script:cliPath
    $gameResAssembly = [Reflection.Assembly]::LoadFrom(
        (Join-Path $assemblyDirectory "GameRes.dll"))
    $arcFormatsAssembly = [Reflection.Assembly]::LoadFrom(
        (Join-Path $assemblyDirectory "ArcFormats.dll"))
    $catalogType = $gameResAssembly.GetType("GameRes.FormatCatalog", $true)
    $catalog = $catalogType.GetProperty(
        "Instance", [Reflection.BindingFlags]"Public,Static").GetValue(
            $null, $null)
    $deserialize = $catalogType.GetMethod(
        "DeserializeScheme", [type[]]@([IO.Stream]))
    $schemePaths = [Collections.Generic.List[string]]::new()
    $schemePaths.Add((Join-Path $catalog.DataDirectory "Formats.dat"))
    $resolverBlock = {
        param($sender, $eventArgs)
        $simpleName = ([Reflection.AssemblyName]::new($eventArgs.Name)).Name
        if ($simpleName -eq "GameRes") {
            return $gameResAssembly
        }
        if ($simpleName -eq "ArcFormats") {
            return $arcFormatsAssembly
        }
        $candidate = Join-Path $assemblyDirectory ($simpleName + ".dll")
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [Reflection.Assembly]::LoadFrom($candidate)
        }
        return $null
    }.GetNewClosure()
    $assemblyResolver = [ResolveEventHandler]$resolverBlock
    [AppDomain]::CurrentDomain.add_AssemblyResolve($assemblyResolver)
    try {
        foreach ($schemePath in $schemePaths) {
            if (-not (Test-Path -LiteralPath $schemePath -PathType Leaf)) {
                continue
            }
            $stream = [IO.File]::OpenRead($schemePath)
            try {
                [void]$deserialize.Invoke($catalog, [object[]]@($stream))
            }
            finally {
                $stream.Dispose()
            }
        }
    }
    finally {
        [AppDomain]::CurrentDomain.remove_AssemblyResolve($assemblyResolver)
    }

    $xp3Type = $arcFormatsAssembly.GetType(
        "GameRes.Formats.KiriKiri.Xp3Opener", $true)
    $hxType = $arcFormatsAssembly.GetType(
        "GameRes.Formats.KiriKiri.HxCrypt", $true)
    $cxType = $arcFormatsAssembly.GetType(
        "GameRes.Formats.KiriKiri.CxEncryption", $true)
    $knownSchemes = $xp3Type.GetProperty(
        "KnownSchemes", [Reflection.BindingFlags]"Public,Static").GetValue(
            $null, $null)
    $flags = [Reflection.BindingFlags]"Instance,Public,NonPublic"
    $controlField = $cxType.GetField("ControlBlock", $flags)
    $tpmField = $cxType.GetField("TpmFileName", $flags)
    $controlSignature = " Encryption control block"
    foreach ($pair in $knownSchemes.GetEnumerator()) {
        $scheme = $pair.Value
        if (-not $cxType.IsInstanceOfType($scheme)) {
            continue
        }
        $control = [uint32[]]$controlField.GetValue($scheme)
        if ($null -eq $control -or $control.Length -ne 0x400) {
            continue
        }
        $rawControl = [byte[]]::new(4096)
        for ($controlIndex = 0; $controlIndex -lt $control.Length;
             $controlIndex++) {
            $rawValue = [uint32]::MaxValue - [uint32]$control[$controlIndex]
            [Array]::Copy(
                [BitConverter]::GetBytes($rawValue), 0,
                $rawControl, $controlIndex * 4, 4)
        }
        if ([Text.Encoding]::ASCII.GetString(
                $rawControl, 0, $controlSignature.Length) -eq
            $controlSignature) {
            $script:tpmControlBlockFixtureBytes = $rawControl
            break
        }
    }
    if ($null -eq $script:tpmControlBlockFixtureBytes) {
        throw "The bundled scheme database has no reusable Cx control block."
    }
    $lazyTpmCandidates = [Collections.Generic.List[object]]::new()
    foreach ($pair in $knownSchemes.GetEnumerator()) {
        $scheme = $pair.Value
        if ($scheme.GetType() -ne $cxType) {
            continue
        }
        $control = $controlField.GetValue($scheme)
        $tpmFileName = [string]$tpmField.GetValue($scheme)
        if ($null -ne $control -or [string]::IsNullOrWhiteSpace($tpmFileName) -or
            [IO.Path]::IsPathRooted($tpmFileName) -or
            $tpmFileName -match '(^|[\\/])\.\.([\\/]|$)') {
            continue
        }
        $mapping = @(
            $catalog.EnumerateGameMap() |
                Where-Object {
                    [string]$_.Value -eq [string]$pair.Key -and
                    [string]$_.Key -eq [IO.Path]::GetFileName([string]$_.Key) -and
                    -not [string]::Equals(
                        [string]$_.Key, $tpmFileName,
                        [StringComparison]::OrdinalIgnoreCase)
                } |
                Sort-Object Key |
                Select-Object -First 1
        )
        if ($mapping.Count -eq 0) {
            continue
        }
        $lazyTpmCandidates.Add([pscustomobject]@{
            SchemeName = [string]$pair.Key
            TpmFileName = $tpmFileName
            ArchiveFileName = [string]$mapping[0].Key
        })
    }
    if ($lazyTpmCandidates.Count -eq 0) {
        throw "The bundled scheme database has no mapped lazy-TPM Cx scheme."
    }
    $script:lazyTpmFixtureScheme = @(
        $lazyTpmCandidates |
            Sort-Object SchemeName, TpmFileName, ArchiveFileName
    )[0]
    $candidates = [Collections.Generic.List[object]]::new()
    foreach ($pair in $knownSchemes.GetEnumerator()) {
        $scheme = $pair.Value
        if (-not $hxType.IsInstanceOfType($scheme)) {
            continue
        }
        $indexKey = [byte[]]$hxType.GetField("IndexKey1", $flags).GetValue($scheme)
        $indexNonce = [byte[]]$hxType.GetField("IndexKey2", $flags).GetValue($scheme)
        $control = [uint32[]]$cxType.GetField("ControlBlock", $flags).GetValue($scheme)
        $even = [byte[]]$cxType.GetField("EvenBranchOrder", $flags).GetValue($scheme)
        $odd = [byte[]]$cxType.GetField("OddBranchOrder", $flags).GetValue($scheme)
        $prolog = [byte[]]$cxType.GetField("PrologOrder", $flags).GetValue($scheme)
        if ($null -eq $indexKey -or $indexKey.Length -ne 32 -or
            $null -eq $indexNonce -or $indexNonce.Length -ne 16 -or
            $null -eq $control -or $control.Length -ne 0x400 -or
            $null -eq $even -or $even.Length -ne 8 -or
            $null -eq $odd -or $odd.Length -ne 6 -or
            $null -eq $prolog -or $prolog.Length -ne 3) {
            continue
        }
        $candidates.Add([pscustomobject]@{
            Name = [string]$pair.Key
            Scheme = $scheme
            ArcFormatsAssembly = $arcFormatsAssembly
            HxType = $hxType
            CxType = $cxType
            IndexKey = $indexKey
            IndexNonce = $indexNonce
            ControlBlock = $control
            EvenOrder = $even
            OddOrder = $odd
            PrologOrder = $prolog
            FilterKey = [uint64]$hxType.GetField("FilterKey", $flags).GetValue($scheme)
            RandomType = [int]$hxType.GetField("RandomType", $flags).GetValue($scheme)
            SplitMask = [uint32]$cxType.GetField("m_mask", $flags).GetValue($scheme)
            SplitPosition = [uint32]$cxType.GetField("m_offset", $flags).GetValue($scheme)
        })
    }
    if ($candidates.Count -lt 2) {
        throw "The bundled scheme database has fewer than two complete Hx v4 schemes."
    }
    $selected = $candidates[0]
    $selectedKey = [Convert]::ToBase64String($selected.IndexKey)
    $wrong = $candidates |
        Where-Object { [Convert]::ToBase64String($_.IndexKey) -ne $selectedKey } |
        Select-Object -First 1
    if ($null -eq $wrong) {
        throw "The bundled scheme database has no distinct Hx v4 index key."
    }
    $script:hxFixtureSchemes = [pscustomobject]@{
        Selected = $selected
        Wrong = $wrong
    }
    return $script:hxFixtureSchemes
}

function Get-LazyTpmFixtureScheme {
    if ($null -eq $script:lazyTpmFixtureScheme) {
        [void](Get-HxFixtureSchemes)
    }
    return $script:lazyTpmFixtureScheme
}

function Protect-HxFixtureContent {
    param(
        $SchemeInfo,
        [uint32]$EntryId,
        [int64]$EntryKey,
        [byte[]]$Content
    )

    $entryType = $SchemeInfo.ArcFormatsAssembly.GetType(
        "GameRes.Formats.KiriKiri.Xp3Entry", $true)
    $extraType = $SchemeInfo.ArcFormatsAssembly.GetType(
        "GameRes.Formats.KiriKiri.HxEntry", $true)
    $entry = [Activator]::CreateInstance($entryType)
    $extra = [Activator]::CreateInstance(
        $extraType, $true)
    $flags = [Reflection.BindingFlags]"Instance,Public,NonPublic"
    $extraType.GetField("Id", $flags).SetValue($extra, [int64]$EntryId)
    $extraType.GetField("Key", $flags).SetValue($extra, $EntryKey)
    $entryType.GetProperty("Extra").SetValue($entry, $extra, $null)
    $createFilter = $SchemeInfo.HxType.GetMethod(
        "CreateFilter", [Reflection.BindingFlags]"Instance,NonPublic")
    [void]$createFilter.Invoke($SchemeInfo.Scheme, [object[]]@($entry))
    $protected = [byte[]]$Content.Clone()
    $decrypt = $SchemeInfo.HxType.GetMethod(
        "Decrypt", [Reflection.BindingFlags]"Instance,Public", $null,
        [type[]]@($entryType, [int64], [byte[]], [int], [int]), $null)
    [void]$decrypt.Invoke($SchemeInfo.Scheme, [object[]]@(
        $entry, [int64]0, $protected, 0, $protected.Length))
    return ,$protected
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

function New-HierarchyZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in @(
            [pscustomobject]@{ Name = "node"; Content = "file-node" },
            [pscustomobject]@{
                Name = "node/child.txt"
                Content = "child-node"
            }
        )) {
            $entry = $zip.CreateEntry($record.Name)
            $stream = $entry.Open()
            try {
                $bytes = [Text.Encoding]::UTF8.GetBytes($record.Content)
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

function New-DuplicateZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $records = @(
        [pscustomobject]@{ Name = "voice/foo.ogg"; Content = "same-content" },
        [pscustomobject]@{ Name = "voice/foo.ogg"; Content = "same-content" },
        [pscustomobject]@{ Name = "voice/foo.ogg"; Content = "different-content" },
        [pscustomobject]@{ Name = "voice/FOO.ogg"; Content = "case-content" },
        [pscustomobject]@{
            Name = "voice/foo.__entry-000001.ogg"
            Content = "natural-suffix"
        },
        [pscustomobject]@{
            Name = [IO.Path]::GetFileName($Path)
            Content = "source-name-collision"
        }
    )
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in $records) {
            $entry = $zip.CreateEntry($record.Name)
            $stream = $entry.Open()
            try {
                $bytes = [Text.Encoding]::UTF8.GetBytes($record.Content)
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

function New-PngFixture {
    param([string]$Path)

    $png = [Convert]::FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    [IO.File]::WriteAllBytes($Path, $png)
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

function New-PartiallyUnderdeclaredZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in @(
            [pscustomobject]@{ Name = "first.txt"; Content = "first" },
            [pscustomobject]@{ Name = "large.txt"; Content = ("A" * 10000) },
            [pscustomobject]@{ Name = "after.txt"; Content = "after" }
        )) {
            $entry = $zip.CreateEntry(
                $record.Name, [IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try {
                $content = [Text.Encoding]::ASCII.GetBytes($record.Content)
                $stream.Write($content, 0, $content.Length)
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

    $bytes = [IO.File]::ReadAllBytes($Path)
    $patchedLocal = $false
    $patchedCentral = $false
    for ($offset = 0; $offset -le $bytes.Length - 46; $offset++) {
        $signature = [BitConverter]::ToUInt32($bytes, $offset)
        if ($signature -eq [uint32]0x04034b50) {
            $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 26)
            $name = [Text.Encoding]::UTF8.GetString(
                $bytes, $offset + 30, $nameLength)
            if ($name -ceq "large.txt") {
                [BitConverter]::GetBytes([uint32]1).CopyTo(
                    $bytes, $offset + 22)
                $patchedLocal = $true
            }
        }
        elseif ($signature -eq [uint32]0x02014b50) {
            $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
            $name = [Text.Encoding]::UTF8.GetString(
                $bytes, $offset + 46, $nameLength)
            if ($name -ceq "large.txt") {
                [BitConverter]::GetBytes([uint32]1).CopyTo(
                    $bytes, $offset + 24)
                $patchedCentral = $true
            }
        }
    }
    if (-not $patchedLocal -or -not $patchedCentral) {
        throw "Could not patch the partially underdeclared ZIP fixture."
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function New-CumulativeBudgetZip {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Create($Path)
    $zip = [IO.Compression.ZipArchive]::new(
        $file, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in @(
            [pscustomobject]@{ Name = "first.txt"; Content = "first" },
            [pscustomobject]@{
                Name = "failing.txt"
                Content = ("A" * 66000)
            },
            [pscustomobject]@{
                Name = "later.txt"
                Content = ("B" * 10000)
            },
            [pscustomobject]@{
                Name = "untouched.txt"
                Content = "not attempted"
            }
        )) {
            $entry = $zip.CreateEntry(
                $record.Name, [IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try {
                $content = [Text.Encoding]::ASCII.GetBytes($record.Content)
                $stream.Write($content, 0, $content.Length)
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

    $bytes = [IO.File]::ReadAllBytes($Path)
    $patchedLocal = $false
    $patchedCentral = $false
    for ($offset = 0; $offset -le $bytes.Length - 46; $offset++) {
        $signature = [BitConverter]::ToUInt32($bytes, $offset)
        if ($signature -eq [uint32]0x04034b50) {
            $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 26)
            $extraLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
            if ($offset + 30 + $nameLength + $extraLength -gt $bytes.Length) {
                continue
            }
            $name = [Text.Encoding]::UTF8.GetString(
                $bytes, $offset + 30, $nameLength)
            if ($name -ceq "failing.txt") {
                $compressedSize = [BitConverter]::ToUInt32(
                    $bytes, $offset + 18)
                $dataOffset = $offset + 30 + $nameLength + $extraLength
                if ($compressedSize -lt 2 -or
                    $dataOffset + $compressedSize -gt $bytes.Length) {
                    throw "Invalid cumulative-budget ZIP fixture data range."
                }
                [BitConverter]::GetBytes([uint32]1).CopyTo(
                    $bytes, $offset + 22)
                $corruptOffset = $dataOffset + $compressedSize - 1
                $bytes[$corruptOffset] = $bytes[$corruptOffset] -bxor 0xff
                $patchedLocal = $true
            }
        }
        elseif ($signature -eq [uint32]0x02014b50) {
            $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
            if ($offset + 46 + $nameLength -gt $bytes.Length) {
                continue
            }
            $name = [Text.Encoding]::UTF8.GetString(
                $bytes, $offset + 46, $nameLength)
            if ($name -ceq "failing.txt") {
                [BitConverter]::GetBytes([uint32]1).CopyTo(
                    $bytes, $offset + 24)
                $patchedCentral = $true
            }
        }
    }
    if (-not $patchedLocal -or -not $patchedCentral) {
        throw "Could not patch the cumulative-budget ZIP fixture."
    }
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

function Add-TjsBytes {
    param(
        [Collections.Generic.List[byte]]$Buffer,
        [byte[]]$Bytes
    )

    foreach ($value in $Bytes) {
        $Buffer.Add($value)
    }
}

function Get-TjsTypeCheck {
    param(
        [ref]$Seed,
        [byte]$TypeCode
    )

    $bytes = [BitConverter]::GetBytes([uint32]$Seed.Value)
    if ($TypeCode -ne 0) {
        $a = ($bytes[0] -bxor (($bytes[0] * 2) -band 0xff)) -band 0xff
        $b = $a
        $b = (($b -shr 2) -bxor $bytes[2]) -band 0xff
        $b = (($b -shr 3) -bxor $bytes[2]) -band 0xff
        $b = ($b -bxor $a) -band 0xff
        $bytes[0] = $bytes[1]
        $bytes[1] = $bytes[2]
        $bytes[2] = [byte]$b
        $Seed.Value = [BitConverter]::ToUInt32($bytes, 0)
    }
    return $bytes[2]
}

function Add-TjsType {
    param(
        [Collections.Generic.List[byte]]$Buffer,
        [ref]$Seed,
        [byte]$TypeCode
    )

    $check = Get-TjsTypeCheck -Seed $Seed -TypeCode $TypeCode
    Add-TjsBytes -Buffer $Buffer -Bytes ([byte[]]($TypeCode, $check))
}

function Add-TjsUInt32 {
    param(
        [Collections.Generic.List[byte]]$Buffer,
        [uint32]$Value
    )

    Add-TjsBytes -Buffer $Buffer -Bytes ([BitConverter]::GetBytes($Value))
}

function Add-TjsString {
    param(
        [Collections.Generic.List[byte]]$Buffer,
        [string]$Value
    )

    $bytes = [Text.Encoding]::Unicode.GetBytes($Value)
    Add-TjsUInt32 -Buffer $Buffer -Value ([uint32]($bytes.Length / 2))
    Add-TjsBytes -Buffer $Buffer -Bytes $bytes
}

function Get-TjsFinalChecksum {
    param([uint32]$Seed)

    $bytes = [BitConverter]::GetBytes($Seed)
    for ($round = 0; $round -lt 3; $round++) {
        $a = ($bytes[0] -bxor (($bytes[0] * 2) -band 0xff)) -band 0xff
        $b = $a
        $b = (($b -shr 2) -bxor $bytes[2]) -band 0xff
        $b = (($b -shr 3) -bxor $bytes[2]) -band 0xff
        $b = ($b -bxor $a) -band 0xff
        $bytes[0] = $bytes[1]
        $bytes[1] = $bytes[2]
        $bytes[2] = [byte]$b
    }
    $swap = $bytes[0]
    $bytes[0] = $bytes[2]
    $bytes[2] = $swap
    return [BitConverter]::ToUInt32($bytes, 0)
}

function ConvertTo-Lz4LiteralBlock {
    param([byte[]]$Data)

    $result = [Collections.Generic.List[byte]]::new()
    $literalLength = $Data.Length
    $result.Add([byte](([Math]::Min(15, $literalLength)) -shl 4))
    if ($literalLength -ge 15) {
        $remaining = $literalLength - 15
        while ($remaining -ge 255) {
            $result.Add([byte]255)
            $remaining -= 255
        }
        $result.Add([byte]$remaining)
    }
    Add-TjsBytes -Buffer $result -Bytes $Data
    return $result.ToArray()
}

function New-TjsPbdFixture {
    param(
        [string]$Path,
        [ValidateSet("4s0-layer", "ns0-thumbnail")]
        [string]$Kind
    )

    [uint32]$seed = 0x00534a54
    $payload = [Collections.Generic.List[byte]]::new()
    if ($Kind -eq "4s0-layer") {
        Add-TjsType -Buffer $payload -Seed ([ref]$seed) -TypeCode 0x81
        Add-TjsUInt32 -Buffer $payload -Value 1
        Add-TjsType -Buffer $payload -Seed ([ref]$seed) -TypeCode 0xc1
        Add-TjsUInt32 -Buffer $payload -Value 1
        Add-TjsString -Buffer $payload -Value "layer_id"
        Add-TjsType -Buffer $payload -Seed ([ref]$seed) -TypeCode 4
        Add-TjsBytes -Buffer $payload -Bytes ([BitConverter]::GetBytes([int64]7))
    }
    else {
        Add-TjsType -Buffer $payload -Seed ([ref]$seed) -TypeCode 0xc1
        Add-TjsUInt32 -Buffer $payload -Value 1
        Add-TjsString -Buffer $payload -Value "hero"
        Add-TjsType -Buffer $payload -Seed ([ref]$seed) -TypeCode 2
        Add-TjsString -Buffer $payload -Value "thumb_chara"
    }
    Add-TjsUInt32 -Buffer $payload -Value (Get-TjsFinalChecksum -Seed $seed)

    $file = [Collections.Generic.List[byte]]::new()
    $magic = if ($Kind -eq "4s0-layer") { "TJS/4s0`0" } else { "TJS/ns0`0" }
    Add-TjsBytes -Buffer $file -Bytes ([Text.Encoding]::ASCII.GetBytes($magic))
    Add-TjsUInt32 -Buffer $file -Value 0x00534a54
    Add-TjsBytes -Buffer $file -Bytes ([byte[]](0, 0, 0, 0))
    if ($Kind -eq "4s0-layer") {
        $compressed = ConvertTo-Lz4LiteralBlock -Data $payload.ToArray()
        Add-TjsBytes -Buffer $file -Bytes (
            [BitConverter]::GetBytes([uint16]$compressed.Length))
        Add-TjsBytes -Buffer $file -Bytes $compressed
    }
    else {
        Add-TjsBytes -Buffer $file -Bytes $payload.ToArray()
    }
    [IO.File]::WriteAllBytes($Path, $file.ToArray())
}

function Invoke-UpstreamPbd2Json {
    param(
        [string]$Executable,
        [string]$InputPath
    )

    $stdoutPath = Join-Path $script:testRoot (
        "pbd2json-stdout-" + [guid]::NewGuid().ToString("N") + ".txt")
    $stderrPath = Join-Path $script:testRoot (
        "pbd2json-stderr-" + [guid]::NewGuid().ToString("N") + ".txt")
    $process = Start-Process -FilePath $Executable `
        -ArgumentList ('"' + $InputPath + '"') `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru
    try {
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            throw "Upstream pbd2json timed out for $InputPath"
        }
        if ($process.ExitCode -ne 0) {
            throw "Upstream pbd2json failed for $InputPath`: " +
                [IO.File]::ReadAllText($stderrPath)
        }
        $stdout = [IO.File]::ReadAllText($stdoutPath)
        if ([string]::IsNullOrWhiteSpace($stdout)) {
            throw "Upstream pbd2json returned no JSON for $InputPath`: " +
                [IO.File]::ReadAllText($stderrPath)
        }
        return $stdout | ConvertFrom-Json
    }
    finally {
        $process.Dispose()
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
    Assert-True (
        $capabilities.data.protocolVersions -contains "garbro.cli/v1") (
        "capabilities machine protocol version")
    Assert-True ($capabilities.data.outputFormats -contains "jsonl") (
        "capabilities JSONL output format")
    Assert-True ($capabilities.data.commands -contains "archive.extract") (
        "capabilities command list")
    foreach ($workflowCommand in @(
        "archive.plan",
        "archive.schemes",
        "archive.scheme-info",
        "archive.scheme-check",
        "image.convert-batch"
    )) {
        Assert-True ($capabilities.data.commands -contains $workflowCommand) (
            "large-workflow capability: $workflowCommand")
    }
    foreach ($hxCommand in @(
        "hxv4.schemes",
        "hxv4.hash",
        "hxv4.generate",
        "hxv4.generate-archive",
        "hxv4.clean",
        "hxv4.find-missing-voices",
        "hxv4.restore-structure",
        "hxv4.rename",
        "hxv4.krkrdump",
        "hxv4.krkrdump-import"
    )) {
        Assert-True ($capabilities.data.commands -contains $hxCommand) (
            "Hx v4 capability: $hxCommand")
    }
    Assert-True $capabilities.data.safety.pathContainment "path containment capability"
    Assert-True $capabilities.data.safety.actualByteCounting "actual byte counting capability"
    Assert-True $capabilities.data.safety.atomicWrites "atomic write capability"
    Assert-True ($capabilities.data.safety.duplicatePolicies -contains "suffix-index") (
        "duplicate suffix capability")
    Assert-True ($capabilities.data.safety.resumeModes -contains "verify-hash") (
        "archive hash resume capability")
    Assert-True (
        $capabilities.data.safety.imageBatchResumeModes -contains "verify-decode") (
        "image batch decode resume capability")
    Assert-Equal "garbro.extraction-manifest/v1" (
        $capabilities.data.safety.extractionManifestSchema) (
        "extraction manifest capability schema")
    Assert-True $capabilities.data.safety.automaticFiniteBudget (
        "automatic finite budget capability")
    Assert-True $capabilities.data.safety.summaryOnly (
        "summary-only capability")
    Assert-True $capabilities.data.safety.explicitXp3SchemeOptions (
        "explicit XP3 scheme option capability")
    Assert-Equal 10000 $capabilities.data.safety.defaultMaxFiles (
        "default file-count safety capability")

    $xorFingerprintA1 = Get-XorResolutionFingerprint -Key 0xAA
    $xorFingerprintA2 = Get-XorResolutionFingerprint -Key 0xAA
    $xorFingerprintB = Get-XorResolutionFingerprint -Key 0xAB
    Assert-Equal $xorFingerprintA1 $xorFingerprintA2 (
        "scheme material fingerprint is deterministic")
    Assert-True ($xorFingerprintA1 -ne $xorFingerprintB) (
        "same-name same-type schemes with different keys have different fingerprints")
    Assert-True $xorFingerprintA1.StartsWith("sha256:") (
        "scheme resolution fingerprint is a SHA-256 digest")
    Assert-True (Test-HxInlineNamesCopy) (
        "inline Hx names remain available to GUI seed generation")

    foreach ($component in @("KrkrDump-x86", "KrkrDump-x64")) {
        $reportedComponent = @(
            $capabilities.data.optionalComponents |
                Where-Object name -eq $component
        )
        Assert-Equal 1 $reportedComponent.Count (
            "KrkrDump optional component discovery: $component")
        Assert-True (-not [string]::IsNullOrWhiteSpace(
            $reportedComponent[0].path)) (
            "KrkrDump optional component path: $component")
    }

    $helpResult = Invoke-Cli -Arguments @("help", "--output", "json")
    Assert-Equal 0 $helpResult.ExitCode "help exit code"
    $help = Read-JsonEnvelope $helpResult

    foreach ($commandName in $capabilities.data.commands) {
        $commandHelpArguments = @($commandName -split '\.') + @(
            "--help", "--output", "json")
        $commandHelpResult = Invoke-Cli -Arguments $commandHelpArguments
        Assert-Equal 0 $commandHelpResult.ExitCode (
            "structured help exit code: $commandName")
        $commandHelp = Read-JsonEnvelope $commandHelpResult
        Assert-Equal $commandName $commandHelp.data.topic (
            "structured help topic: $commandName")
        Assert-Equal "command" $commandHelp.data.kind (
            "structured help kind: $commandName")
    }

    $missingOptionValueResult = Invoke-Cli -Arguments @(
        "archive", "schemes", "--filter", "--output", "json")
    Assert-Equal 2 $missingOptionValueResult.ExitCode (
        "missing option value exit code")
    $missingOptionValue = Read-JsonEnvelope $missingOptionValueResult
    Assert-Equal "missing_option_value" $missingOptionValue.error.code (
        "missing option value error code")

    $blankSchemeValueResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", "unused.xp3",
        "--scheme", "   ", "--output", "json")
    Assert-Equal 2 $blankSchemeValueResult.ExitCode (
        "blank scheme value exit code")
    $blankSchemeValue = Read-JsonEnvelope $blankSchemeValueResult
    Assert-Equal "missing_option_value" $blankSchemeValue.error.code (
        "blank scheme value error code")
    foreach ($hxAction in @(
        "schemes", "hash", "generate", "generate-archive", "clean",
        "find-missing-voices",
        "restore-structure", "rename", "krkrdump", "krkrdump-import"
    )) {
        Assert-True ($help.data.usage -like "*hxv4 $hxAction*") (
            "help discovers Hx v4 action: $hxAction")
    }

    $archiveExtractHelpResult = Invoke-Cli -Arguments @(
        "archive", "extract", "--help", "--output", "json")
    Assert-Equal 0 $archiveExtractHelpResult.ExitCode (
        "archive extract subcommand help exit code")
    $archiveExtractHelp = Read-JsonEnvelope $archiveExtractHelpResult
    Assert-Equal "archive.extract" $archiveExtractHelp.data.topic (
        "archive extract help topic")
    Assert-Equal "command" $archiveExtractHelp.data.kind (
        "archive extract help kind")
    foreach ($requiredOption in @(
        "destination", "entry-index", "duplicate-policy", "budget",
        "manifest", "checksum", "resume", "resume-manifest",
        "summary-only", "scheme", "hx-names", "cx-dump-dir"
    )) {
        Assert-Equal 1 @(
            $archiveExtractHelp.data.options |
                Where-Object name -eq $requiredOption
        ).Count "archive extract help option: $requiredOption"
    }
    $archiveEntryOption = @(
        $archiveExtractHelp.data.options | Where-Object name -eq "entry"
    )[0]
    Assert-True $archiveEntryOption.repeatable (
        "archive extract --entry is documented as repeatable")
    $archiveDuplicateOption = @(
        $archiveExtractHelp.data.options |
            Where-Object name -eq "duplicate-policy"
    )[0]
    Assert-True ($archiveDuplicateOption.choices -contains "suffix-index") (
        "archive extract help documents suffix-index")

    $archivePlanHelpResult = Invoke-Cli -Arguments @(
        "help", "archive", "plan", "--output", "json")
    Assert-Equal 0 $archivePlanHelpResult.ExitCode (
        "explicit archive plan help exit code")
    $archivePlanHelp = Read-JsonEnvelope $archivePlanHelpResult
    Assert-Equal "archive.plan" $archivePlanHelp.data.topic (
        "explicit archive plan help topic")
    Assert-Equal 1 @(
        $archivePlanHelp.data.options | Where-Object name -eq "entry-index"
    ).Count "archive plan help entry-index option"

    $archiveSchemeCheckHelpResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", "--help", "--output", "json")
    Assert-Equal 0 $archiveSchemeCheckHelpResult.ExitCode (
        "archive scheme-check help exit code")
    $archiveSchemeCheckHelp = Read-JsonEnvelope $archiveSchemeCheckHelpResult
    Assert-Equal "archive.scheme-check" $archiveSchemeCheckHelp.data.topic (
        "archive scheme-check help topic")
    Assert-True ($archiveSchemeCheckHelp.data.usage.EndsWith(
        "archive scheme-check ARCHIVE " +
        "<--scheme NAME | --cx-dump-dir DIR | both> [--hx-names FILE]",
        [StringComparison]::Ordinal)) (
        "archive scheme-check help documents its scheme requirement")
    foreach ($requiredOption in @("scheme", "cx-dump-dir", "hx-names")) {
        Assert-Equal 1 @(
            $archiveSchemeCheckHelp.data.options |
                Where-Object name -eq $requiredOption
        ).Count "archive scheme-check help option: $requiredOption"
    }

    $hxNamesAloneResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $cliPath,
        "--hx-names", (Join-Path $testRoot "unused-HxNames.lst"),
        "--output", "json")
    Assert-Equal 3 $hxNamesAloneResult.ExitCode (
        "archive scheme-check rejects hx-names without a base scheme")
    $hxNamesAlone = Read-JsonEnvelope $hxNamesAloneResult
    Assert-Equal "xp3_scheme_required" $hxNamesAlone.error.code (
        "archive scheme-check hx-names-only error")

    $imageBatchHelpResult = Invoke-Cli -Arguments @(
        "image", "convert-batch", "--help", "--output", "json")
    Assert-Equal 0 $imageBatchHelpResult.ExitCode (
        "image batch subcommand help exit code")
    $imageBatchHelp = Read-JsonEnvelope $imageBatchHelpResult
    Assert-Equal "image.convert-batch" $imageBatchHelp.data.topic (
        "image batch help topic")
    foreach ($requiredOption in @(
        "source-root", "destination", "format", "manifest", "recursive",
        "detect-by-signature", "resume", "budget", "summary-only"
    )) {
        Assert-Equal 1 @(
            $imageBatchHelp.data.options |
                Where-Object name -eq $requiredOption
        ).Count "image batch help option: $requiredOption"
    }

    $imageConvertHelpResult = Invoke-Cli -Arguments @(
        "image", "convert", "--help", "--output", "json")
    Assert-Equal 0 $imageConvertHelpResult.ExitCode (
        "image convert subcommand help exit code")
    $imageConvertHelp = Read-JsonEnvelope $imageConvertHelpResult
    Assert-Equal "image.convert" $imageConvertHelp.data.topic (
        "image convert help topic")
    foreach ($requiredOption in @(
        "format", "destination", "overwrite", "max-total-bytes",
        "max-entry-bytes", "dry-run"
    )) {
        Assert-Equal 1 @(
            $imageConvertHelp.data.options |
                Where-Object name -eq $requiredOption
        ).Count "image convert help option: $requiredOption"
    }

    $hxGenerateArchiveHelpResult = Invoke-Cli -Arguments @(
        "hxv4", "generate-archive", "--help", "--output", "json")
    Assert-Equal 0 $hxGenerateArchiveHelpResult.ExitCode (
        "Hx generate-archive subcommand help exit code")
    $hxGenerateArchiveHelp = Read-JsonEnvelope $hxGenerateArchiveHelpResult
    Assert-Equal "hxv4.generate-archive" $hxGenerateArchiveHelp.data.topic (
        "Hx generate-archive help topic")
    foreach ($requiredOption in @("scheme", "destination", "seed")) {
        Assert-Equal 1 @(
            $hxGenerateArchiveHelp.data.options |
                Where-Object name -eq $requiredOption
        ).Count "Hx generate-archive help option: $requiredOption"
    }

    $archiveSchemesResult = Invoke-Cli -Arguments @(
        "archive", "schemes", "--filter", "__NOCRYPT__",
        "--output", "json")
    Assert-Equal 0 $archiveSchemesResult.ExitCode "archive schemes exit code"
    $archiveSchemes = Read-JsonEnvelope $archiveSchemesResult
    Assert-Equal 1 $archiveSchemes.data.schemeCount (
        "archive scheme filter count")
    Assert-Equal "__NOCRYPT__" $archiveSchemes.data.schemes[0].name (
        "archive scheme builtin alias")
    Assert-Equal "none" $archiveSchemes.data.schemes[0].family (
        "archive scheme family")

    $archiveSchemeInfoResult = Invoke-Cli -Arguments @(
        "archive", "scheme-info", "__nocrypt__", "--output", "json")
    Assert-Equal 0 $archiveSchemeInfoResult.ExitCode (
        "archive scheme-info exit code")
    $archiveSchemeInfo = Read-JsonEnvelope $archiveSchemeInfoResult
    Assert-Equal "__NOCRYPT__" $archiveSchemeInfo.data.name (
        "archive scheme-info exact case-insensitive resolution")
    Assert-Equal "__nocrypt__" $archiveSchemeInfo.data.requestedName (
        "archive scheme-info requested name")
    Assert-True ($null -eq $archiveSchemeInfo.data.key) (
        "archive scheme-info does not serialize key material")

    $archiveUnknownSchemeResult = Invoke-Cli -Arguments @(
        "archive", "scheme-info", "__definitely_missing_scheme__",
        "--output", "json")
    Assert-Equal 3 $archiveUnknownSchemeResult.ExitCode (
        "archive unknown scheme exit code")
    $archiveUnknownScheme = Read-JsonEnvelope $archiveUnknownSchemeResult
    Assert-Equal "xp3_scheme_not_found" $archiveUnknownScheme.error.code (
        "archive unknown scheme error")
    Assert-Equal "__definitely_missing_scheme__" (
        $archiveUnknownScheme.error.details.requestedScheme) (
        "archive unknown scheme requested value")

    $hxSchemesResult = Invoke-Cli -Arguments @(
        "hxv4", "schemes", "--output", "json")
    Assert-Equal 0 $hxSchemesResult.ExitCode "Hx v4 schemes exit code"
    $hxSchemes = Read-JsonEnvelope $hxSchemesResult
    Assert-True ($hxSchemes.data.count -gt 0) "Hx v4 schemes are discoverable"

    $formatsResult = Invoke-Cli -Arguments @(
        "formats", "list", "--kind", "script", "--output", "jsonl")
    Assert-Equal 0 $formatsResult.ExitCode "formats list exit code"
    $formats = Read-JsonLines $formatsResult
    Assert-Equal "summary" $formats[-1].event "formats terminal event"
    Assert-True ($formats[-1].data.count -gt 0) "script format count"

    $hxFileHashResult = Invoke-Cli -Arguments @(
        "hxv4", "hash", "startup.tjs", "--kind", "file", "--output", "json")
    Assert-Equal 0 $hxFileHashResult.ExitCode "Hx v4 file hash exit code"
    $hxFileHash = Read-JsonEnvelope $hxFileHashResult
    Assert-Equal (
        "D9FB4859A254D7B9EDA6621CFBE7DFD9D428082090CA08E32A9314E7116548E9"
    ) $hxFileHash.data.hash "Hx v4 file hash vector"

    $hxPathHashResult = Invoke-Cli -Arguments @(
        "hxv4", "hash", "locale/jp/", "--kind", "path", "--output", "json")
    Assert-Equal 0 $hxPathHashResult.ExitCode "Hx v4 path hash exit code"
    $hxPathHash = Read-JsonEnvelope $hxPathHashResult
    Assert-Equal "DE097C7B9EA97EB7" $hxPathHash.data.hash (
        "Hx v4 path hash vector")

    $hxSource = Join-Path $testRoot "hxv4-source"
    $hxDataMain = Join-Path $hxSource "data\main"
    $hxVoice = Join-Path $hxSource "voice"
    New-Item -ItemType Directory -Path $hxDataMain -Force | Out-Null
    New-Item -ItemType Directory -Path $hxVoice -Force | Out-Null
    $baseStageText = @'
%[
  times => %[ day => %[ prefix => "d_" ], none => %[ prefix => void ] ],
  seasons => %[ summer => %[ prefix => "s_" ] ],
  stages => %[ school => %[ image => "bg_TIMESEASONschool" ] ]
]
'@
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "base.stage"), $baseStageText, [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "soundlist.csv"),
        "customtrack,ignored`r`n", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "cglist.csv"),
        "thum_ev001,ev001a|ev001ab`r`nthum_sd001,sd001a01`r`n",
        [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "charvoice.csv"),
        "CHAR,aya_001`r`n", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "imagediffmap.csv"),
        "row,evdiff`r`nrow,img1|img2.png`r`nrow,comboa|combob`r`n",
        [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "savelist.csv"),
        "savethum_ev001`r`n", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "scenelist.csv"),
        "movthum_op|thum_scene`r`n", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllText(
        (Join-Path $hxDataMain "replay.ks"),
        "[edmovie file=ending]`r`n", [Text.Encoding]::Unicode)
    [IO.File]::WriteAllBytes(
        (Join-Path $hxDataMain "MixedCase.bin"), [byte[]](5, 6))
    [IO.File]::WriteAllBytes(
        (Join-Path $hxVoice "anj_loop_64.ogg"), [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllText(
        (Join-Path $hxVoice "bgv001.csv"),
        "row,ignored,bgv_voice`r`n", [Text.Encoding]::Unicode)
    $hxForeground = Join-Path $hxSource "fgimage"
    New-Item -ItemType Directory -Path $hxForeground | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $hxForeground "sample.stand"),
        "filename:'hero'", [Text.Encoding]::Unicode)
    $hxLayerPbd = Join-Path $hxForeground "hero.pbd"
    $hxThumbnailPbd = Join-Path $hxForeground "_chthum_index.pbd"
    $hxEncryptedPbd = Join-Path $hxForeground "encrypted.pbd"
    $hxEncryptedIvPbd = Join-Path $hxForeground "encrypted_iv.pbd"
    New-TjsPbdFixture -Path $hxLayerPbd -Kind "4s0-layer"
    New-TjsPbdFixture -Path $hxThumbnailPbd -Kind "ns0-thumbnail"
    $hxEncryptedPbdBase64 = Join-Path (
        Join-Path $PSScriptRoot "Fixtures") "hxv4-pbd-crypt1.base64"
    [IO.File]::WriteAllBytes(
        $hxEncryptedPbd,
        [Convert]::FromBase64String(
            [IO.File]::ReadAllText($hxEncryptedPbdBase64).Trim()))
    $hxEncryptedIvPbdBase64 = Join-Path (
        Join-Path $PSScriptRoot "Fixtures") "hxv4-pbd-crypt6-iv.base64"
    [IO.File]::WriteAllBytes(
        $hxEncryptedIvPbd,
        [Convert]::FromBase64String(
            [IO.File]::ReadAllText($hxEncryptedIvPbdBase64).Trim()))
    $hxScenario = Join-Path $hxSource "scn"
    New-Item -ItemType Directory -Path $hxScenario | Out-Null
    $hxPsbFixtureBase64 = Join-Path (
        Join-Path $PSScriptRoot "Fixtures") "hxv4-scenario.psb.base64"
    $hxPsbFixture = Join-Path $hxScenario "fixture.psb"
    [IO.File]::WriteAllBytes(
        $hxPsbFixture,
        [Convert]::FromBase64String(
            [IO.File]::ReadAllText($hxPsbFixtureBase64).Trim()))

    if (-not [string]::IsNullOrWhiteSpace($HxV4UpstreamRoot)) {
        $pbd2json = Join-Path $HxV4UpstreamRoot "binaries\pbd2json.exe"
        Assert-True (Test-Path -LiteralPath $pbd2json -PathType Leaf) (
            "upstream pbd2json exists")
        $upstreamLayer = Invoke-UpstreamPbd2Json `
            -Executable $pbd2json -InputPath $hxLayerPbd
        Assert-Equal 7 $upstreamLayer[0].layer_id (
            "upstream pbd2json reads generated TJS/4s0 fixture")
        $upstreamThumbnail = Invoke-UpstreamPbd2Json `
            -Executable $pbd2json -InputPath $hxThumbnailPbd
        Assert-Equal "thumb_chara" $upstreamThumbnail.hero (
            "upstream pbd2json reads generated TJS/ns0 fixture")
        $upstreamEncrypted = Invoke-UpstreamPbd2Json `
            -Executable $pbd2json -InputPath $hxEncryptedPbd
        Assert-Equal 7 $upstreamEncrypted[0].layer_id (
            "upstream pbd2json reads encrypted TJS/4s0 fixture")
        $upstreamEncryptedIv = Invoke-UpstreamPbd2Json `
            -Executable $pbd2json -InputPath $hxEncryptedIvPbd
        Assert-Equal 7 $upstreamEncryptedIv[0].layer_id (
            "upstream pbd2json reads encrypted TJS/4s0 IV fixture")
    }

    $hxKrkrDump = Join-Path $testRoot "hxv4-krkrdump-source"
    New-Item -ItemType Directory -Path $hxKrkrDump | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $hxKrkrDump "KrkrDump-fixture.log"),
        "NameHash: `"oracle_log.bin`" `"xp3hnp`" `"$('A' * 64)`"`r`n" +
        "PathHash: `"oracle/path/`" `"xp3hnp`" `"$('B' * 16)`"`r`n",
        [Text.Encoding]::UTF8)
    $hxExplicitSource = Join-Path $testRoot "explicit-source.stand"
    [IO.File]::WriteAllText(
        $hxExplicitSource, "filename:'explicit_hero'", [Text.Encoding]::Unicode)
    $hxExplicitVoiceSource = Join-Path $testRoot "explicit-voice.tjs"
    [IO.File]::WriteAllText(
        $hxExplicitVoiceSource, 'voice => "explicit_voice.wav"',
        [Text.Encoding]::Unicode)
    $hxSeedFile = Join-Path $testRoot "HxNames-seed.lst"
    [IO.File]::WriteAllText(
        $hxSeedFile,
        "94D4A97C61498621:`r`n$('C' * 64):seed_only.bin`r`n",
        [Text.UTF8Encoding]::new($false))

    $hxGeneratedFile = Join-Path $testRoot "HxNames-generated.lst"
    $hxGenerateResult = Invoke-Cli -Arguments @(
        "hxv4", "generate",
        "--source-dir", $hxSource,
        "--source-file", $hxExplicitSource,
        "--source-file", $hxExplicitVoiceSource,
        "--krkrdump-dir", $hxKrkrDump,
        "--seed", $hxSeedFile,
        "--include-garbro-common",
        "--destination", $hxGeneratedFile,
        "--output", "json")
    Assert-Equal 0 $hxGenerateResult.ExitCode "Hx v4 source generation exit code"
    $hxGenerate = Read-JsonEnvelope $hxGenerateResult
    Assert-Equal "success" $hxGenerate.status "Hx v4 source generation status"
    Assert-True (Test-Path -LiteralPath $hxGeneratedFile -PathType Leaf) (
        "Hx v4 generated names file exists")
    $hxGeneratedValues = @(
        Get-Content -LiteralPath $hxGeneratedFile |
            ForEach-Object { ($_ -split ":", 2)[1] }
    )
    foreach ($expectedName in @(
        "bg_d_s_school.png",
        "bgthum_bg_d_s_school.jpg",
        "bg_nulls_school.png",
        "customtrack.ogg",
        "customtrack.mchx.sli",
        "aya_title.ogg",
        "aya_after.ogg",
        "aya_titleback.ogg",
        "ending.mp4",
        "op.mp4",
        "en_op1080.wmv",
        "cn_ending_1080.mp4",
        "tw_ending720p.wmv",
        "hero.pbd",
        "hero.sinfo",
        "hero_0.pbd",
        "hero_7.tlg",
        "encrypted_7.tlg",
        "encrypted_iv_7.tlg",
        "explicit_hero.pbd",
        "explicit_hero.sinfo",
        "explicit_voice.wav",
        "seed_only.bin",
        "thumb_chara.png",
        "thum_ev001_censored.psb",
        "ev001a.pimg",
        "ev001ab.pimg",
        "savethum_ev001ab.psb",
        "sd001.mtn",
        "sd001a01.asd",
        "evdiff_censored.psb",
        "img1.png",
        "img2.png",
        "comboa|combob.pimg",
        "bgv_voice.opus.sli",
        "bgv_voice.ini",
        "savethum_ev001.psb",
        "thum_ev001.psb",
        "movthum_op_censored.png",
        "thum_scene.psb",
        "fixture_scenario.scn",
        "hero_001_0001.ogg",
        "hero_001_0002.ogg",
        "hero_loop_03.ogg",
        "chaticon_smile.png",
        "stamp_happy.png",
        "theme_main.mchx",
        "bg_school.png",
        "bgthum_bg_school.jpg",
        "ev001.png",
        "se_click.ogg",
        "se_hover.ini",
        "hero.stand",
        "hero_face.png",
        "hero_alt.stand",
        "bgv001.csv",
        "phone_bg.tlg",
        "sd_layer.png",
        "event_clip.png",
        "stage_second.png",
        "anj_loop_01.ogg",
        "anj_loop_69.ogg",
        "anj_loop_69c.ogg.sli",
        "MixedCase.bin",
        "mixedcase.bin",
        "oracle_log.bin",
        "oracle/path/",
        "scenario/",
        "data/main/",
        "main/"
    )) {
        Assert-True ($hxGeneratedValues -ccontains $expectedName) (
            "Hx v4 generated candidate: $expectedName")
    }

    $hxMissingVoicesResult = Invoke-Cli -Arguments @(
        "hxv4", "find-missing-voices",
        "--voice-dir", $hxVoice,
        "--output", "json")
    Assert-Equal 0 $hxMissingVoicesResult.ExitCode (
        "Hx v4 missing-voice scan exit code")
    $hxMissingVoices = Read-JsonEnvelope $hxMissingVoicesResult
    Assert-Equal 1 $hxMissingVoices.data.prefixCount (
        "Hx v4 missing-voice prefix count")
    Assert-Equal 552 $hxMissingVoices.data.candidateCount (
        "Hx v4 missing-voice candidate count")
    Assert-Equal 275 $hxMissingVoices.data.missingCount (
        "Hx v4 missing-voice missing count")
    Assert-True ($hxMissingVoices.data.missingVoiceStems -contains
        "anj_loop_69c") "Hx v4 reports a possible missing voice"
    Assert-True (-not ($hxMissingVoices.data.missingVoiceStems -contains
        "anj_loop_64")) "Hx v4 excludes an existing voice"

    if (-not [string]::IsNullOrWhiteSpace($HxV4UpstreamRoot)) {
        $pythonCommand = Get-Command python -ErrorAction Stop
        $oracleScript = Join-Path (
            Join-Path $PSScriptRoot "Fixtures") "hxv4-upstream-oracle.py"
        $oracleStderr = Join-Path $testRoot "hxv4-upstream-oracle.stderr"
        $oracleOutput = @(
            & $pythonCommand.Source $oracleScript `
                --upstream-root $HxV4UpstreamRoot `
                --source-root $hxSource `
                --krkrdump-root $hxKrkrDump 2> $oracleStderr
        )
        $oracleExitCode = $LASTEXITCODE
        $oracleDiagnostics = [IO.File]::ReadAllText($oracleStderr)
        Assert-Equal 0 $oracleExitCode (
            "upstream Hx v4 oracle exit code: $oracleDiagnostics")
        Assert-True ($oracleOutput.Count -ge 1) "upstream Hx v4 oracle output"
        $oracle = $oracleOutput[-1] | ConvertFrom-Json
        foreach ($upstreamName in @($oracle.files)) {
            Assert-True ($hxGeneratedValues -ccontains $upstreamName) (
                "local Hx v4 file candidates include upstream: $upstreamName")
        }
        foreach ($upstreamPath in @($oracle.paths)) {
            Assert-True ($hxGeneratedValues -ccontains $upstreamPath) (
                "local Hx v4 path candidates include upstream: $upstreamPath")
        }
    }

    $hxDeobfuscated = Join-Path $testRoot "hxv4-deobfuscated"
    $hxDeobfuscatedPack = Join-Path $hxDeobfuscated "pack\data\main"
    New-Item -ItemType Directory -Path $hxDeobfuscatedPack -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $hxDeobfuscatedPack "base.stage"), "fixture",
        [Text.Encoding]::UTF8)
    $hxCleanFile = Join-Path $testRoot "HxNames-clean.lst"
    $hxCleanResult = Invoke-Cli -Arguments @(
        "hxv4", "clean", $hxGeneratedFile,
        "--deobfuscated-dir", $hxDeobfuscated,
        "--destination", $hxCleanFile,
        "--output", "json")
    Assert-Equal 0 $hxCleanResult.ExitCode "Hx v4 clean exit code"
    $hxClean = Read-JsonEnvelope $hxCleanResult
    Assert-True ($hxClean.data.writtenEntries -ge 2) (
        "Hx v4 clean writes observed entries")
    $hxCleanValues = @(
        Get-Content -LiteralPath $hxCleanFile |
            ForEach-Object { ($_ -split ":", 2)[1] }
    )
    Assert-True ($hxCleanValues -ccontains "base.stage") (
        "Hx v4 clean keeps observed file")
    Assert-True ($hxCleanValues -ccontains "data/main/") (
        "Hx v4 clean keeps observed path")

    $hxRestoreRoot = Join-Path $testRoot "hxv4-restore"
    New-Item -ItemType Directory -Path $hxRestoreRoot | Out-Null
    $hxFlatFile = Join-Path $hxRestoreRoot "AAAA_BBBB_test.bin"
    [IO.File]::WriteAllBytes($hxFlatFile, [byte[]](7, 8, 9))
    $hxRestoreDryResult = Invoke-Cli -Arguments @(
        "hxv4", "restore-structure", $hxRestoreRoot,
        "--dry-run", "--output", "json")
    Assert-Equal 0 $hxRestoreDryResult.ExitCode "Hx v4 restore dry-run exit code"
    $hxRestoreDry = Read-JsonEnvelope $hxRestoreDryResult
    Assert-Equal 1 $hxRestoreDry.data.planned "Hx v4 restore dry-run plan"
    Assert-True (Test-Path -LiteralPath $hxFlatFile -PathType Leaf) (
        "Hx v4 restore dry-run preserves source")
    $hxRestoreResult = Invoke-Cli -Arguments @(
        "hxv4", "restore-structure", $hxRestoreRoot, "--output", "json")
    Assert-Equal 0 $hxRestoreResult.ExitCode "Hx v4 restore exit code"
    $hxRestore = Read-JsonEnvelope $hxRestoreResult
    Assert-Equal 1 $hxRestore.data.changed "Hx v4 restore changed count"
    Assert-True (Test-Path -LiteralPath (
        Join-Path $hxRestoreRoot "AAAA\BBBB\test.bin") -PathType Leaf) (
        "Hx v4 restore creates directory tree")

    $hxRenameRoot = Join-Path $testRoot "hxv4-rename"
    New-Item -ItemType Directory -Path $hxRenameRoot | Out-Null
    $hxHashedFile = Join-Path $hxRenameRoot $hxFileHash.data.hash
    [IO.File]::WriteAllBytes($hxHashedFile, [byte[]](10, 11, 12))
    $hxSecondHashedFile = Join-Path $hxRenameRoot ("D" * 64)
    [IO.File]::WriteAllBytes($hxSecondHashedFile, [byte[]](13, 14, 15))
    $hxVoiceHashResult = Invoke-Cli -Arguments @(
        "hxv4", "hash", "voice/", "--kind", "path", "--output", "json")
    Assert-Equal 0 $hxVoiceHashResult.ExitCode "Hx v4 voice path hash exit code"
    $hxVoiceHash = Read-JsonEnvelope $hxVoiceHashResult
    $hxHashedDirectory = Join-Path $hxRenameRoot $hxVoiceHash.data.hash
    New-Item -ItemType Directory -Path $hxHashedDirectory | Out-Null
    $hxRenameNames = Join-Path $testRoot "HxNames-rename.lst"
    [IO.File]::WriteAllLines(
        $hxRenameNames,
        [string[]]@(
            "$($hxFileHash.data.hash):startup.tjs",
            "$('D' * 64):startup.tjs",
            "$($hxVoiceHash.data.hash):voice/"
        ),
        [Text.UTF8Encoding]::new($false))
    $hxRenameDryResult = Invoke-Cli -Arguments @(
        "hxv4", "rename", $hxRenameRoot,
        "--names", $hxRenameNames, "--dry-run", "--output", "json")
    Assert-Equal 0 $hxRenameDryResult.ExitCode "Hx v4 rename dry-run exit code"
    $hxRenameDry = Read-JsonEnvelope $hxRenameDryResult
    Assert-Equal 3 $hxRenameDry.data.planned "Hx v4 rename dry-run plan"
    $hxPlannedDestinations = @(
        $hxRenameDry.data.items |
            Where-Object kind -eq "file" |
            Select-Object -ExpandProperty destination
    )
    Assert-True ($hxPlannedDestinations -contains (
        Join-Path $hxRenameRoot "startup.tjs")) (
        "Hx v4 rename dry-run plans first conflict destination")
    Assert-True ($hxPlannedDestinations -contains (
        Join-Path $hxRenameRoot "startup_1.tjs")) (
        "Hx v4 rename dry-run reserves unique conflict destination")
    Assert-True (Test-Path -LiteralPath $hxHashedFile -PathType Leaf) (
        "Hx v4 rename dry-run preserves hashed file")
    $hxRenameResult = Invoke-Cli -Arguments @(
        "hxv4", "rename", $hxRenameRoot,
        "--names", $hxRenameNames, "--output", "json")
    Assert-Equal 0 $hxRenameResult.ExitCode "Hx v4 rename exit code"
    $hxRename = Read-JsonEnvelope $hxRenameResult
    Assert-Equal 3 $hxRename.data.changed "Hx v4 rename changed count"
    Assert-True (Test-Path -LiteralPath (
        Join-Path $hxRenameRoot "startup.tjs") -PathType Leaf) (
        "Hx v4 rename restores file name")
    Assert-True (Test-Path -LiteralPath (
        Join-Path $hxRenameRoot "startup_1.tjs") -PathType Leaf) (
        "Hx v4 rename preserves file-name conflict")
    Assert-True (Test-Path -LiteralPath (
        Join-Path $hxRenameRoot "voice") -PathType Container) (
        "Hx v4 rename restores directory name")

    $hxFakeArchive = Join-Path $testRoot "fake.xp3"
    $hxFakeExe = Join-Path $testRoot "fake.exe"
    [IO.File]::WriteAllBytes($hxFakeArchive, [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllBytes($hxFakeExe, [byte[]](0x4d, 0x5a, 0, 0))
    $hxMissingSchemeResult = Invoke-Cli -Arguments @(
        "hxv4", "generate-archive", $hxFakeArchive,
        "--scheme", "__missing_hxv4_scheme__",
        "--destination", (Join-Path $testRoot "HxNames-index-filtered.lst"),
        "--output", "json")
    Assert-Equal 3 $hxMissingSchemeResult.ExitCode (
        "Hx v4 archive generation missing scheme exit code")
    $hxMissingScheme = Read-JsonEnvelope $hxMissingSchemeResult
    Assert-Equal "hxv4_scheme_not_found" $hxMissingScheme.error.code (
        "Hx v4 archive generation missing scheme error")
    Assert-True ($hxMissingScheme.error.details.availableSchemes.Count -gt 0) (
        "Hx v4 archive generation reports available schemes")

    $hxStructuredFailureDestination = Join-Path `
        $testRoot "HxNames-structured-failure.lst"
    $hxStructuredFailureResult = Invoke-Cli -Arguments @(
        "hxv4", "generate-archive", $hxFakeArchive,
        "--scheme", $hxSchemes.data.schemes[0],
        "--destination", $hxStructuredFailureDestination,
        "--output", "jsonl")
    Assert-Equal 3 $hxStructuredFailureResult.ExitCode (
        "Hx v4 structured generation failure exit code")
    $hxStructuredFailureEvents = Read-JsonLines $hxStructuredFailureResult
    $hxProgressEvents = @(
        $hxStructuredFailureEvents | Where-Object event -eq "progress"
    )
    Assert-True ($hxProgressEvents.Count -ge 1) (
        "Hx v4 JSONL emits progress before a generation failure")
    Assert-True ($hxProgressEvents.Count -le 3) (
        "Hx v4 progress is throttled for a one-archive fixture")
    foreach ($progress in $hxProgressEvents) {
        Assert-True (-not [string]::IsNullOrWhiteSpace($progress.data.phase)) (
            "Hx v4 progress phase")
        Assert-True ($progress.data.elapsedMs -ge 0) (
            "Hx v4 progress elapsed time")
    }
    $hxStructuredFailure = $hxStructuredFailureEvents[-1]
    Assert-Equal "hxv4_generation_failed" $hxStructuredFailure.error.code (
        "Hx v4 structured generation failure code")
    Assert-Equal "no_readable_index" (
        $hxStructuredFailure.error.details.reasonCode) (
        "Hx v4 structured generation reason")
    Assert-Equal "hxnames-preset-only" (
        $hxStructuredFailure.error.details.autoDetectionScope) (
        "Hx v4 auto detection scope")
    Assert-Equal 1 $hxStructuredFailure.error.details.indexArchivesTried (
        "Hx v4 attempted index archive count")
    Assert-Equal 0 $hxStructuredFailure.error.details.readableIndexCount (
        "Hx v4 readable index count")
    Assert-True (
        $hxStructuredFailure.error.details.recommendedActions -contains
            "select_scheme") "Hx v4 structured select-scheme recommendation"
    Assert-True (
        $hxStructuredFailure.error.details.recommendedActions -contains
            "run_krkrdump") "Hx v4 structured KrkrDump recommendation"
    Assert-True (-not (Test-Path -LiteralPath $hxStructuredFailureDestination)) (
        "Hx v4 failed generation does not create a names file")

    $hxExistingDump = Join-Path $testRoot "existing-krkrdump"
    New-Item -ItemType Directory -Path $hxExistingDump | Out-Null
    $hxSyntheticLog = @"
Parsing archive: fake.xp3
Index Key: 000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F
Index Nonce: 101112131415161718191A1B1C1D1E1F
Filter Key: 0x123456789ABCDEF0
Split Pos Mask: 0xFFFFFFFF
Split Pos: 0x00000000
Random Type: 0
Cxdec Order (8): 0,1,2,3,4,5,6,7
Cxdec Order (6): 0,1,2,3,4,5
Cxdec Order (3): 0,1,2
NameHash: "startup.tjs" "" "$($hxFileHash.data.hash)"
PathHash: "voice/" "" "$($hxVoiceHash.data.hash)"
"@
    [IO.File]::WriteAllText(
        (Join-Path $hxExistingDump "KrkrDump-fixture.log"),
        $hxSyntheticLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $hxExistingDump "CxdecTable.bin"),
        [BitConverter]::GetBytes([uint32]0x12345678))
    $hxKrkrDumpImportResult = Invoke-Cli -Arguments @(
        "hxv4", "krkrdump-import", $hxFakeArchive,
        "--result-dir", $hxExistingDump,
        "--game-executable", $hxFakeExe,
        "--output", "json")
    Assert-Equal 0 $hxKrkrDumpImportResult.ExitCode (
        "Hx v4 existing KrkrDump import exit code")
    $hxKrkrDumpImport = Read-JsonEnvelope $hxKrkrDumpImportResult
    Assert-True $hxKrkrDumpImport.data.imported (
        "Hx v4 existing KrkrDump result imported")
    Assert-True (-not [string]::IsNullOrWhiteSpace(
        $hxKrkrDumpImport.data.schemeName)) (
        "Hx v4 existing KrkrDump scheme name")
    Assert-True (-not [string]::IsNullOrWhiteSpace(
        $hxKrkrDumpImport.data.namesFile)) (
        "Hx v4 existing KrkrDump imported names file")
    $hxImportedNames = Join-Path $hxExistingDump "HxNames.lst"
    Assert-True (Test-Path -LiteralPath $hxImportedNames -PathType Leaf) (
        "Hx v4 existing KrkrDump names file")
    $hxImportedValues = @(
        Get-Content -LiteralPath $hxImportedNames |
            ForEach-Object { ($_ -split ":", 2)[1] }
    )
    Assert-True ($hxImportedValues -ccontains "startup.tjs") (
        "Hx v4 existing KrkrDump imports file name")
    Assert-True ($hxImportedValues -ccontains "voice/") (
        "Hx v4 existing KrkrDump imports path name")

    $cxReadOnlyDump = Join-Path $testRoot "cx-read-only-scheme"
    New-Item -ItemType Directory -Path $cxReadOnlyDump | Out-Null
    Copy-Item -LiteralPath (
        Join-Path $hxExistingDump "KrkrDump-fixture.log") -Destination (
        Join-Path $cxReadOnlyDump "KrkrDump-fixture.log")
    [IO.File]::WriteAllBytes(
        (Join-Path $cxReadOnlyDump "CxdecTable.bin"),
        [byte[]]::new(0x1000))
    $cxReadOnlyNames = Join-Path $cxReadOnlyDump "HxNames.lst"
    [IO.File]::WriteAllText(
        $cxReadOnlyNames,
        "DO-NOT-OVERWRITE:sentinel`r`n",
        [Text.UTF8Encoding]::new($false))
    $cxReadOnlyBefore = @(
        Get-ChildItem -LiteralPath $cxReadOnlyDump -File |
            Sort-Object Name |
            ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    Hash = (Get-FileHash -LiteralPath $_.FullName `
                        -Algorithm SHA256).Hash
                }
            }
    )
    $cxReadOnlyResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFakeArchive,
        "--cx-dump-dir", $cxReadOnlyDump,
        "--output", "json")
    Assert-Equal 3 $cxReadOnlyResult.ExitCode (
        "Cx dump read-only scheme-check reaches archive validation")
    $cxReadOnly = Read-JsonEnvelope $cxReadOnlyResult
    Assert-Equal "xp3_scheme_rejected" $cxReadOnly.error.code (
        "Cx dump read-only scheme-check resolution succeeds")
    Assert-True (-not (
        $cxReadOnly.error.details.schemeResolution.cxNamesCacheWritten)) (
        "Cx dump scheme resolution reports no names cache write")
    $cxReadOnlyAfter = @(
        Get-ChildItem -LiteralPath $cxReadOnlyDump -File |
            Sort-Object Name |
            ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    Hash = (Get-FileHash -LiteralPath $_.FullName `
                        -Algorithm SHA256).Hash
                }
            }
    )
    Assert-Equal ($cxReadOnlyBefore.Name -join ",") (
        $cxReadOnlyAfter.Name -join ",") (
        "Cx dump scheme-check adds no directory artifacts")
    foreach ($beforeFile in $cxReadOnlyBefore) {
        $afterFile = $cxReadOnlyAfter |
            Where-Object Name -CEQ $beforeFile.Name
        Assert-Equal $beforeFile.Hash $afterFile.Hash (
            "Cx dump scheme-check preserves $($beforeFile.Name)")
    }
    Assert-Equal "DO-NOT-OVERWRITE:sentinel" (
        [IO.File]::ReadAllText($cxReadOnlyNames).Trim()) (
        "Cx dump scheme-check does not overwrite HxNames.lst")

    $cxInvalidNumericDump = Join-Path $testRoot "cx-invalid-numeric"
    New-Item -ItemType Directory -Path $cxInvalidNumericDump | Out-Null
    $cxInvalidNumericLog = $hxSyntheticLog.Replace(
        "Filter Key: 0x123456789ABCDEF0",
        "Filter Key: 0x123456789ABCDEF0123456789ABCDEF0")
    [IO.File]::WriteAllText(
        (Join-Path $cxInvalidNumericDump "KrkrDump-invalid.log"),
        $cxInvalidNumericLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $cxInvalidNumericDump "CxdecTable.bin"),
        [byte[]]::new(0x1000))
    $cxInvalidNumericResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFakeArchive,
        "--cx-dump-dir", $cxInvalidNumericDump,
        "--output", "json")
    Assert-Equal 3 $cxInvalidNumericResult.ExitCode (
        "Cx invalid numeric log exit code")
    $cxInvalidNumeric = Read-JsonEnvelope $cxInvalidNumericResult
    Assert-Equal "xp3_cx_dump_invalid" $cxInvalidNumeric.error.code (
        "Cx invalid numeric log stable error")
    Assert-True ($cxInvalidNumeric.error.details.message -like "*invalid*") (
        "Cx invalid numeric log diagnostic")

    $cxOverflowUintDump = Join-Path $testRoot "cx-overflow-uint"
    New-Item -ItemType Directory -Path $cxOverflowUintDump | Out-Null
    $cxOverflowUintLog = $hxSyntheticLog.Replace(
        "Split Pos: 0x00000000", "Split Pos: 0x100000000")
    [IO.File]::WriteAllText(
        (Join-Path $cxOverflowUintDump "KrkrDump-invalid.log"),
        $cxOverflowUintLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $cxOverflowUintDump "CxdecTable.bin"),
        [byte[]]::new(0x1000))
    $cxOverflowUintResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFakeArchive,
        "--cx-dump-dir", $cxOverflowUintDump, "--output", "json")
    Assert-Equal 3 $cxOverflowUintResult.ExitCode (
        "Cx overflowing uint log exit code")
    $cxOverflowUint = Read-JsonEnvelope $cxOverflowUintResult
    Assert-Equal "xp3_cx_dump_invalid" $cxOverflowUint.error.code (
        "Cx overflowing uint log stable error")

    $cxInvalidOrderDump = Join-Path $testRoot "cx-invalid-order"
    New-Item -ItemType Directory -Path $cxInvalidOrderDump | Out-Null
    $cxInvalidOrderLog = $hxSyntheticLog.Replace(
        "Cxdec Order (8): 0,1,2,3,4,5,6,7",
        "Cxdec Order (8): 0,1,2,3,4,5,6")
    [IO.File]::WriteAllText(
        (Join-Path $cxInvalidOrderDump "KrkrDump-invalid.log"),
        $cxInvalidOrderLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $cxInvalidOrderDump "CxdecTable.bin"),
        [byte[]]::new(0x1000))
    $cxInvalidOrderResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFakeArchive,
        "--cx-dump-dir", $cxInvalidOrderDump, "--output", "json")
    Assert-Equal 3 $cxInvalidOrderResult.ExitCode (
        "Cx invalid order log exit code")
    $cxInvalidOrder = Read-JsonEnvelope $cxInvalidOrderResult
    Assert-Equal "xp3_cx_dump_invalid" $cxInvalidOrder.error.code (
        "Cx invalid order log stable error")

    $cxInvalidKeyDump = Join-Path $testRoot "cx-invalid-key"
    New-Item -ItemType Directory -Path $cxInvalidKeyDump | Out-Null
    $cxInvalidKeyLog = $hxSyntheticLog.Replace(
        "Index Key: 000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
        "Index Key: 000102030405060708090A0B0C0D0E0F")
    [IO.File]::WriteAllText(
        (Join-Path $cxInvalidKeyDump "KrkrDump-invalid.log"),
        $cxInvalidKeyLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $cxInvalidKeyDump "CxdecTable.bin"),
        [byte[]]::new(0x1000))
    $cxInvalidKeyResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFakeArchive,
        "--cx-dump-dir", $cxInvalidKeyDump, "--output", "json")
    Assert-Equal 3 $cxInvalidKeyResult.ExitCode (
        "Cx invalid key length exit code")
    $cxInvalidKey = Read-JsonEnvelope $cxInvalidKeyResult
    Assert-Equal "xp3_cx_dump_invalid" $cxInvalidKey.error.code (
        "Cx invalid key length stable error")

    $cxOversizedDump = Join-Path $testRoot "cx-oversized-artifact"
    New-Item -ItemType Directory -Path $cxOversizedDump | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $cxOversizedDump "KrkrDump-oversized.log"),
        $hxSyntheticLog, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $cxOversizedDump "CxdecTable.bin"),
        [byte[]]::new(0x1000))
    $cxOversizedOrderPath = Join-Path $cxOversizedDump "CxdecOrder.bin"
    $cxOversizedOrder = [IO.File]::Create($cxOversizedOrderPath)
    try {
        $cxOversizedOrder.SetLength(65537)
    }
    finally {
        $cxOversizedOrder.Dispose()
    }
    $cxOversizedResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFakeArchive,
        "--cx-dump-dir", $cxOversizedDump, "--output", "json")
    Assert-Equal 3 $cxOversizedResult.ExitCode (
        "Cx oversized artifact exit code")
    $cxOversized = Read-JsonEnvelope $cxOversizedResult
    Assert-Equal "xp3_cx_dump_invalid" $cxOversized.error.code (
        "Cx oversized artifact stable error")

    $hxFixtureSchemes = Get-HxFixtureSchemes
    $hxFixtureScheme = $hxFixtureSchemes.Selected
    $hxFixtureWrongScheme = $hxFixtureSchemes.Wrong
    [uint32]$hxFixtureEntryId = 0x12345678
    [int64]$hxFixtureEntryKey = 0x0123456789ABCDEF
    $hxFixturePng = [Convert]::FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    [byte[]]$hxFixtureEncryptedPng = Protect-HxFixtureContent `
        -SchemeInfo $hxFixtureScheme -EntryId $hxFixtureEntryId `
        -EntryKey $hxFixtureEntryKey -Content $hxFixturePng
    $hxFixturePathHashResult = Invoke-Cli -Arguments @(
        "hxv4", "hash", "images/", "--kind", "path", "--output", "json")
    Assert-Equal 0 $hxFixturePathHashResult.ExitCode (
        "synthetic Hx fixture path hash exit code")
    $hxFixturePathHash = Read-JsonEnvelope $hxFixturePathHashResult
    $hxFixtureNameHashResult = Invoke-Cli -Arguments @(
        "hxv4", "hash", "pixel.png", "--kind", "file", "--output", "json")
    Assert-Equal 0 $hxFixtureNameHashResult.ExitCode (
        "synthetic Hx fixture name hash exit code")
    $hxFixtureNameHash = Read-JsonEnvelope $hxFixtureNameHashResult
    [byte[]]$hxFixturePathHashBytes = Convert-HexToBytes (
        $hxFixturePathHash.data.hash)
    [byte[]]$hxFixtureNameHashBytes = Convert-HexToBytes (
        $hxFixtureNameHash.data.hash)
    $hxFixtureArchive = Join-Path $testRoot "synthetic-hx-scheme.xp3"
    $createHxArguments = [object[]]::new(9)
    $createHxArguments[0] = [string]$hxFixtureArchive
    $createHxArguments[1] = [string]$hxFixtureScheme.ArcFormatsAssembly.Location
    $createHxArguments[2] = [byte[]]$hxFixtureScheme.IndexKey
    $createHxArguments[3] = [byte[]]$hxFixtureScheme.IndexNonce
    $createHxArguments[4] = [byte[]]$hxFixturePathHashBytes
    $createHxArguments[5] = [byte[]]$hxFixtureNameHashBytes
    $createHxArguments[6] = $hxFixtureEntryId
    $createHxArguments[7] = $hxFixtureEntryKey
    $createHxArguments[8] = [byte[]]$hxFixtureEncryptedPng
    [void]$script:xp3BuilderType.GetMethod("CreateHx").Invoke(
        $null, $createHxArguments)

    $hxFixtureNames = Join-Path $testRoot "synthetic-hx.HxNames.lst"
    [IO.File]::WriteAllLines(
        $hxFixtureNames,
        [string[]]@(
            ($hxFixturePathHash.data.hash + ":images/"),
            ($hxFixtureNameHash.data.hash + ":pixel.png")
        ),
        [Text.UTF8Encoding]::new($false))
    $hxFixtureCxDump = Join-Path $testRoot "synthetic-hx-cx"
    $writeCxArguments = [object[]]::new(12)
    $writeCxArguments[0] = [string]$hxFixtureCxDump
    $writeCxArguments[1] = [string][IO.Path]::GetFileName($hxFixtureArchive)
    $writeCxArguments[2] = [byte[]]$hxFixtureScheme.IndexKey
    $writeCxArguments[3] = [byte[]]$hxFixtureScheme.IndexNonce
    $writeCxArguments[4] = [uint64]$hxFixtureScheme.FilterKey
    $writeCxArguments[5] = [uint32]$hxFixtureScheme.SplitMask
    $writeCxArguments[6] = [uint32]$hxFixtureScheme.SplitPosition
    $writeCxArguments[7] = [int]$hxFixtureScheme.RandomType
    $writeCxArguments[8] = [byte[]]$hxFixtureScheme.EvenOrder
    $writeCxArguments[9] = [byte[]]$hxFixtureScheme.OddOrder
    $writeCxArguments[10] = [byte[]]$hxFixtureScheme.PrologOrder
    $writeCxArguments[11] = [uint32[]]$hxFixtureScheme.ControlBlock
    [void]$script:xp3BuilderType.GetMethod("WriteCxDump").Invoke(
        $null, $writeCxArguments)
    [IO.File]::AppendAllText(
        (Join-Path $hxFixtureCxDump "KrkrDump-fixture.log"),
        ('NameHash: "base.png" "" "' +
            $hxFixtureNameHash.data.hash + '"' + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))

    $hxFixtureBaseResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--scheme", $hxFixtureScheme.Name,
        "--hx-names", $hxFixtureNames, "--output", "json")
    Assert-Equal 0 $hxFixtureBaseResult.ExitCode (
        "synthetic HxNames-only scheme-check exit code")
    $hxFixtureBase = Read-JsonEnvelope $hxFixtureBaseResult
    Assert-Equal "matched" $hxFixtureBase.data.contentValidation.status (
        "synthetic HxNames-only content validation")
    Assert-Equal "images/pixel.png" (
        $hxFixtureBase.data.contentValidation.matches[0].entry) (
        "synthetic HxNames-only resolves the logical entry name")

    $hxFixtureWrongResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--scheme", $hxFixtureWrongScheme.Name,
        "--hx-names", $hxFixtureNames, "--output", "json")
    Assert-Equal 3 $hxFixtureWrongResult.ExitCode (
        "synthetic Hx wrong scheme exit code")
    $hxFixtureWrong = Read-JsonEnvelope $hxFixtureWrongResult
    Assert-Equal "xp3_hx_names_invalid" $hxFixtureWrong.error.code (
        "synthetic Hx wrong scheme stable error")

    $hxFixtureWrongWithoutNamesResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--scheme", $hxFixtureWrongScheme.Name, "--output", "json")
    Assert-Equal 3 $hxFixtureWrongWithoutNamesResult.ExitCode (
        "synthetic Hx wrong scheme without names exit code")
    $hxFixtureWrongWithoutNames = Read-JsonEnvelope (
        $hxFixtureWrongWithoutNamesResult)
    Assert-Equal "xp3_scheme_check_failed" (
        $hxFixtureWrongWithoutNames.error.code) (
        "synthetic Hx wrong scheme without names stable error")
    Assert-Equal "hx_index_unresolved" (
        $hxFixtureWrongWithoutNames.error.details.contentValidation.failures[0].reason) (
        "synthetic Hx wrong scheme identifies the unresolved index")

    $hxFixtureCxOnlyResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--cx-dump-dir", $hxFixtureCxDump, "--output", "json")
    Assert-Equal 0 $hxFixtureCxOnlyResult.ExitCode (
        "synthetic Cx-only scheme-check exit code")
    $hxFixtureCxOnly = Read-JsonEnvelope $hxFixtureCxOnlyResult
    Assert-Equal "xp3:cx-dump" (
        $hxFixtureCxOnly.data.schemeResolution.identity) (
        "synthetic Cx-only scheme identity")
    Assert-Equal "matched" (
        $hxFixtureCxOnly.data.contentValidation.status) (
        "synthetic Cx-only decrypts recognizable content")

    $hxFixtureCxCompatResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--cx-dump-dir", ($hxFixtureCxDump + "|garbro-importer"),
        "--output", "json")
    Assert-Equal 0 $hxFixtureCxCompatResult.ExitCode (
        "synthetic Cx compatibility modifier exit code")
    $hxFixtureCxCompat = Read-JsonEnvelope $hxFixtureCxCompatResult
    Assert-True (
        $hxFixtureCxCompat.data.schemeResolution.cxCompatModifierStripped) (
        "synthetic Cx compatibility modifier is reported as stripped")
    Assert-Equal "matched" (
        $hxFixtureCxCompat.data.contentValidation.status) (
        "synthetic Cx compatibility modifier preserves decryption")

    $hxFixtureCxBadModifierResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--cx-dump-dir", ($hxFixtureCxDump + "|unsupported"),
        "--output", "json")
    Assert-Equal 3 $hxFixtureCxBadModifierResult.ExitCode (
        "synthetic Cx unknown compatibility modifier exit code")
    $hxFixtureCxBadModifier = Read-JsonEnvelope (
        $hxFixtureCxBadModifierResult)
    Assert-Equal "xp3_cx_dump_modifier_invalid" (
        $hxFixtureCxBadModifier.error.code) (
        "synthetic Cx unknown compatibility modifier stable error")

    $hxFixtureCombinedResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--scheme", $hxFixtureScheme.Name,
        "--cx-dump-dir", ($hxFixtureCxDump + "|garbro-importer"),
        "--hx-names", $hxFixtureNames, "--output", "json")
    Assert-Equal 0 $hxFixtureCombinedResult.ExitCode (
        "synthetic base+Cx+HxNames scheme-check exit code")
    $hxFixtureCombined = Read-JsonEnvelope $hxFixtureCombinedResult
    Assert-Equal "xp3:cx-dump+hx-names" (
        $hxFixtureCombined.data.schemeResolution.identity) (
        "synthetic combined scheme identity")
    Assert-True $hxFixtureCombined.data.schemeResolution.baseSchemeSuperseded (
        "synthetic Cx dump supersedes the base scheme")
    Assert-Equal "matched" (
        $hxFixtureCombined.data.contentValidation.status) (
        "synthetic combined scheme validates content")
    Assert-Equal "images/pixel.png" (
        $hxFixtureCombined.data.contentValidation.matches[0].entry) (
        "explicit HxNames overlay wins over Cx inline names")

    $hxFixtureCombinedDestination = Join-Path `
        $testRoot "synthetic-hx-combined-output"
    $hxFixtureCombinedManifest = Join-Path `
        $testRoot "synthetic-hx-combined-manifest.jsonl"
    $hxFixtureCombinedExtractResult = Invoke-Cli -Arguments @(
        "archive", "extract", $hxFixtureArchive,
        "--destination", $hxFixtureCombinedDestination,
        "--scheme", $hxFixtureScheme.Name,
        "--cx-dump-dir", ($hxFixtureCxDump + "|garbro-importer"),
        "--hx-names", $hxFixtureNames, "--budget", "auto",
        "--manifest", $hxFixtureCombinedManifest,
        "--checksum", "sha256", "--summary-only", "--output", "json")
    Assert-Equal 0 $hxFixtureCombinedExtractResult.ExitCode (
        "synthetic combined scheme extraction exit code")
    $hxFixtureCombinedExtract = Read-JsonEnvelope (
        $hxFixtureCombinedExtractResult)
    Assert-Equal 1 $hxFixtureCombinedExtract.data.written (
        "synthetic combined scheme extraction written count")
    $hxFixtureCombinedOutput = Join-Path `
        $hxFixtureCombinedDestination "images\pixel.png"
    Assert-Equal ([Convert]::ToBase64String($hxFixturePng)) (
        [Convert]::ToBase64String(
            [IO.File]::ReadAllBytes($hxFixtureCombinedOutput))) (
        "synthetic combined scheme extraction decrypts the exact payload")

    $hxFixtureCombinedEquivalentResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $hxFixtureArchive,
        "--destination", $hxFixtureCombinedDestination,
        "--scheme", $hxFixtureScheme.Name,
        "--cx-dump-dir", $hxFixtureCxDump,
        "--hx-names", $hxFixtureNames, "--budget", "auto",
        "--resume", "verify-hash",
        "--resume-manifest", $hxFixtureCombinedManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $hxFixtureCombinedEquivalentResumeResult.ExitCode (
        "equivalent Cx compatibility spelling resume exit code")
    $hxFixtureCombinedEquivalentResume = Read-JsonEnvelope (
        $hxFixtureCombinedEquivalentResumeResult)
    Assert-Equal 1 (
        $hxFixtureCombinedEquivalentResume.data.verifiedExisting) (
        "equivalent Cx compatibility spelling verifies the output")

    $hxFixtureDestination = Join-Path $testRoot "synthetic-hx-output"
    $hxFixtureManifest = Join-Path $testRoot "synthetic-hx-manifest.jsonl"
    $hxFixtureExtractResult = Invoke-Cli -Arguments @(
        "archive", "extract", $hxFixtureArchive,
        "--destination", $hxFixtureDestination,
        "--scheme", $hxFixtureScheme.Name,
        "--hx-names", $hxFixtureNames, "--budget", "auto",
        "--manifest", $hxFixtureManifest, "--checksum", "sha256",
        "--summary-only", "--output", "json")
    Assert-Equal 0 $hxFixtureExtractResult.ExitCode (
        "synthetic HxNames extraction exit code")
    $hxFixtureExtract = Read-JsonEnvelope $hxFixtureExtractResult
    Assert-Equal 1 $hxFixtureExtract.data.written (
        "synthetic HxNames extraction written count")
    $hxFixtureOutput = Join-Path $hxFixtureDestination "images\pixel.png"
    Assert-Equal ([Convert]::ToBase64String($hxFixturePng)) (
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($hxFixtureOutput))) (
        "synthetic HxNames extraction decrypts the exact payload")

    $hxFixtureResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $hxFixtureArchive,
        "--destination", $hxFixtureDestination,
        "--scheme", $hxFixtureScheme.Name,
        "--hx-names", $hxFixtureNames, "--budget", "auto",
        "--resume", "verify-hash", "--resume-manifest", $hxFixtureManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $hxFixtureResumeResult.ExitCode (
        "synthetic HxNames resume exit code")
    $hxFixtureResume = Read-JsonEnvelope $hxFixtureResumeResult
    Assert-Equal 1 $hxFixtureResume.data.verifiedExisting (
        "synthetic HxNames resume verifies the output")

    $hxFixtureSchemeMismatchResult = Invoke-Cli -Arguments @(
        "archive", "extract", $hxFixtureArchive,
        "--destination", $hxFixtureDestination,
        "--scheme", $hxFixtureScheme.Name,
        "--cx-dump-dir", $hxFixtureCxDump,
        "--hx-names", $hxFixtureNames, "--budget", "auto",
        "--resume", "verify-hash", "--resume-manifest", $hxFixtureManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $hxFixtureSchemeMismatchResult.ExitCode (
        "synthetic scheme-fingerprint mismatch exit code")
    $hxFixtureSchemeMismatch = Read-JsonEnvelope (
        $hxFixtureSchemeMismatchResult)
    Assert-Equal "manifest_handler_mismatch" (
        $hxFixtureSchemeMismatch.error.code) (
        "synthetic scheme-fingerprint mismatch stable error")

    $hxFixtureJunctionTarget = Join-Path $testRoot "synthetic-cx-junction-target"
    $hxFixtureJunctionDump = Join-Path $hxFixtureJunctionTarget "dump"
    $junctionCxArguments = [object[]]::new(12)
    $junctionCxArguments[0] = [string]$hxFixtureJunctionDump
    $junctionCxArguments[1] = [string][IO.Path]::GetFileName(
        $hxFixtureArchive)
    $junctionCxArguments[2] = [byte[]]$hxFixtureScheme.IndexKey
    $junctionCxArguments[3] = [byte[]]$hxFixtureScheme.IndexNonce
    $junctionCxArguments[4] = [uint64]$hxFixtureScheme.FilterKey
    $junctionCxArguments[5] = [uint32]$hxFixtureScheme.SplitMask
    $junctionCxArguments[6] = [uint32]$hxFixtureScheme.SplitPosition
    $junctionCxArguments[7] = [int]$hxFixtureScheme.RandomType
    $junctionCxArguments[8] = [byte[]]$hxFixtureScheme.EvenOrder
    $junctionCxArguments[9] = [byte[]]$hxFixtureScheme.OddOrder
    $junctionCxArguments[10] = [byte[]]$hxFixtureScheme.PrologOrder
    $junctionCxArguments[11] = [uint32[]]$hxFixtureScheme.ControlBlock
    [void]$script:xp3BuilderType.GetMethod("WriteCxDump").Invoke(
        $null, $junctionCxArguments)
    $hxFixtureJunction = Join-Path $testRoot "synthetic-cx-junction"
    New-Item -ItemType Junction -Path $hxFixtureJunction `
        -Target $hxFixtureJunctionTarget | Out-Null
    $hxFixtureJunctionResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $hxFixtureArchive,
        "--cx-dump-dir", (Join-Path $hxFixtureJunction "dump"),
        "--output", "json")
    Assert-Equal 3 $hxFixtureJunctionResult.ExitCode (
        "Cx dump ancestor junction exit code")
    $hxFixtureJunctionError = Read-JsonEnvelope $hxFixtureJunctionResult
    Assert-Equal "xp3_cx_dump_reparse_point" (
        $hxFixtureJunctionError.error.code) (
        "Cx dump ancestor junction stable error")

    $hxProgressDirectory = Join-Path $testRoot "synthetic-hx-progress"
    New-Item -ItemType Directory -Path $hxProgressDirectory | Out-Null
    $hxProgressArchive = Join-Path $hxProgressDirectory "progress.xp3"
    $createHxProgressArguments = [object[]]::new(10)
    $createHxProgressArguments[0] = [string]$hxProgressArchive
    $createHxProgressArguments[1] = [string](
        $hxFixtureScheme.ArcFormatsAssembly.Location)
    $createHxProgressArguments[2] = [byte[]]$hxFixtureScheme.IndexKey
    $createHxProgressArguments[3] = [byte[]]$hxFixtureScheme.IndexNonce
    $createHxProgressArguments[4] = [byte[]]$hxFixturePathHashBytes
    $createHxProgressArguments[5] = [byte[]]$hxFixtureNameHashBytes
    $createHxProgressArguments[6] = [uint32]$hxFixtureEntryId
    $createHxProgressArguments[7] = [int64]$hxFixtureEntryKey
    $createHxProgressArguments[8] = [byte[]]$hxFixtureEncryptedPng
    $createHxProgressArguments[9] = 250
    [void]$script:xp3BuilderType.GetMethod("CreateHxWithFillers").Invoke(
        $null, $createHxProgressArguments)
    $hxProgressDestination = Join-Path `
        $hxProgressDirectory "generated.HxNames.lst"
    $hxProgressResult = Invoke-Cli -Arguments @(
        "hxv4", "generate-archive", $hxProgressArchive,
        "--scheme", $hxFixtureScheme.Name,
        "--seed", $hxFixtureNames,
        "--destination", $hxProgressDestination,
        "--output", "jsonl")
    Assert-Equal 0 $hxProgressResult.ExitCode (
        "synthetic Hx progress generation exit code")
    $hxProgressEvents = Read-JsonLines $hxProgressResult
    $hxReadIndexProgress = @(
        $hxProgressEvents | Where-Object {
            $_.event -eq "progress" -and $_.data.phase -eq "read_indexes"
        })
    Assert-True ($hxReadIndexProgress.Count -ge 1) (
        "synthetic Hx generation reports index attempts")
    Assert-Equal 1 $hxReadIndexProgress[-1].data.archiveIndex (
        "synthetic Hx generation reports the final attempted index")
    $hxScanProgress = @(
        $hxProgressEvents | Where-Object {
            $_.event -eq "progress" -and $_.data.phase -eq "scan_entries"
        })
    Assert-True ($hxScanProgress.Count -ge 1) (
        "synthetic Hx generation reports entry scanning")
    Assert-True ($hxScanProgress.Count -lt 20) (
        "synthetic Hx generation throttles per-entry progress")
    Assert-True ($hxProgressEvents[-1].data.scannedEntryCount -ge 251) (
        "synthetic Hx generation scans every fixture entry")
    Assert-True (Test-Path -LiteralPath $hxProgressDestination -PathType Leaf) (
        "synthetic Hx generation writes the filtered names table")

    $hxKrkrDumpImportJsonlResult = Invoke-Cli -Arguments @(
        "hxv4", "krkrdump-import", $hxFakeArchive,
        "--result-dir", $hxExistingDump,
        "--game-executable", $hxFakeExe,
        "--output", "jsonl")
    Assert-Equal 0 $hxKrkrDumpImportJsonlResult.ExitCode (
        "Hx v4 existing KrkrDump JSONL import exit code")
    $hxKrkrDumpImportEvents = Read-JsonLines $hxKrkrDumpImportJsonlResult
    Assert-Equal 0 @(
        $hxKrkrDumpImportEvents | Where-Object event -eq "progress"
    ).Count "KrkrDump import must not start HxNames resource generation"

    $hxExistingRunDestination = Join-Path $testRoot "existing-krkrdump-run"
    New-Item -ItemType Directory -Path (
        Join-Path $hxExistingRunDestination ".krkrdump") -Force | Out-Null
    $hxExistingRunResult = Invoke-Cli -Arguments @(
        "hxv4", "krkrdump", $hxFakeArchive,
        "--game-executable", $hxFakeExe,
        "--destination", $hxExistingRunDestination,
        "--output", "json")
    Assert-Equal 6 $hxExistingRunResult.ExitCode (
        "Hx v4 KrkrDump existing destination exit code")
    $hxExistingRun = Read-JsonEnvelope $hxExistingRunResult
    Assert-Equal "krkrdump_destination_exists" $hxExistingRun.error.code (
        "Hx v4 KrkrDump existing destination error")

    $hxMissingRuntime = Join-Path $testRoot "missing-krkrdump-runtime"
    $hxKrkrDumpResult = Invoke-Cli -Arguments @(
        "hxv4", "krkrdump", $hxFakeArchive,
        "--game-executable", $hxFakeExe,
        "--destination", (Join-Path $testRoot "hxv4-krkrdump"),
        "--tool-directory", $hxMissingRuntime,
        "--output", "json")
    Assert-Equal 3 $hxKrkrDumpResult.ExitCode (
        "Hx v4 KrkrDump missing runtime exit code")
    $hxKrkrDump = Read-JsonEnvelope $hxKrkrDumpResult
    Assert-Equal "krkrdump_runtime_missing" $hxKrkrDump.error.code (
        "Hx v4 KrkrDump missing runtime error")

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

    $hierarchyZip = Join-Path $testRoot "hierarchy-collision.zip"
    New-HierarchyZip -Path $hierarchyZip
    $hierarchyDestination = Join-Path $testRoot "hierarchy-output"
    $hierarchyPlanResult = Invoke-Cli -Arguments @(
        "archive", "plan", $hierarchyZip,
        "--destination", $hierarchyDestination,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $hierarchyPlanResult.ExitCode (
        "archive file-directory hierarchy plan exit code")
    $hierarchyPlan = Read-JsonEnvelope $hierarchyPlanResult
    Assert-Equal "unsafe_output_path" $hierarchyPlan.error.code (
        "archive file-directory hierarchy plan error")
    Assert-Equal "destination_collision" $hierarchyPlan.error.details.reason (
        "archive file-directory hierarchy collision reason")
    $hierarchyExtractResult = Invoke-Cli -Arguments @(
        "archive", "extract", $hierarchyZip,
        "--destination", $hierarchyDestination,
        "--output", "json")
    Assert-Equal 3 $hierarchyExtractResult.ExitCode (
        "archive file-directory hierarchy extract exit code")
    Assert-True (-not (Test-Path -LiteralPath $hierarchyDestination)) (
        "archive hierarchy collision performs no writes")

    $destinationFile = Join-Path $testRoot "destination-is-file"
    [IO.File]::WriteAllText(
        $destinationFile, "destination sentinel",
        [Text.UTF8Encoding]::new($false))
    $destinationFileResult = Invoke-Cli -Arguments @(
        "archive", "plan", $hierarchyZip,
        "--destination", $destinationFile,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $destinationFileResult.ExitCode (
        "archive destination-file preflight exit code")
    $destinationFileError = Read-JsonEnvelope $destinationFileResult
    Assert-Equal "invalid_destination" $destinationFileError.error.code (
        "archive destination-file preflight error")
    Assert-Equal "destination sentinel" (
        [IO.File]::ReadAllText($destinationFile)) (
        "archive destination-file preflight preserves the file")

    $parentFileDestination = Join-Path $testRoot "parent-file-output"
    New-Item -ItemType Directory -Path $parentFileDestination | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $parentFileDestination "node"), "parent sentinel",
        [Text.UTF8Encoding]::new($false))
    $parentFileResult = Invoke-Cli -Arguments @(
        "archive", "plan", $hierarchyZip,
        "--destination", $parentFileDestination,
        "--entry-index", "1", "--summary-only", "--output", "json")
    Assert-Equal 3 $parentFileResult.ExitCode (
        "archive existing parent-file preflight exit code")
    $parentFileError = Read-JsonEnvelope $parentFileResult
    Assert-Equal "unsafe_output_path" $parentFileError.error.code (
        "archive existing parent-file preflight error")
    Assert-Equal "parent_is_file" $parentFileError.error.details.reason (
        "archive existing parent-file preflight reason")

    $reparseTarget = Join-Path $testRoot "reparse-target"
    $reparseLink = Join-Path $testRoot "reparse-link"
    New-Item -ItemType Directory -Path $reparseTarget | Out-Null
    New-Item -ItemType Junction -Path $reparseLink -Target $reparseTarget |
        Out-Null
    $reparseDestination = Join-Path $reparseLink "new-output"
    $reparseDestinationResult = Invoke-Cli -Arguments @(
        "archive", "plan", $hierarchyZip,
        "--destination", $reparseDestination,
        "--entry-index", "0", "--summary-only", "--output", "json")
    Assert-Equal 3 $reparseDestinationResult.ExitCode (
        "archive reparse-ancestor destination exit code")
    $reparseDestinationError = Read-JsonEnvelope $reparseDestinationResult
    Assert-Equal "invalid_destination" $reparseDestinationError.error.code (
        "archive reparse-ancestor destination error")
    Assert-Equal "reparse_point" (
        $reparseDestinationError.error.details.reason) (
        "archive reparse-ancestor destination reason")
    Assert-True (-not (Test-Path -LiteralPath (
        Join-Path $reparseTarget "new-output"))) (
        "archive reparse-ancestor destination performs no writes")

    $reparseImageTarget = Join-Path $testRoot "reparse-image-target"
    $reparseImageLink = Join-Path $testRoot "reparse-image-link"
    New-Item -ItemType Directory -Path $reparseImageTarget | Out-Null
    New-PngFixture -Path (Join-Path $reparseImageTarget "fixture.png")
    New-Item -ItemType Junction `
        -Path $reparseImageLink -Target $reparseImageTarget | Out-Null
    $reparseImageOutput = Join-Path $testRoot "reparse-image-output"
    $reparseImageResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $reparseImageLink,
        "--destination", $reparseImageOutput,
        "--format", "png", "--dry-run", "--output", "json")
    Assert-Equal 3 $reparseImageResult.ExitCode (
        "image batch reparse source-root exit code")
    $reparseImageError = Read-JsonEnvelope $reparseImageResult
    Assert-Equal "source_root_reparse_point" $reparseImageError.error.code (
        "image batch reparse source-root error")
    Assert-True (-not (Test-Path -LiteralPath $reparseImageOutput)) (
        "image batch reparse source-root performs no writes")

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

    $partialFailureZip = Join-Path $testRoot "partial-failure.zip"
    New-PartiallyUnderdeclaredZip -Path $partialFailureZip
    $partialFailureDestination = Join-Path $testRoot "partial-failure-out"
    $partialFailureManifest = Join-Path $testRoot "partial-failure.jsonl"
    $partialFailureResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialFailureZip,
        "--destination", $partialFailureDestination,
        "--max-entry-bytes", "100",
        "--manifest", $partialFailureManifest,
        "--output", "json")
    Assert-Equal 7 $partialFailureResult.ExitCode (
        "partial failure manifest extraction exit code")
    $partialFailure = Read-JsonEnvelope $partialFailureResult
    Assert-Equal "partial_success" $partialFailure.status (
        "partial failure manifest extraction status")
    Assert-Equal 1 $partialFailure.data.written (
        "partial failure manifest written count")
    Assert-Equal 1 $partialFailure.data.failed (
        "partial failure manifest failed count")
    Assert-Equal 1 $partialFailure.data.notAttempted (
        "partial failure manifest not-attempted count")
    Assert-True (
        $partialFailure.data.observedBytes -gt
            $partialFailure.data.bytesWritten) (
        "partial failure observed bytes include the rolled-back attempt")
    Assert-Equal "entry_size_limit_exceeded" (
        $partialFailure.data.failures[0].code) (
        "partial failure reports original error code")
    $partialFailureRecords = @(
        Get-Content -LiteralPath $partialFailureManifest |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    $partialFailureEntries = @(
        $partialFailureRecords | Where-Object record -eq "entry"
    )
    Assert-Equal 3 $partialFailureEntries.Count (
        "partial failure manifest audits every logical entry")
    Assert-Equal "written" $partialFailureEntries[0].status (
        "partial failure manifest successful entry status")
    Assert-Equal "failed" $partialFailureEntries[1].status (
        "partial failure manifest failed entry status")
    Assert-True (-not $partialFailureEntries[1].outputSizeKnown) (
        "partial failure manifest failed output size is unknown")
    Assert-True ($null -eq $partialFailureEntries[1].actualBytes) (
        "partial failure manifest has no failed actual byte count")
    Assert-Equal "entry_size_limit_exceeded" (
        $partialFailureEntries[1].error.code) (
        "partial failure manifest preserves error code")
    Assert-True (-not [string]::IsNullOrWhiteSpace(
        $partialFailureEntries[1].error.message)) (
        "partial failure manifest preserves error message")
    Assert-Equal "not_attempted" $partialFailureEntries[2].status (
        "partial failure manifest remaining entry status")
    Assert-Equal "aborted_after_error" (
        $partialFailureEntries[2].error.code) (
        "partial failure manifest remaining entry reason")
    $partialFailureSummary = @(
        $partialFailureRecords | Where-Object record -eq "summary"
    )
    Assert-Equal 1 $partialFailureSummary.Count (
        "partial failure manifest terminal summary count")
    Assert-Equal "partial_success" $partialFailureSummary[0].status (
        "partial failure manifest terminal status")
    Assert-Equal 1 $partialFailureSummary[0].counts.written (
        "partial failure manifest summary written count")
    Assert-Equal 1 $partialFailureSummary[0].counts.failed (
        "partial failure manifest summary failed count")
    Assert-Equal 1 $partialFailureSummary[0].counts.notAttempted (
        "partial failure manifest summary not-attempted count")

    $resumePreserveDestination = Join-Path `
        $testRoot "resume-preserve-output"
    $resumePreserveManifest = Join-Path `
        $testRoot "resume-preserve.jsonl"
    $resumePreserveFreshResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialFailureZip,
        "--destination", $resumePreserveDestination,
        "--budget", "auto", "--manifest", $resumePreserveManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $resumePreserveFreshResult.ExitCode (
        "resume-state preservation fresh extraction exit code")
    $resumePreserveFresh = Read-JsonEnvelope $resumePreserveFreshResult
    Assert-Equal 3 $resumePreserveFresh.data.written (
        "resume-state preservation fresh written count")
    $resumePreserveLarge = Join-Path `
        $resumePreserveDestination "large.txt"
    $resumePreserveLength = (
        Get-Item -LiteralPath $resumePreserveLarge).Length
    [IO.File]::WriteAllBytes(
        $resumePreserveLarge, [byte[]]::new([int]$resumePreserveLength))
    $resumePreserveAfter = Join-Path `
        $resumePreserveDestination "after.txt"
    $resumePreserveAfterLength = (
        Get-Item -LiteralPath $resumePreserveAfter).Length
    [IO.File]::WriteAllBytes(
        $resumePreserveAfter,
        [byte[]]::new([int]$resumePreserveAfterLength))

    $resumePreservePartialResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialFailureZip,
        "--destination", $resumePreserveDestination,
        "--resume", "verify-hash", "--manifest", $resumePreserveManifest,
        "--overwrite", "replace", "--max-files", "3",
        "--max-total-bytes", "20000", "--max-entry-bytes", "100",
        "--max-depth", "4", "--summary-only", "--output", "json")
    Assert-Equal 7 $resumePreservePartialResult.ExitCode (
        "resume-state preservation partial exit code")
    $resumePreservePartial = Read-JsonEnvelope $resumePreservePartialResult
    Assert-Equal 1 $resumePreservePartial.data.verifiedExisting (
        "resume-state preservation verifies the earlier entry")
    Assert-Equal 1 $resumePreservePartial.data.failed (
        "resume-state preservation records the repair failure")
    Assert-Equal 1 $resumePreservePartial.data.notAttempted (
        "resume-state preservation audits the later entry")
    [IO.File]::WriteAllText(
        $resumePreserveAfter, "after", [Text.Encoding]::ASCII)

    $resumePreserveFinalResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialFailureZip,
        "--destination", $resumePreserveDestination,
        "--resume", "verify-hash", "--manifest", $resumePreserveManifest,
        "--overwrite", "replace", "--budget", "auto",
        "--summary-only", "--output", "json")
    Assert-Equal 0 $resumePreserveFinalResult.ExitCode (
        "resume-state preservation final repair exit code")
    $resumePreserveFinal = Read-JsonEnvelope $resumePreserveFinalResult
    Assert-Equal 1 $resumePreserveFinal.data.written (
        "resume-state preservation repairs only the failed entry")
    Assert-Equal 2 $resumePreserveFinal.data.verifiedExisting (
        "resume-state preservation keeps later materialized state")
    Assert-Equal 0 $resumePreserveFinal.data.notAttempted (
        "resume-state preservation final not-attempted count")

    $resumeFirstFailureDestination = Join-Path `
        $testRoot "resume-first-failure-output"
    $resumeFirstFailureManifest = Join-Path `
        $testRoot "resume-first-failure.jsonl"
    $resumeFirstFreshResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialFailureZip,
        "--destination", $resumeFirstFailureDestination,
        "--entry-index", "1", "--entry-index", "2",
        "--budget", "auto", "--manifest", $resumeFirstFailureManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $resumeFirstFreshResult.ExitCode (
        "first-repair-failure fresh extraction exit code")
    [void](Read-JsonEnvelope $resumeFirstFreshResult)
    $resumeFirstLarge = Join-Path `
        $resumeFirstFailureDestination "large.txt"
    $resumeFirstLargeLength = (Get-Item -LiteralPath $resumeFirstLarge).Length
    [IO.File]::WriteAllBytes(
        $resumeFirstLarge,
        [byte[]]::new([int]$resumeFirstLargeLength))
    $resumeFirstFailureResult = Invoke-Cli -Arguments @(
        "archive", "extract", $partialFailureZip,
        "--destination", $resumeFirstFailureDestination,
        "--entry-index", "1", "--entry-index", "2",
        "--resume", "verify-hash", "--manifest", $resumeFirstFailureManifest,
        "--overwrite", "replace", "--max-files", "2",
        "--max-total-bytes", "20000", "--max-entry-bytes", "100",
        "--max-depth", "4", "--output", "jsonl")
    Assert-Equal 7 $resumeFirstFailureResult.ExitCode (
        "first-repair-failure partial exit code")
    $resumeFirstFailureEvents = Read-JsonLines $resumeFirstFailureResult
    $resumeFirstFiles = @(
        $resumeFirstFailureEvents | Where-Object event -eq "file")
    Assert-Equal 2 $resumeFirstFiles.Count (
        "first-repair-failure logical file event count")
    Assert-Equal "failed" $resumeFirstFiles[0].data.status (
        "first-repair-failure failed event")
    Assert-Equal "verified_existing" $resumeFirstFiles[1].data.status (
        "first-repair-failure later verified event")
    Assert-Equal 1 $resumeFirstFailureEvents[-1].data.failed (
        "first-repair-failure terminal failed count")
    Assert-Equal 1 $resumeFirstFailureEvents[-1].data.verifiedExisting (
        "first-repair-failure terminal verified count")
    Assert-Equal 0 $resumeFirstFailureEvents[-1].data.notAttempted (
        "first-repair-failure terminal not-attempted count")
    Assert-Equal 2 (
        $resumeFirstFailureEvents[-1].data.failed +
            $resumeFirstFailureEvents[-1].data.verifiedExisting) (
        "first-repair-failure terminal logical coverage")

    $cumulativeBudgetZip = Join-Path $testRoot "cumulative-budget.zip"
    New-CumulativeBudgetZip -Path $cumulativeBudgetZip
    $cumulativeBudgetDestination = Join-Path $testRoot "cumulative-budget-out"
    $cumulativeBudgetManifest = Join-Path $testRoot "cumulative-budget.jsonl"
    $cumulativeBudgetResult = Invoke-Cli -Arguments @(
        "archive", "extract", $cumulativeBudgetZip,
        "--destination", $cumulativeBudgetDestination,
        "--max-entry-bytes", "100000",
        "--max-total-bytes", "70000",
        "--manifest", $cumulativeBudgetManifest,
        "--output", "json")
    Assert-Equal 7 $cumulativeBudgetResult.ExitCode (
        "cumulative failed-write budget exit code")
    $cumulativeBudget = Read-JsonEnvelope $cumulativeBudgetResult
    Assert-Equal "partial_success" $cumulativeBudget.status (
        "cumulative failed-write budget status")
    Assert-Equal 1 $cumulativeBudget.data.written (
        "cumulative failed-write committed count")
    Assert-Equal 2 $cumulativeBudget.data.failed (
        "cumulative failed-write failure count")
    Assert-Equal 1 $cumulativeBudget.data.notAttempted (
        "cumulative failed-write not-attempted count")
    Assert-Equal $cumulativeBudget.data.selected (
        $cumulativeBudget.data.written +
            $cumulativeBudget.data.verifiedExisting +
            $cumulativeBudget.data.skipped +
            $cumulativeBudget.data.failed +
            $cumulativeBudget.data.notAttempted) (
        "cumulative failed-write logical entry coverage")
    Assert-Equal 5 $cumulativeBudget.data.bytesWritten (
        "cumulative failed-write committed bytes")
    Assert-True ($cumulativeBudget.data.observedBytes -gt 70000) (
        "cumulative failed-write observed bytes exceed the total limit")
    Assert-Equal "failing.txt" $cumulativeBudget.data.failures[0].entry (
        "cumulative failed-write late failure entry")
    Assert-True (
        $cumulativeBudget.data.failures[0].code -ne
            "total_size_limit_exceeded") (
        "cumulative failed-write late failure occurs before total exhaustion")
    Assert-Equal "later.txt" $cumulativeBudget.data.failures[1].entry (
        "cumulative failed-write subsequent entry")
    Assert-Equal "total_size_limit_exceeded" (
        $cumulativeBudget.data.failures[1].code) (
        "cumulative failed-write charge blocks the subsequent entry")
    Assert-True (Test-Path -LiteralPath (
        Join-Path $cumulativeBudgetDestination "first.txt") -PathType Leaf) (
        "cumulative failed-write preserves committed output")
    Assert-True (-not (Test-Path -LiteralPath (
        Join-Path $cumulativeBudgetDestination "failing.txt"))) (
        "cumulative failed-write does not commit the late failure")
    Assert-True (-not (Test-Path -LiteralPath (
        Join-Path $cumulativeBudgetDestination "later.txt"))) (
        "cumulative failed-write does not commit the budget failure")
    Assert-True (-not (Test-Path -LiteralPath (
        Join-Path $cumulativeBudgetDestination "untouched.txt"))) (
        "cumulative failed-write leaves later entries unattempted")
    $cumulativeBudgetRecords = @(
        Get-Content -LiteralPath $cumulativeBudgetManifest |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    $cumulativeBudgetEntries = @(
        $cumulativeBudgetRecords | Where-Object record -eq "entry"
    )
    Assert-Equal 4 $cumulativeBudgetEntries.Count (
        "cumulative failed-write manifest logical entry count")
    Assert-Equal "written,failed,failed,not_attempted" (
        $cumulativeBudgetEntries.status -join ",") (
        "cumulative failed-write manifest statuses")
    Assert-Equal "total_size_limit_exceeded" (
        $cumulativeBudgetEntries[2].error.code) (
        "cumulative failed-write manifest budget error")
    $cumulativeBudgetSummary = @(
        $cumulativeBudgetRecords | Where-Object record -eq "summary"
    )
    Assert-Equal 1 $cumulativeBudgetSummary.Count (
        "cumulative failed-write manifest summary count")
    Assert-Equal 2 $cumulativeBudgetSummary[0].counts.failed (
        "cumulative failed-write manifest failed count")
    Assert-Equal 1 $cumulativeBudgetSummary[0].counts.notAttempted (
        "cumulative failed-write manifest not-attempted count")

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

    $duplicateZip = Join-Path $testRoot "duplicate-input.zip"
    New-DuplicateZip -Path $duplicateZip
    $duplicateSourceHash = (
        Get-FileHash -LiteralPath $duplicateZip -Algorithm SHA256).Hash

    $duplicateListResult = Invoke-Cli -Arguments @(
        "archive", "list", $duplicateZip, "--output", "json")
    Assert-Equal 0 $duplicateListResult.ExitCode "duplicate archive list exit code"
    $duplicateList = Read-JsonEnvelope $duplicateListResult
    Assert-Equal 6 $duplicateList.data.entryCount "duplicate archive entry count"
    Assert-Equal "0,1,2,3,4,5" (
        @($duplicateList.data.entries.entryIndex) -join ",") (
        "archive-wide zero-based entry indexes")
    Assert-True (-not $duplicateList.data.entries[0].outputSizeKnown) (
        "archive list does not promise materialized size")
    Assert-True $duplicateList.data.entries[0].materializedSizeMayDiffer (
        "archive list exposes materialized-size uncertainty")

    $duplicateListSummaryResult = Invoke-Cli -Arguments @(
        "archive", "list", $duplicateZip,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateListSummaryResult.ExitCode (
        "duplicate archive summary-only list exit code")
    $duplicateListSummary = Read-JsonEnvelope $duplicateListSummaryResult
    Assert-Equal 6 $duplicateListSummary.data.entryCount (
        "duplicate archive summary-only entry count")
    Assert-True $duplicateListSummary.data.summaryOnly (
        "duplicate archive summary-only marker")
    Assert-True ($null -eq $duplicateListSummary.data.entries) (
        "duplicate archive summary-only omits entries")

    $duplicatePlanDestination = Join-Path $testRoot "duplicate-plan"
    $duplicateErrorPlanResult = Invoke-Cli -Arguments @(
        "archive", "plan", $duplicateZip,
        "--destination", $duplicatePlanDestination,
        "--output", "json")
    Assert-Equal 0 $duplicateErrorPlanResult.ExitCode (
        "duplicate archive default plan exit code")
    $duplicateErrorPlan = Read-JsonEnvelope $duplicateErrorPlanResult
    Assert-Equal "error" $duplicateErrorPlan.data.duplicatePolicy (
        "duplicate archive default policy")
    Assert-Equal 1 $duplicateErrorPlan.data.destinationCollisionGroupCount (
        "duplicate archive collision group count")
    Assert-True (-not $duplicateErrorPlan.data.ready) (
        "duplicate archive error plan is not ready")

    $duplicatePlanResult = Invoke-Cli -Arguments @(
        "archive", "plan", $duplicateZip,
        "--destination", $duplicatePlanDestination,
        "--duplicate-policy", "suffix-index",
        "--output", "json")
    Assert-Equal 0 $duplicatePlanResult.ExitCode (
        "duplicate archive suffix plan exit code")
    $duplicatePlan = Read-JsonEnvelope $duplicatePlanResult
    Assert-Equal 6 $duplicatePlan.data.selected "duplicate plan selected count"
    Assert-Equal 3 $duplicatePlan.data.uniqueNormalizedPathCount (
        "duplicate plan unique path count")
    Assert-Equal 1 $duplicatePlan.data.duplicateGroupCount (
        "duplicate plan duplicate group count")
    Assert-Equal 4 $duplicatePlan.data.duplicateEntryCount (
        "duplicate plan duplicate entry count")
    Assert-Equal 3 $duplicatePlan.data.extraOccurrenceCount (
        "duplicate plan extra occurrence count")
    Assert-True $duplicatePlan.data.ready "duplicate suffix plan is ready"
    Assert-Equal 6 $duplicatePlan.data.recommendedLimits.maxFiles (
        "duplicate plan finite max-files recommendation")
    Assert-True ($duplicatePlan.data.recommendedLimits.maxTotalBytes -gt 0) (
        "duplicate plan finite total-byte recommendation")
    Assert-True (-not [string]::IsNullOrWhiteSpace(
        $duplicatePlan.data.planFingerprint)) "duplicate plan fingerprint"
    Assert-Equal 6 @(
        $duplicatePlan.data.entries.outputRelativePath |
            Sort-Object -Unique
    ).Count "duplicate plan output paths are unique"

    $duplicateIndexZero = @(
        $duplicatePlan.data.entries | Where-Object entryIndex -eq 0
    )[0]
    $duplicateIndexOne = @(
        $duplicatePlan.data.entries | Where-Object entryIndex -eq 1
    )[0]
    $duplicateIndexTwo = @(
        $duplicatePlan.data.entries | Where-Object entryIndex -eq 2
    )[0]
    $duplicateIndexThree = @(
        $duplicatePlan.data.entries | Where-Object entryIndex -eq 3
    )[0]
    $duplicateNaturalSuffix = @(
        $duplicatePlan.data.entries | Where-Object entryIndex -eq 4
    )[0]
    Assert-Equal "foo.ogg" (Split-Path -Leaf $duplicateIndexZero.path) (
        "first duplicate keeps its original path")
    Assert-Equal "foo.__entry-000001-01.ogg" (
        Split-Path -Leaf $duplicateIndexOne.path) (
        "duplicate suffix disambiguates a natural suffix collision")
    Assert-Equal "foo.__entry-000002.ogg" (
        Split-Path -Leaf $duplicateIndexTwo.path) (
        "duplicate suffix includes stable entry index")
    Assert-Equal "FOO.__entry-000003.ogg" (
        Split-Path -Leaf $duplicateIndexThree.path) (
        "case-only duplicate keeps its stem and stable index")
    Assert-Equal "foo.__entry-000001.ogg" (
        Split-Path -Leaf $duplicateNaturalSuffix.path) (
        "natural suffix entry keeps its original path")
    Assert-Equal 2 $duplicateIndexOne.occurrence (
        "duplicate occurrence is archive-wide")
    Assert-Equal 4 $duplicateIndexOne.groupSize (
        "duplicate group size is archive-wide")

    $duplicateSummaryPlanResult = Invoke-Cli -Arguments @(
        "archive", "plan", $duplicateZip,
        "--destination", $duplicatePlanDestination,
        "--duplicate-policy", "suffix-index", "--summary-only",
        "--output", "json")
    Assert-Equal 0 $duplicateSummaryPlanResult.ExitCode (
        "duplicate summary-only plan exit code")
    $duplicateSummaryPlan = Read-JsonEnvelope $duplicateSummaryPlanResult
    Assert-True $duplicateSummaryPlan.data.summaryOnly (
        "duplicate summary-only plan marker")
    Assert-True ($null -eq $duplicateSummaryPlan.data.entries) (
        "duplicate summary-only plan omits entries")
    Assert-Equal $duplicatePlan.data.planFingerprint (
        $duplicateSummaryPlan.data.planFingerprint) (
        "duplicate summary-only plan uses the same fingerprint")

    $duplicateIndexPlanResult = Invoke-Cli -Arguments @(
        "archive", "plan", $duplicateZip,
        "--destination", $duplicatePlanDestination,
        "--entry", "voice/FOO.ogg",
        "--entry-index", "1",
        "--duplicate-policy", "suffix-index",
        "--output", "json")
    Assert-Equal 0 $duplicateIndexPlanResult.ExitCode (
        "entry-index and glob intersection exit code")
    $duplicateIndexPlan = Read-JsonEnvelope $duplicateIndexPlanResult
    Assert-Equal 1 $duplicateIndexPlan.data.selected (
        "entry-index and glob intersection selected count")
    Assert-Equal 1 $duplicateIndexPlan.data.entries[0].entryIndex (
        "entry-index selection retains archive ordinal")
    Assert-Equal "foo.__entry-000001-01.ogg" (
        Split-Path -Leaf $duplicateIndexPlan.data.entries[0].path) (
        "filtered duplicate keeps archive-wide suffix mapping")

    $duplicateEmptyIntersectionResult = Invoke-Cli -Arguments @(
        "archive", "plan", $duplicateZip,
        "--destination", $duplicatePlanDestination,
        "--entry", "voice/*", "--entry-index", "5",
        "--duplicate-policy", "suffix-index",
        "--output", "json")
    Assert-Equal 3 $duplicateEmptyIntersectionResult.ExitCode (
        "empty entry-index and glob intersection exit code")
    $duplicateEmptyIntersection = Read-JsonEnvelope (
        $duplicateEmptyIntersectionResult)
    Assert-Equal "no_entries_selected" $duplicateEmptyIntersection.error.code (
        "empty entry-index and glob intersection error")

    $duplicateOutOfRangeResult = Invoke-Cli -Arguments @(
        "archive", "plan", $duplicateZip,
        "--destination", $duplicatePlanDestination,
        "--entry-index", "6", "--output", "json")
    Assert-Equal 3 $duplicateOutOfRangeResult.ExitCode (
        "out-of-range entry-index exit code")
    $duplicateOutOfRange = Read-JsonEnvelope $duplicateOutOfRangeResult
    Assert-Equal "entry_index_out_of_range" $duplicateOutOfRange.error.code (
        "out-of-range entry-index error")
    Assert-Equal 5 $duplicateOutOfRange.error.details.maximum (
        "out-of-range entry-index maximum detail")
    Assert-Equal 6 $duplicateOutOfRange.error.details.entryCount (
        "out-of-range entry-index archive count detail")

    $duplicateErrorDestination = Join-Path $testRoot "duplicate-error"
    $duplicateErrorExtractResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateErrorDestination,
        "--output", "json")
    Assert-Equal 3 $duplicateErrorExtractResult.ExitCode (
        "duplicate extraction default policy exit code")
    $duplicateErrorExtract = Read-JsonEnvelope $duplicateErrorExtractResult
    Assert-Equal "unsafe_output_path" $duplicateErrorExtract.error.code (
        "duplicate extraction default policy error")
    Assert-Equal "duplicate_destination" (
        $duplicateErrorExtract.error.details.reason) (
        "duplicate extraction default policy reason")
    Assert-True (-not (Test-Path -LiteralPath $duplicateErrorDestination)) (
        "duplicate error policy writes no files")

    $duplicateDryDestination = Join-Path $testRoot "duplicate-dry"
    $duplicateDryResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateDryDestination,
        "--duplicate-policy", "suffix-index",
        "--budget", "auto", "--dry-run", "--summary-only",
        "--output", "json")
    Assert-Equal 0 $duplicateDryResult.ExitCode (
        "duplicate auto-budget dry-run exit code")
    $duplicateDry = Read-JsonEnvelope $duplicateDryResult
    Assert-Equal 6 $duplicateDry.data.planned (
        "duplicate auto-budget dry-run planned count")
    Assert-Equal "archivePlan" $duplicateDry.data.policy.budgetSource (
        "duplicate auto-budget source")
    Assert-Equal 6 $duplicateDry.data.policy.maxFiles (
        "duplicate auto-budget max-files")
    Assert-Equal $duplicatePlan.data.recommendedLimits.maxTotalBytes (
        $duplicateDry.data.policy.maxTotalBytes) (
        "duplicate auto-budget total bytes match plan")
    Assert-Equal $duplicatePlan.data.recommendedLimits.maxEntryBytes (
        $duplicateDry.data.policy.maxEntryBytes) (
        "duplicate auto-budget entry bytes match plan")
    Assert-True $duplicateDry.data.summaryOnly (
        "duplicate dry-run summary-only marker")
    Assert-True ($null -eq $duplicateDry.data.files) (
        "duplicate dry-run summary-only omits files")
    Assert-True (-not (Test-Path -LiteralPath $duplicateDryDestination)) (
        "duplicate dry-run creates no destination")

    $basicXp3 = Join-Path $testRoot "basic-scheme.xp3"
    $basicXp3Png = [Convert]::FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    $basicXp3Records = [Collections.Generic.List[object]]::new()
    $basicXp3Records.Add([pscustomobject]@{
        Name = "images/pixel.png"
        Content = $basicXp3Png
    })
    for ($basicIndex = 1; $basicIndex -lt 10; $basicIndex++) {
        $basicXp3Records.Add([pscustomobject]@{
            Name = ("text/item-{0:D2}.txt" -f $basicIndex)
            Content = ("basic-xp3-content-{0}" -f $basicIndex)
        })
    }
    New-Xp3Fixture -Path $basicXp3 -Records $basicXp3Records.ToArray()

    $basicXp3NeedsInputResult = Invoke-Cli -Arguments @(
        "archive", "list", $basicXp3,
        "--non-interactive", "--output", "json")
    Assert-Equal 5 $basicXp3NeedsInputResult.ExitCode (
        "synthetic XP3 requires an explicit scheme")
    $basicXp3NeedsInput = Read-JsonEnvelope $basicXp3NeedsInputResult
    Assert-Equal "needs_input" $basicXp3NeedsInput.status (
        "synthetic XP3 missing scheme status")

    $basicXp3ProbeResult = Invoke-Cli -Arguments @(
        "probe", $basicXp3, "--scheme", "__NOCRYPT__", "--output", "json")
    Assert-Equal 0 $basicXp3ProbeResult.ExitCode (
        "synthetic XP3 explicit-scheme probe exit code")
    $basicXp3Probe = Read-JsonEnvelope $basicXp3ProbeResult
    Assert-Equal "XP3" $basicXp3Probe.data.tag (
        "synthetic XP3 explicit-scheme probe tag")

    $basicXp3ListResult = Invoke-Cli -Arguments @(
        "archive", "list", $basicXp3,
        "--scheme", "__NOCRYPT__", "--output", "json")
    Assert-Equal 0 $basicXp3ListResult.ExitCode (
        "synthetic XP3 explicit-scheme list exit code")
    $basicXp3List = Read-JsonEnvelope $basicXp3ListResult
    Assert-Equal 10 $basicXp3List.data.entryCount (
        "synthetic XP3 entry count")
    Assert-Equal "xp3:scheme:__NOCRYPT__" (
        $basicXp3List.data.schemeResolution.identity) (
        "synthetic XP3 scheme identity")

    $basicXp3CheckResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $basicXp3,
        "--scheme", "__NOCRYPT__", "--output", "json")
    Assert-Equal 0 $basicXp3CheckResult.ExitCode (
        "synthetic XP3 correct scheme-check exit code")
    $basicXp3Check = Read-JsonEnvelope $basicXp3CheckResult
    Assert-True ($basicXp3Check.data.contentValidation.matchedEntries -ge 1) (
        "synthetic XP3 correct scheme validates recognized content")

    $basicXp3WrongResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $basicXp3,
        "--scheme", "__XOR-FF__", "--output", "json")
    Assert-Equal 3 $basicXp3WrongResult.ExitCode (
        "synthetic XP3 wrong scheme-check exit code")
    $basicXp3Wrong = Read-JsonEnvelope $basicXp3WrongResult
    Assert-Equal "xp3_scheme_check_failed" $basicXp3Wrong.error.code (
        "synthetic XP3 wrong scheme stable error")

    $basicXp3Destination = Join-Path $testRoot "basic-scheme-output"
    $basicXp3Manifest = Join-Path $testRoot "basic-scheme-manifest.jsonl"
    $basicXp3ExtractResult = Invoke-Cli -Arguments @(
        "archive", "extract", $basicXp3,
        "--destination", $basicXp3Destination,
        "--scheme", "__NOCRYPT__", "--budget", "auto",
        "--manifest", $basicXp3Manifest, "--checksum", "sha256",
        "--summary-only", "--output", "json")
    Assert-Equal 0 $basicXp3ExtractResult.ExitCode (
        "synthetic XP3 explicit-scheme extract exit code")
    $basicXp3Extract = Read-JsonEnvelope $basicXp3ExtractResult
    Assert-Equal 10 $basicXp3Extract.data.written (
        "synthetic XP3 explicit-scheme written count")
    Assert-Equal 10 @(
        Get-ChildItem -LiteralPath $basicXp3Destination -Recurse -File
    ).Count "synthetic XP3 output coverage"
    Assert-Equal 10 @(
        Get-Content -LiteralPath $basicXp3Manifest |
            ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object record -eq "entry"
    ).Count "synthetic XP3 manifest logical-entry coverage"
    Assert-Equal (
        [Convert]::ToBase64String($basicXp3Png)) (
        [Convert]::ToBase64String([IO.File]::ReadAllBytes(
            (Join-Path $basicXp3Destination "images\pixel.png")))) (
        "synthetic XP3 extracted payload is exact")

    $basicXp3AliasResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $basicXp3,
        "--destination", $basicXp3Destination,
        "--scheme", "__nocrypt__", "--budget", "auto",
        "--resume", "verify-hash", "--resume-manifest", $basicXp3Manifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $basicXp3AliasResumeResult.ExitCode (
        "canonical alias case resume exit code")
    $basicXp3AliasResume = Read-JsonEnvelope $basicXp3AliasResumeResult
    Assert-Equal 10 $basicXp3AliasResume.data.verifiedExisting (
        "canonical alias case resume verifies every output")

    $lazyTpmScheme = Get-LazyTpmFixtureScheme
    $lazyTpmRoot = Join-Path $testRoot "lazy-tpm"
    New-Item -ItemType Directory -Path $lazyTpmRoot | Out-Null
    $lazyTpmArchive = Join-Path `
        $lazyTpmRoot $lazyTpmScheme.ArchiveFileName
    New-Xp3Fixture -Path $lazyTpmArchive -Records @(
        [pscustomobject]@{
            Name = "empty.bin"
            Content = [Convert]::FromBase64String("AA==")
        }
    )
    $lazyTpmPath = Join-Path $lazyTpmRoot $lazyTpmScheme.TpmFileName
    Write-TpmFixture -Path $lazyTpmPath -Mutation 0
    $lazyTpmDestination = Join-Path $lazyTpmRoot "output"
    $lazyTpmPlanDestination = Join-Path $lazyTpmRoot "planned"
    $lazyTpmPlanBeforeResult = Invoke-Cli -Arguments @(
        "archive", "plan", $lazyTpmArchive,
        "--destination", $lazyTpmPlanDestination,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $lazyTpmPlanBeforeResult.ExitCode (
        "auto-detected lazy TPM plan exit code")
    $lazyTpmPlanBefore = Read-JsonEnvelope $lazyTpmPlanBeforeResult
    Assert-True (
        $lazyTpmPlanBefore.data.schemeResolution.sourceChain -contains
            "auto_detected") (
        "auto-detected XP3 plan reports its scheme source")
    Assert-Equal $lazyTpmScheme.SchemeName (
        $lazyTpmPlanBefore.data.schemeResolution.scheme.name) (
        "auto-detected XP3 plan reports the canonical scheme")
    $lazyTpmArtifactBefore = @(
        $lazyTpmPlanBefore.data.schemeResolution.artifacts |
            Where-Object kind -eq "xp3_tpm_control_block"
    )
    Assert-Equal 1 $lazyTpmArtifactBefore.Count (
        "lazy TPM plan reports the consumed control-block snapshot")
    Assert-Equal 64 $lazyTpmArtifactBefore[0].sha256.Length (
        "lazy TPM artifact uses SHA-256")

    $lazyTpmAutoDestination = Join-Path $lazyTpmRoot "auto-output"
    $lazyTpmAutoManifest = Join-Path `
        $lazyTpmRoot "auto-extract.manifest.jsonl"
    $lazyTpmAutoFreshResult = Invoke-Cli -Arguments @(
        "archive", "extract", $lazyTpmArchive,
        "--destination", $lazyTpmAutoDestination,
        "--budget", "auto", "--manifest", $lazyTpmAutoManifest,
        "--checksum", "sha256", "--summary-only", "--output", "json")
    Assert-Equal 0 $lazyTpmAutoFreshResult.ExitCode (
        "auto-detected lazy TPM manifested extraction exit code")
    $lazyTpmAutoFresh = Read-JsonEnvelope $lazyTpmAutoFreshResult
    Assert-Equal 1 $lazyTpmAutoFresh.data.written (
        "auto-detected lazy TPM manifested extraction written count")
    Assert-True (
        $lazyTpmAutoFresh.data.schemeResolution.sourceChain -contains
            "auto_detected") (
        "auto-detected manifested extraction binds the effective scheme")
    $lazyTpmAutoResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $lazyTpmArchive,
        "--destination", $lazyTpmAutoDestination,
        "--budget", "auto", "--resume", "verify-hash",
        "--resume-manifest", $lazyTpmAutoManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $lazyTpmAutoResumeResult.ExitCode (
        "unchanged auto-detected lazy TPM resume exit code")
    $lazyTpmAutoResume = Read-JsonEnvelope $lazyTpmAutoResumeResult
    Assert-Equal 1 $lazyTpmAutoResume.data.verifiedExisting (
        "unchanged auto-detected lazy TPM resume verifies the output")
    $lazyTpmAutoOutput = Join-Path $lazyTpmAutoDestination "empty.bin"
    $lazyTpmAutoOutputHash = (
        Get-FileHash -LiteralPath $lazyTpmAutoOutput -Algorithm SHA256).Hash
    $lazyTpmAutoManifestHash = (
        Get-FileHash -LiteralPath $lazyTpmAutoManifest -Algorithm SHA256).Hash

    $lazyTpmManifest = Join-Path $lazyTpmRoot "extract.manifest.jsonl"
    $lazyTpmFreshResult = Invoke-Cli -Arguments @(
        "archive", "extract", $lazyTpmArchive,
        "--destination", $lazyTpmDestination,
        "--scheme", $lazyTpmScheme.SchemeName,
        "--budget", "auto", "--manifest", $lazyTpmManifest,
        "--checksum", "sha256", "--summary-only", "--output", "json")
    Assert-Equal 0 $lazyTpmFreshResult.ExitCode (
        "lazy TPM manifested extraction exit code")
    $lazyTpmFresh = Read-JsonEnvelope $lazyTpmFreshResult
    Assert-Equal 1 $lazyTpmFresh.data.written (
        "lazy TPM manifested extraction writes the empty fixture")
    $lazyTpmOutput = Join-Path $lazyTpmDestination "empty.bin"
    Assert-Equal 1 (Get-Item -LiteralPath $lazyTpmOutput).Length (
        "lazy TPM manifested extraction output length")
    $lazyTpmOutputHash = (
        Get-FileHash -LiteralPath $lazyTpmOutput -Algorithm SHA256).Hash
    $lazyTpmManifestHash = (
        Get-FileHash -LiteralPath $lazyTpmManifest -Algorithm SHA256).Hash

    Write-TpmFixture -Path $lazyTpmPath -Mutation 1
    $lazyTpmAutoChangedResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $lazyTpmArchive,
        "--destination", $lazyTpmAutoDestination,
        "--budget", "auto", "--resume", "verify-hash",
        "--resume-manifest", $lazyTpmAutoManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $lazyTpmAutoChangedResumeResult.ExitCode (
        "changed auto-detected lazy TPM resume exit code")
    $lazyTpmAutoChangedResume = Read-JsonEnvelope (
        $lazyTpmAutoChangedResumeResult)
    Assert-Equal "manifest_handler_mismatch" (
        $lazyTpmAutoChangedResume.error.code) (
        "changed auto-detected TPM resume rejects mixed scheme material")
    Assert-Equal $lazyTpmAutoOutputHash (
        Get-FileHash -LiteralPath $lazyTpmAutoOutput -Algorithm SHA256).Hash (
        "changed auto-detected TPM resume preserves the prior output")
    Assert-Equal $lazyTpmAutoManifestHash (
        Get-FileHash -LiteralPath $lazyTpmAutoManifest -Algorithm SHA256).Hash (
        "changed auto-detected TPM resume preserves the manifest")

    $lazyTpmResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $lazyTpmArchive,
        "--destination", $lazyTpmDestination,
        "--scheme", $lazyTpmScheme.SchemeName,
        "--budget", "auto", "--resume", "verify-hash",
        "--resume-manifest", $lazyTpmManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $lazyTpmResumeResult.ExitCode (
        "changed lazy TPM resume exit code")
    $lazyTpmResume = Read-JsonEnvelope $lazyTpmResumeResult
    Assert-Equal "manifest_handler_mismatch" $lazyTpmResume.error.code (
        "changed lazy TPM resume is rejected by scheme fingerprint")
    Assert-Equal $lazyTpmOutputHash (
        Get-FileHash -LiteralPath $lazyTpmOutput -Algorithm SHA256).Hash (
        "changed lazy TPM resume preserves the prior output")
    Assert-Equal $lazyTpmManifestHash (
        Get-FileHash -LiteralPath $lazyTpmManifest -Algorithm SHA256).Hash (
        "changed lazy TPM resume preserves the manifest")

    $lazyTpmPlanAfterResult = Invoke-Cli -Arguments @(
        "archive", "plan", $lazyTpmArchive,
        "--destination", $lazyTpmPlanDestination,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $lazyTpmPlanAfterResult.ExitCode (
        "changed auto-detected lazy TPM plan exit code")
    $lazyTpmPlanAfter = Read-JsonEnvelope $lazyTpmPlanAfterResult
    Assert-True (
        $lazyTpmPlanBefore.data.schemeResolution.fingerprint -ne
            $lazyTpmPlanAfter.data.schemeResolution.fingerprint) (
        "lazy TPM scheme fingerprint binds the post-open control block")
    Assert-True (
        $lazyTpmPlanBefore.data.planFingerprint -ne
            $lazyTpmPlanAfter.data.planFingerprint) (
        "auto-detected plan fingerprint binds the lazy TPM control block")

    $duplicateXp3 = Join-Path $testRoot "duplicate-paths.xp3"
    New-Xp3Fixture -Path $duplicateXp3 -Records @(
        [pscustomobject]@{ Name = "dup/item.bin"; Content = "same" },
        [pscustomobject]@{ Name = "dup/item.bin"; Content = "same" },
        [pscustomobject]@{ Name = "dup/item.bin"; Content = "different" },
        [pscustomobject]@{ Name = "dup/ITEM.bin"; Content = "case" }
    )
    $duplicateXp3DefaultResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateXp3,
        "--destination", (Join-Path $testRoot "duplicate-xp3-default"),
        "--scheme", "__NOCRYPT__", "--dry-run", "--output", "json")
    Assert-Equal 3 $duplicateXp3DefaultResult.ExitCode (
        "synthetic duplicate XP3 default collision exit code")
    $duplicateXp3Default = Read-JsonEnvelope $duplicateXp3DefaultResult
    Assert-Equal "unsafe_output_path" (
        $duplicateXp3Default.error.code) (
        "synthetic duplicate XP3 default collision code")
    Assert-Equal "duplicate_destination" (
        $duplicateXp3Default.error.details.reason) (
        "synthetic duplicate XP3 collision reason")
    $duplicateXp3Destination = Join-Path $testRoot "duplicate-xp3-output"
    $duplicateXp3Manifest = Join-Path $testRoot "duplicate-xp3-manifest.jsonl"
    $duplicateXp3ExtractResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateXp3,
        "--destination", $duplicateXp3Destination,
        "--scheme", "__NOCRYPT__", "--duplicate-policy", "suffix-index",
        "--budget", "auto", "--manifest", $duplicateXp3Manifest,
        "--checksum", "sha256", "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateXp3ExtractResult.ExitCode (
        "synthetic duplicate XP3 suffix extraction exit code")
    $duplicateXp3Extract = Read-JsonEnvelope $duplicateXp3ExtractResult
    Assert-Equal 4 $duplicateXp3Extract.data.written (
        "synthetic duplicate XP3 logical-entry extraction coverage")
    Assert-Equal 4 @(
        Get-ChildItem -LiteralPath $duplicateXp3Destination -Recurse -File
    ).Count "synthetic duplicate XP3 physical output coverage"
    Assert-Equal 4 @(
        Get-Content -LiteralPath $duplicateXp3Manifest |
            ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object record -eq "entry"
    ).Count "synthetic duplicate XP3 manifest coverage"

    $largeZip = Join-Path $testRoot "large-json.xp3"
    New-LargeXp3 -Path $largeZip -Count 50001
    $largeListResult = Invoke-Cli -Arguments @(
        "archive", "list", $largeZip,
        "--scheme", "__NOCRYPT__", "--output", "json")
    Assert-Equal 0 $largeListResult.ExitCode "large archive list exit code"
    $largeList = Read-JsonEnvelope $largeListResult
    Assert-Equal 50001 $largeList.data.entries.Count (
        "large archive JSON entry count")
    Assert-Equal 1 @(
        $largeList.warnings | Where-Object code -eq "large_json_response"
    ).Count "large archive JSON warning"
    Assert-Equal 1000 @(
        $largeList.warnings | Where-Object code -eq "large_json_response"
    )[0].details.threshold "large archive JSON warning threshold"
    $largeSummaryResult = Invoke-Cli -Arguments @(
        "archive", "list", $largeZip,
        "--scheme", "__NOCRYPT__", "--summary-only", "--output", "json")
    Assert-Equal 0 $largeSummaryResult.ExitCode (
        "large archive summary-only exit code")
    $largeSummary = Read-JsonEnvelope $largeSummaryResult
    Assert-Equal 50001 $largeSummary.data.entryCount (
        "large archive summary-only entry count")
    Assert-True ($null -eq $largeSummary.data.entries) (
        "large archive summary-only omits entries")
    Assert-True ($null -eq $largeSummary.warnings) (
        "large archive summary-only needs no large-JSON warning")

    $largeJsonlResult = Invoke-CliJsonlToFile -Arguments @(
        "archive", "list", $largeZip,
        "--scheme", "__NOCRYPT__", "--output", "jsonl") `
        -Stem "large-xp3-list"
    Assert-Equal 0 $largeJsonlResult.ExitCode (
        "large archive JSONL list exit code")
    $largeJsonlScan = Read-JsonlFileSummary `
        -Path $largeJsonlResult.StdoutPath -ExpectedCommand "archive.list"
    Assert-Equal 50001 $largeJsonlScan.EntryCount (
        "large archive JSONL entry event count")
    $largeJsonlSummary = $largeJsonlScan.Terminal
    Assert-Equal "summary" $largeJsonlSummary.event (
        "large archive JSONL terminal event")
    Assert-Equal 50001 $largeJsonlSummary.data.entryCount (
        "large archive JSONL terminal entry count")
    Assert-True ($null -eq $largeJsonlSummary.data.entries) (
        "large archive JSONL terminal event does not aggregate entries")
    Assert-True ($largeJsonlResult.PeakWorkingSetBytes -gt 0) (
        "large archive JSONL records peak working set")
    Assert-True ($largeJsonlResult.PeakWorkingSetBytes -lt 536870912) (
        "large archive JSONL peak working set remains below 512 MiB")
    Assert-True ([string]::IsNullOrWhiteSpace(
        [IO.File]::ReadAllText($largeJsonlResult.StderrPath))) (
        "large archive JSONL writes no diagnostics")

    $largePlanDestination = Join-Path $testRoot "large-plan-output"
    $largePlanResult = Invoke-Cli -Arguments @(
        "archive", "plan", $largeZip,
        "--destination", $largePlanDestination,
        "--scheme", "__NOCRYPT__",
        "--summary-only", "--output", "json")
    Assert-Equal 0 $largePlanResult.ExitCode (
        "above-default archive plan exit code")
    $largePlan = Read-JsonEnvelope $largePlanResult
    Assert-Equal 50001 $largePlan.data.selected (
        "above-default archive plan selected count")
    Assert-True (-not $largePlan.data.fitsDefaultLimits) (
        "above-default archive plan reports default-limit mismatch")
    Assert-Equal 50001 $largePlan.data.recommendedLimits.maxFiles (
        "above-default archive plan max-files recommendation")
    Assert-True ($null -eq $largePlan.data.entries) (
        "above-default summary-only plan omits entries")

    $largeDefaultDryResult = Invoke-Cli -Arguments @(
        "archive", "extract", $largeZip,
        "--destination", $largePlanDestination,
        "--scheme", "__NOCRYPT__",
        "--dry-run", "--summary-only", "--output", "json")
    Assert-Equal 3 $largeDefaultDryResult.ExitCode (
        "above-default archive default budget exit code")
    $largeDefaultDry = Read-JsonEnvelope $largeDefaultDryResult
    Assert-Equal "file_count_limit_exceeded" $largeDefaultDry.error.code (
        "above-default archive default budget error")
    Assert-Equal 50001 $largeDefaultDry.error.details.selected (
        "above-default archive default budget selected detail")

    $largeAutoDryResult = Invoke-Cli -Arguments @(
        "archive", "extract", $largeZip,
        "--destination", $largePlanDestination,
        "--scheme", "__NOCRYPT__",
        "--budget", "auto", "--dry-run", "--summary-only",
        "--output", "json")
    Assert-Equal 0 $largeAutoDryResult.ExitCode (
        "above-default archive auto-budget exit code")
    $largeAutoDry = Read-JsonEnvelope $largeAutoDryResult
    Assert-Equal 50001 $largeAutoDry.data.planned (
        "above-default archive auto-budget planned count")
    Assert-Equal 50001 $largeAutoDry.data.policy.maxFiles (
        "above-default archive auto-budget max-files")
    Assert-True (-not (Test-Path -LiteralPath $largePlanDestination)) (
        "above-default archive auto-budget dry-run creates no destination")

    $nonXp3SchemeResult = Invoke-Cli -Arguments @(
        "archive", "list", $duplicateZip,
        "--scheme", "__NOCRYPT__", "--output", "json")
    Assert-Equal 3 $nonXp3SchemeResult.ExitCode (
        "explicit scheme on non-XP3 exit code")
    $nonXp3Scheme = Read-JsonEnvelope $nonXp3SchemeResult
    Assert-Equal "xp3_scheme_rejected" $nonXp3Scheme.error.code (
        "explicit scheme on non-XP3 error")
    Assert-Equal "xp3:scheme:__NOCRYPT__" (
        $nonXp3Scheme.error.details.schemeResolution.identity) (
        "explicit scheme on non-XP3 identity")

    $schemeCheckMissingOptionResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $duplicateZip, "--output", "json")
    Assert-Equal 2 $schemeCheckMissingOptionResult.ExitCode (
        "archive scheme-check missing option exit code")
    $schemeCheckMissingOption = Read-JsonEnvelope (
        $schemeCheckMissingOptionResult)
    Assert-Equal "xp3_scheme_required" (
        $schemeCheckMissingOption.error.code) (
        "archive scheme-check missing option error")

    $nonXp3SchemeCheckResult = Invoke-Cli -Arguments @(
        "archive", "scheme-check", $duplicateZip,
        "--scheme", "__NOCRYPT__", "--output", "json")
    Assert-Equal 3 $nonXp3SchemeCheckResult.ExitCode (
        "archive scheme-check non-XP3 exit code")
    $nonXp3SchemeCheck = Read-JsonEnvelope $nonXp3SchemeCheckResult
    Assert-Equal "xp3_scheme_rejected" $nonXp3SchemeCheck.error.code (
        "archive scheme-check non-XP3 error")
    Assert-Equal "none" (
        $nonXp3SchemeCheck.error.details.schemeResolution.scheme.family) (
        "archive scheme-check reports safe scheme metadata")

    $manifestInputDestination = Join-Path $testRoot "manifest-input-collision"
    $manifestInputCollisionResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $manifestInputDestination,
        "--duplicate-policy", "suffix-index",
        "--manifest", $duplicateZip,
        "--overwrite", "replace", "--output", "json")
    Assert-Equal 6 $manifestInputCollisionResult.ExitCode (
        "manifest and source collision exit code")
    $manifestInputCollision = Read-JsonEnvelope $manifestInputCollisionResult
    Assert-Equal "manifest_input_collision" (
        $manifestInputCollision.error.code) (
        "manifest and source collision error")

    $outputInputCollisionResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $testRoot,
        "--entry-index", "5", "--overwrite", "replace",
        "--output", "json")
    Assert-Equal 6 $outputInputCollisionResult.ExitCode (
        "output and source collision exit code")
    $outputInputCollision = Read-JsonEnvelope $outputInputCollisionResult
    Assert-Equal "output_input_collision" $outputInputCollision.error.code (
        "output and source collision error")
    Assert-Equal "sourceArchive" (
        $outputInputCollision.error.details.inputKind) (
        "output and source collision input kind")

    $manifestOutputDestination = Join-Path $testRoot "manifest-output-collision"
    $manifestOutputPath = Join-Path $manifestOutputDestination "voice\foo.ogg"
    $manifestOutputCollisionResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $manifestOutputDestination,
        "--entry-index", "0", "--manifest", $manifestOutputPath,
        "--output", "json")
    Assert-Equal 6 $manifestOutputCollisionResult.ExitCode (
        "manifest and output collision exit code")
    $manifestOutputCollision = Read-JsonEnvelope $manifestOutputCollisionResult
    Assert-Equal "manifest_output_collision" (
        $manifestOutputCollision.error.code) (
        "manifest and output collision error")
    Assert-Equal $duplicateSourceHash (
        Get-FileHash -LiteralPath $duplicateZip -Algorithm SHA256).Hash (
        "collision preflight preserves the source archive")

    $dryRunExistingDestination = Join-Path $testRoot "manifest-dry-run-output"
    $dryRunExistingManifest = Join-Path $testRoot "manifest-dry-run.jsonl"
    [IO.File]::WriteAllText(
        $dryRunExistingManifest, "sentinel-manifest",
        [Text.UTF8Encoding]::new($false))
    $dryRunExistingHash = (
        Get-FileHash -LiteralPath $dryRunExistingManifest -Algorithm SHA256).Hash
    $dryRunManifestConflictResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $dryRunExistingDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--manifest", $dryRunExistingManifest,
        "--dry-run", "--summary-only", "--output", "json")
    Assert-Equal 6 $dryRunManifestConflictResult.ExitCode (
        "dry-run existing manifest conflict exit code")
    $dryRunManifestConflict = Read-JsonEnvelope $dryRunManifestConflictResult
    Assert-Equal "manifest_exists" $dryRunManifestConflict.error.code (
        "dry-run existing manifest conflict code")
    Assert-Equal $dryRunExistingHash (
        Get-FileHash -LiteralPath $dryRunExistingManifest -Algorithm SHA256).Hash (
        "dry-run existing manifest conflict preserves manifest")
    Assert-True (-not (Test-Path -LiteralPath $dryRunExistingDestination)) (
        "dry-run manifest conflict creates no destination")

    $dryRunManifestReplaceResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $dryRunExistingDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--manifest", $dryRunExistingManifest, "--overwrite", "replace",
        "--dry-run", "--summary-only", "--output", "json")
    Assert-Equal 0 $dryRunManifestReplaceResult.ExitCode (
        "dry-run replace existing manifest exit code")
    [void](Read-JsonEnvelope $dryRunManifestReplaceResult)
    Assert-Equal $dryRunExistingHash (
        Get-FileHash -LiteralPath $dryRunExistingManifest -Algorithm SHA256).Hash (
        "dry-run replace preserves existing manifest")
    Assert-True (-not (Test-Path -LiteralPath $dryRunExistingDestination)) (
        "dry-run replace creates no destination")

    $manifestHardlinkSentinel = Join-Path `
        $testRoot "manifest-hardlink-sentinel.txt"
    $manifestHardlinkPath = Join-Path `
        $testRoot "manifest-hardlink.jsonl"
    [IO.File]::WriteAllText(
        $manifestHardlinkSentinel, "SENTINEL",
        [Text.UTF8Encoding]::new($false))
    New-Item -ItemType HardLink -Path $manifestHardlinkPath `
        -Target $manifestHardlinkSentinel | Out-Null
    $manifestHardlinkDestination = Join-Path `
        $testRoot "manifest-hardlink-output"
    $manifestHardlinkResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $manifestHardlinkDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--manifest", $manifestHardlinkPath, "--overwrite", "replace",
        "--summary-only", "--output", "json")
    Assert-Equal 0 $manifestHardlinkResult.ExitCode (
        "fresh hardlinked manifest copy-on-write exit code")
    [void](Read-JsonEnvelope $manifestHardlinkResult)
    Assert-Equal "SENTINEL" (
        [IO.File]::ReadAllText($manifestHardlinkSentinel)) (
        "fresh manifest replacement preserves the hardlink target")
    Assert-True (
        [IO.File]::ReadAllText($manifestHardlinkPath).StartsWith("{")) (
        "fresh manifest replacement detaches and writes JSONL")

    $manifestResumeShadow = Join-Path `
        $testRoot "manifest-resume-hardlink-shadow.jsonl"
    New-Item -ItemType HardLink -Path $manifestResumeShadow `
        -Target $manifestHardlinkPath | Out-Null
    $manifestResumeShadowHash = (
        Get-FileHash -LiteralPath $manifestResumeShadow -Algorithm SHA256).Hash
    $manifestHardlinkResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $manifestHardlinkDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $manifestHardlinkPath,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $manifestHardlinkResumeResult.ExitCode (
        "resume hardlinked manifest copy-on-write exit code")
    [void](Read-JsonEnvelope $manifestHardlinkResumeResult)
    Assert-Equal $manifestResumeShadowHash (
        Get-FileHash -LiteralPath $manifestResumeShadow -Algorithm SHA256).Hash (
        "resume manifest append preserves the hardlink target")
    Assert-True ($manifestResumeShadowHash -ne (
        Get-FileHash -LiteralPath $manifestHardlinkPath -Algorithm SHA256).Hash) (
        "resume manifest append detaches before journaling")

    $manifestReparseTarget = Join-Path `
        $testRoot "manifest-reparse-target"
    $manifestReparseLink = Join-Path `
        $testRoot "manifest-reparse-link"
    New-Item -ItemType Directory -Path $manifestReparseTarget | Out-Null
    $manifestReparseSentinel = Join-Path `
        $manifestReparseTarget "sentinel.jsonl"
    [IO.File]::WriteAllText(
        $manifestReparseSentinel, "REPARSE-SENTINEL",
        [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Junction -Path $manifestReparseLink `
        -Target $manifestReparseTarget | Out-Null
    $manifestReparseDestination = Join-Path `
        $testRoot "manifest-reparse-output"
    $manifestReparseResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $manifestReparseDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--manifest", (Join-Path $manifestReparseLink "sentinel.jsonl"),
        "--overwrite", "replace", "--summary-only", "--output", "json")
    Assert-Equal 3 $manifestReparseResult.ExitCode (
        "manifest reparse ancestor rejection exit code")
    $manifestReparseError = Read-JsonEnvelope $manifestReparseResult
    Assert-Equal "manifest_reparse_point" (
        $manifestReparseError.error.code) (
        "manifest reparse ancestor stable error")
    Assert-Equal "REPARSE-SENTINEL" (
        [IO.File]::ReadAllText($manifestReparseSentinel)) (
        "manifest reparse rejection preserves the target")
    Assert-True (-not (Test-Path -LiteralPath $manifestReparseDestination)) (
        "manifest reparse rejection performs no extraction writes")

    $duplicateExtractDestination = Join-Path $testRoot "duplicate-extract"
    $duplicateManifest = Join-Path $testRoot "duplicate-extraction.jsonl"
    $duplicateFreshResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index",
        "--budget", "auto", "--manifest", $duplicateManifest,
        "--checksum", "sha256", "--summary-only",
        "--output", "json")
    Assert-Equal 0 $duplicateFreshResult.ExitCode (
        "fresh manifest extraction exit code")
    $duplicateFresh = Read-JsonEnvelope $duplicateFreshResult
    Assert-Equal "success" $duplicateFresh.status (
        "fresh manifest extraction status")
    Assert-Equal 6 $duplicateFresh.data.written (
        "fresh manifest extraction written count")
    Assert-Equal 0 $duplicateFresh.data.verifiedExisting (
        "fresh manifest extraction verified count")
    Assert-Equal "sha256" $duplicateFresh.data.checksum (
        "fresh manifest extraction checksum mode")
    Assert-True $duplicateFresh.data.manifestWritten (
        "fresh manifest extraction manifest marker")
    Assert-True ($null -eq $duplicateFresh.data.files) (
        "fresh summary-only extraction omits files")
    Assert-Equal 6 @(
        Get-ChildItem -LiteralPath $duplicateExtractDestination -Recurse -File
    ).Count "fresh manifest extraction output count"
    Assert-True (Test-Path -LiteralPath $duplicateManifest -PathType Leaf) (
        "fresh extraction manifest exists")

    $duplicateManifestRows = @(
        Get-Content -LiteralPath $duplicateManifest |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    Assert-Equal 8 $duplicateManifestRows.Count (
        "fresh extraction manifest record count")
    Assert-Equal "garbro.extraction-manifest/v1" (
        $duplicateManifestRows[0].schemaVersion) (
        "fresh extraction manifest schema")
    Assert-Equal "header" $duplicateManifestRows[0].record (
        "fresh extraction manifest header")
    Assert-Equal $duplicateSourceHash.ToLowerInvariant() (
        $duplicateManifestRows[0].sourceArchive.sha256) (
        "fresh extraction source checksum")
    $duplicateManifestEntries = @(
        $duplicateManifestRows | Where-Object record -eq "entry"
    )
    Assert-Equal 6 $duplicateManifestEntries.Count (
        "fresh extraction manifest logical entry count")
    Assert-Equal 6 @(
        $duplicateManifestEntries.entryIndex | Sort-Object -Unique
    ).Count "fresh extraction manifest unique entry indexes"
    foreach ($manifestEntry in $duplicateManifestEntries) {
        Assert-Equal 64 $manifestEntry.outputSha256.Length (
            "fresh extraction manifest output SHA-256")
        Assert-True $manifestEntry.outputSizeKnown (
            "fresh extraction manifest output size known")
        $manifestOutputPath = Join-Path `
            $duplicateExtractDestination $manifestEntry.outputRelativePath
        Assert-Equal $manifestEntry.actualBytes (
            Get-Item -LiteralPath $manifestOutputPath).Length (
            "fresh extraction manifest actual byte count")
        Assert-Equal $manifestEntry.outputSha256 (
            (Get-FileHash -LiteralPath $manifestOutputPath -Algorithm SHA256
            ).Hash.ToLowerInvariant()) (
            "fresh extraction manifest checksum matches output")
    }
    Assert-Equal "summary" $duplicateManifestRows[-1].record (
        "fresh extraction manifest summary")
    $duplicateManifestIndexZero = @(
        $duplicateManifestEntries | Where-Object entryIndex -eq 0
    )[0]
    $duplicateManifestIndexOne = @(
        $duplicateManifestEntries | Where-Object entryIndex -eq 1
    )[0]
    $duplicateManifestIndexTwo = @(
        $duplicateManifestEntries | Where-Object entryIndex -eq 2
    )[0]
    Assert-Equal $duplicateManifestIndexZero.outputSha256 (
        $duplicateManifestIndexOne.outputSha256) (
        "manifest preserves equal-content duplicate logical entries")
    Assert-True (
        $duplicateManifestIndexZero.outputSha256 -ne
            $duplicateManifestIndexTwo.outputSha256) (
        "manifest distinguishes different-content duplicate entries")

    $manifestWithoutNewlineBytes = [IO.File]::ReadAllBytes(
        $duplicateManifest)
    $manifestWithoutNewlineLength = $manifestWithoutNewlineBytes.Length
    while ($manifestWithoutNewlineLength -gt 0 -and
           $manifestWithoutNewlineBytes[$manifestWithoutNewlineLength - 1] `
               -in [byte[]](10, 13)) {
        --$manifestWithoutNewlineLength
    }
    $manifestWithoutNewline = [byte[]]::new($manifestWithoutNewlineLength)
    [Array]::Copy(
        $manifestWithoutNewlineBytes, $manifestWithoutNewline,
        $manifestWithoutNewlineLength)
    [IO.File]::WriteAllBytes($duplicateManifest, $manifestWithoutNewline)

    $duplicateVerifySizeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-size",
        "--resume-manifest", $duplicateManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateVerifySizeResult.ExitCode (
        "manifest verify-size exit code")
    $duplicateVerifySize = Read-JsonEnvelope $duplicateVerifySizeResult
    Assert-Equal 6 $duplicateVerifySize.data.verifiedExisting (
        "manifest verify-size verified count")
    Assert-Equal 0 $duplicateVerifySize.data.written (
        "manifest verify-size written count")
    Assert-Equal "success" $duplicateVerifySize.status (
        "manifest verify-size does not downgrade status")
    $manifestAfterMissingNewline = @(
        Get-Content -LiteralPath $duplicateManifest |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    Assert-Equal "summary" $manifestAfterMissingNewline[-1].record (
        "resume inserts a JSONL boundary after a valid unterminated record")

    $manifestRecordsBeforeCrashTail = $manifestAfterMissingNewline.Count
    $manifestBeforeCrashBytes = [IO.File]::ReadAllBytes($duplicateManifest)
    $manifestBeforeCrashLength = $manifestBeforeCrashBytes.Length
    while ($manifestBeforeCrashLength -gt 0 -and
           $manifestBeforeCrashBytes[$manifestBeforeCrashLength - 1] `
               -in [byte[]](10, 13)) {
        --$manifestBeforeCrashLength
    }
    $manifestBeforeCrashNoNewline = [byte[]]::new($manifestBeforeCrashLength)
    [Array]::Copy(
        $manifestBeforeCrashBytes, $manifestBeforeCrashNoNewline,
        $manifestBeforeCrashLength)
    [IO.File]::WriteAllBytes(
        $duplicateManifest, $manifestBeforeCrashNoNewline)

    $manifestCrashTail = [Text.UTF8Encoding]::new($false).GetBytes(
        '{"schemaVersion":"garbro.extraction-manifest/v1","record":"entry"')
    $manifestCrashStream = [IO.File]::Open(
        $duplicateManifest, [IO.FileMode]::Append,
        [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $manifestCrashStream.Write(
            $manifestCrashTail, 0, $manifestCrashTail.Length)
    }
    finally {
        $manifestCrashStream.Dispose()
    }
    $duplicateCrashTailResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-size", "--manifest", $duplicateManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateCrashTailResult.ExitCode (
        "manifest truncated-tail recovery exit code")
    $duplicateCrashTail = Read-JsonEnvelope $duplicateCrashTailResult
    Assert-Equal 6 $duplicateCrashTail.data.verifiedExisting (
        "manifest truncated-tail recovery verified count")
    $manifestAfterCrashTail = @(
        Get-Content -LiteralPath $duplicateManifest |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
    Assert-Equal "summary" $manifestAfterCrashTail[-1].record (
        "manifest truncated tail is removed before append")
    Assert-Equal ($manifestRecordsBeforeCrashTail + 1) (
        $manifestAfterCrashTail.Count) (
        "manifest recovery preserves the valid unterminated record prefix")

    $duplicateInvalidManifest = Join-Path `
        $testRoot "duplicate-extraction-invalid.jsonl"
    Copy-Item -LiteralPath $duplicateManifest -Destination $duplicateInvalidManifest
    [IO.File]::AppendAllText(
        $duplicateInvalidManifest,
        ('{"schemaVersion":"garbro.extraction-manifest/v1",' +
         '"record":"summary"}{"x":1}' + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $duplicateInvalidManifestHash = (
        Get-FileHash -LiteralPath $duplicateInvalidManifest -Algorithm SHA256).Hash
    $duplicateInvalidManifestResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-size",
        "--resume-manifest", $duplicateInvalidManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $duplicateInvalidManifestResult.ExitCode (
        "manifest concatenated-record rejection exit code")
    $duplicateInvalidManifestError = Read-JsonEnvelope (
        $duplicateInvalidManifestResult)
    Assert-Equal "invalid_extraction_manifest" (
        $duplicateInvalidManifestError.error.code) (
        "manifest concatenated-record rejection code")
    Assert-Equal $duplicateInvalidManifestHash (
        Get-FileHash -LiteralPath $duplicateInvalidManifest -Algorithm SHA256).Hash (
        "invalid completed manifest line is not modified")

    $duplicateGarbageManifest = Join-Path `
        $testRoot "duplicate-extraction-garbage.jsonl"
    Copy-Item -LiteralPath $duplicateManifest -Destination $duplicateGarbageManifest
    $garbageManifestBytes = [IO.File]::ReadAllBytes($duplicateGarbageManifest)
    $garbageManifestLength = $garbageManifestBytes.Length
    while ($garbageManifestLength -gt 0 -and
           $garbageManifestBytes[$garbageManifestLength - 1] `
               -in [byte[]](10, 13)) {
        --$garbageManifestLength
    }
    $garbageManifestWithoutNewline = [byte[]]::new($garbageManifestLength)
    [Array]::Copy(
        $garbageManifestBytes, $garbageManifestWithoutNewline,
        $garbageManifestLength)
    [IO.File]::WriteAllBytes(
        $duplicateGarbageManifest, $garbageManifestWithoutNewline)
    [IO.File]::AppendAllText(
        $duplicateGarbageManifest, "garbage",
        [Text.UTF8Encoding]::new($false))
    $duplicateGarbageManifestResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-size",
        "--resume-manifest", $duplicateGarbageManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $duplicateGarbageManifestResult.ExitCode (
        "manifest unterminated garbage rejection exit code")
    $duplicateGarbageManifestError = Read-JsonEnvelope (
        $duplicateGarbageManifestResult)
    Assert-Equal "invalid_extraction_manifest" (
        $duplicateGarbageManifestError.error.code) (
        "manifest unterminated garbage rejection code")

    $duplicateUtf16Manifest = Join-Path `
        $testRoot "duplicate-extraction-utf16.jsonl"
    [IO.File]::WriteAllText(
        $duplicateUtf16Manifest,
        [IO.File]::ReadAllText(
            $duplicateManifest, [Text.UTF8Encoding]::new($false, $true)),
        [Text.Encoding]::Unicode)
    $duplicateUtf16ManifestResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-size",
        "--resume-manifest", $duplicateUtf16Manifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $duplicateUtf16ManifestResult.ExitCode (
        "UTF-16 extraction manifest rejection exit code")
    $duplicateUtf16ManifestError = Read-JsonEnvelope (
        $duplicateUtf16ManifestResult)
    Assert-Equal "invalid_extraction_manifest" (
        $duplicateUtf16ManifestError.error.code) (
        "UTF-16 extraction manifest stable error")

    $duplicateInvalidUtf8Manifest = Join-Path `
        $testRoot "duplicate-extraction-invalid-utf8.jsonl"
    $validManifestBytes = [IO.File]::ReadAllBytes($duplicateManifest)
    $invalidManifestBytes = [byte[]]::new($validManifestBytes.Length + 2)
    [Array]::Copy(
        $validManifestBytes, $invalidManifestBytes, $validManifestBytes.Length)
    $invalidManifestBytes[$validManifestBytes.Length] = 0xC3
    $invalidManifestBytes[$validManifestBytes.Length + 1] = 0x28
    [IO.File]::WriteAllBytes(
        $duplicateInvalidUtf8Manifest, $invalidManifestBytes)
    $duplicateInvalidUtf8Result = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-size",
        "--resume-manifest", $duplicateInvalidUtf8Manifest,
        "--summary-only", "--output", "json")
    Assert-Equal 3 $duplicateInvalidUtf8Result.ExitCode (
        "invalid UTF-8 extraction manifest rejection exit code")
    $duplicateInvalidUtf8 = Read-JsonEnvelope $duplicateInvalidUtf8Result
    Assert-Equal "invalid_extraction_manifest" (
        $duplicateInvalidUtf8.error.code) (
        "invalid UTF-8 extraction manifest stable error")

    $duplicateVerifyHashResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $duplicateManifest,
        "--output", "jsonl")
    Assert-Equal 0 $duplicateVerifyHashResult.ExitCode (
        "manifest verify-hash exit code")
    $duplicateVerifyHashEvents = Read-JsonLines $duplicateVerifyHashResult
    $duplicateVerifiedFiles = @(
        $duplicateVerifyHashEvents | Where-Object event -eq "file"
    )
    Assert-Equal 6 $duplicateVerifiedFiles.Count (
        "manifest verify-hash file event count")
    foreach ($verifiedFile in $duplicateVerifiedFiles) {
        Assert-Equal "verified_existing" $verifiedFile.data.status (
            "manifest verify-hash file status")
        Assert-True ($verifiedFile.data.offset -ge 0) (
            "manifest verify-hash file event offset")
        Assert-True $verifiedFile.data.outputSizeKnown (
            "manifest verify-hash reports known output size")
        Assert-Equal 64 $verifiedFile.data.outputSha256.Length (
            "manifest verify-hash reports output checksum")
        Assert-Equal "sha256" $verifiedFile.data.checksum.algorithm (
            "manifest verify-hash checksum algorithm")
    }
    $duplicateVerifyHash = $duplicateVerifyHashEvents[-1]
    Assert-Equal 6 $duplicateVerifyHash.data.verifiedExisting (
        "manifest verify-hash verified count")
    Assert-Equal 0 $duplicateVerifyHash.data.written (
        "manifest verify-hash written count")
    Assert-True ($null -eq $duplicateVerifyHash.data.files) (
        "manifest JSONL summary does not aggregate file results")
    Assert-True ($null -eq $duplicateVerifyHash.data.failures) (
        "manifest JSONL summary does not aggregate failures")

    $duplicateMissingPath = Join-Path `
        $duplicateExtractDestination $duplicateNaturalSuffix.outputRelativePath
    $duplicateMissingHash = @(
        $duplicateManifestEntries | Where-Object entryIndex -eq 4
    )[0].outputSha256
    Remove-Item -LiteralPath $duplicateMissingPath -Force
    $duplicateMissingResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $duplicateManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateMissingResult.ExitCode (
        "manifest missing-file resume exit code")
    $duplicateMissing = Read-JsonEnvelope $duplicateMissingResult
    Assert-Equal 1 $duplicateMissing.data.written (
        "manifest missing-file resume writes one file")
    Assert-Equal 5 $duplicateMissing.data.verifiedExisting (
        "manifest missing-file resume verifies remaining files")
    Assert-Equal 0 $duplicateMissing.data.repaired (
        "manifest missing-file resume is not a repair")
    Assert-True (Test-Path -LiteralPath $duplicateMissingPath -PathType Leaf) (
        "manifest missing-file resume restores output")
    Assert-Equal $duplicateMissingHash (
        (Get-FileHash -LiteralPath $duplicateMissingPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()) (
        "manifest missing-file resume restores expected content")

    $duplicateCorruptPath = Join-Path `
        $duplicateExtractDestination $duplicateIndexTwo.outputRelativePath
    $duplicateExpectedHash = $duplicateManifestIndexTwo.outputSha256
    $duplicateCorruptLength = (Get-Item -LiteralPath $duplicateCorruptPath).Length
    $duplicateCorruptBytes = New-Object byte[] ([int]$duplicateCorruptLength)
    [IO.File]::WriteAllBytes($duplicateCorruptPath, $duplicateCorruptBytes)
    Assert-Equal $duplicateCorruptLength (
        Get-Item -LiteralPath $duplicateCorruptPath).Length (
        "same-size corruption preserves output length")
    $duplicateCorruptHash = (
        Get-FileHash -LiteralPath $duplicateCorruptPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    Assert-True ($duplicateCorruptHash -ne $duplicateExpectedHash) (
        "same-size corruption changes output hash")
    $manifestLinesBeforeConflict = @(
        Get-Content -LiteralPath $duplicateManifest).Count
    $duplicateHashConflictResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $duplicateManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 6 $duplicateHashConflictResult.ExitCode (
        "same-size hash corruption conflict exit code")
    $duplicateHashConflict = Read-JsonEnvelope $duplicateHashConflictResult
    Assert-Equal "resume_verification_failed" (
        $duplicateHashConflict.error.code) (
        "same-size hash corruption conflict error")
    Assert-Equal $duplicateCorruptHash (
        (Get-FileHash -LiteralPath $duplicateCorruptPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()) (
        "hash conflict does not replace corrupt output")
    Assert-Equal $manifestLinesBeforeConflict @(
        Get-Content -LiteralPath $duplicateManifest).Count (
        "hash conflict does not append the manifest")

    $duplicateRepairResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $duplicateManifest,
        "--overwrite", "replace", "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateRepairResult.ExitCode (
        "same-size hash repair exit code")
    $duplicateRepair = Read-JsonEnvelope $duplicateRepairResult
    Assert-Equal 1 $duplicateRepair.data.written (
        "same-size hash repair written count")
    Assert-Equal 1 $duplicateRepair.data.repaired (
        "same-size hash repair repaired count")
    Assert-Equal 5 $duplicateRepair.data.verifiedExisting (
        "same-size hash repair verified count")
    Assert-Equal $duplicateExpectedHash (
        (Get-FileHash -LiteralPath $duplicateCorruptPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()) (
        "same-size hash repair restores expected content")

    $duplicateFinalResumeResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $duplicateManifest,
        "--summary-only", "--output", "json")
    Assert-Equal 0 $duplicateFinalResumeResult.ExitCode (
        "repaired manifest replay exit code")
    $duplicateFinalResume = Read-JsonEnvelope $duplicateFinalResumeResult
    Assert-Equal 6 $duplicateFinalResume.data.verifiedExisting (
        "repaired manifest replay verified count")
    Assert-Equal 0 $duplicateFinalResume.data.written (
        "repaired manifest replay written count")

    $duplicateIndexZeroExtractedPath = Join-Path `
        $duplicateExtractDestination $duplicateIndexZero.outputRelativePath
    $duplicateOutputHashBeforeSourceMismatch = (
        Get-FileHash -LiteralPath $duplicateIndexZeroExtractedPath -Algorithm SHA256
    ).Hash
    $duplicateManifestLinesBeforeSourceMismatch = @(
        Get-Content -LiteralPath $duplicateManifest).Count
    $duplicateOriginalWriteTime = (
        Get-Item -LiteralPath $duplicateZip).LastWriteTimeUtc
    (Get-Item -LiteralPath $duplicateZip).LastWriteTimeUtc = (
        $duplicateOriginalWriteTime.AddSeconds(2))
    $duplicateSourceMismatchResult = Invoke-Cli -Arguments @(
        "archive", "extract", $duplicateZip,
        "--destination", $duplicateExtractDestination,
        "--duplicate-policy", "suffix-index", "--budget", "auto",
        "--resume", "verify-hash", "--manifest", $duplicateManifest,
        "--overwrite", "replace", "--summary-only", "--output", "json")
    Assert-Equal 3 $duplicateSourceMismatchResult.ExitCode (
        "changed source identity resume exit code")
    $duplicateSourceMismatch = Read-JsonEnvelope $duplicateSourceMismatchResult
    Assert-Equal "manifest_source_mismatch" (
        $duplicateSourceMismatch.error.code) (
        "changed source identity resume error")
    Assert-Equal $duplicateManifestLinesBeforeSourceMismatch @(
        Get-Content -LiteralPath $duplicateManifest).Count (
        "changed source identity does not append manifest")
    Assert-Equal $duplicateOutputHashBeforeSourceMismatch (
        Get-FileHash -LiteralPath $duplicateIndexZeroExtractedPath -Algorithm SHA256
    ).Hash (
        "changed source identity performs no output writes")

    $imageBatchSource = Join-Path $testRoot "image-batch-source"
    $imageBatchNested = Join-Path $imageBatchSource "nested"
    New-Item -ItemType Directory -Path $imageBatchNested -Force | Out-Null
    $imageBatchRootImage = Join-Path $imageBatchSource "visible.png"
    $imageBatchNestedImage = Join-Path $imageBatchNested "nested.png"
    $imageBatchExtensionless = Join-Path $imageBatchNested "extensionless-image"
    New-PngFixture -Path $imageBatchRootImage
    New-PngFixture -Path $imageBatchNestedImage
    New-PngFixture -Path $imageBatchExtensionless
    [IO.File]::WriteAllText(
        (Join-Path $imageBatchNested "note.txt"), "not an image",
        [Text.UTF8Encoding]::new($false))

    $imageBatchEmptyDestination = Join-Path $testRoot "image-batch-empty-output"
    $imageBatchEmptyResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchEmptyDestination,
        "--format", "png", "--recursive",
        "--include", "missing/**/*.png", "--dry-run", "--output", "json")
    Assert-Equal 3 $imageBatchEmptyResult.ExitCode (
        "image batch empty selection exit code")
    $imageBatchEmpty = Read-JsonEnvelope $imageBatchEmptyResult
    Assert-Equal "no_images_selected" $imageBatchEmpty.error.code (
        "image batch empty selection stable error")
    Assert-Equal 4 $imageBatchEmpty.error.details.scanned (
        "image batch empty selection scanned detail")
    Assert-True (-not (Test-Path -LiteralPath $imageBatchEmptyDestination)) (
        "image batch empty selection creates no destination")

    $imageBatchUnsafeDestination = Join-Path $imageBatchSource "nested\output"
    $imageBatchUnsafeResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchUnsafeDestination,
        "--format", "png", "--recursive", "--dry-run",
        "--output", "json")
    Assert-Equal 3 $imageBatchUnsafeResult.ExitCode (
        "image batch destination-inside-source exit code")
    $imageBatchUnsafe = Read-JsonEnvelope $imageBatchUnsafeResult
    Assert-Equal "destination_inside_source_root" (
        $imageBatchUnsafe.error.code) (
        "image batch destination-inside-source error")
    Assert-True (-not (Test-Path -LiteralPath $imageBatchUnsafeDestination)) (
        "image batch unsafe destination is not created")

    $imageBatchAncestor = Join-Path $testRoot "image-batch-ancestor"
    $imageBatchAncestorSource = Join-Path $imageBatchAncestor "source"
    $imageBatchAncestorNested = Join-Path $imageBatchAncestorSource "source"
    New-Item -ItemType Directory -Path $imageBatchAncestorNested -Force |
        Out-Null
    $imageBatchAncestorVictim = Join-Path `
        $imageBatchAncestorSource "victim.png"
    $imageBatchAncestorInput = Join-Path `
        $imageBatchAncestorNested "victim.png"
    New-PngFixture -Path $imageBatchAncestorVictim
    New-PngFixture -Path $imageBatchAncestorInput
    $imageBatchAncestorVictimHash = (
        Get-FileHash -LiteralPath $imageBatchAncestorVictim -Algorithm SHA256
    ).Hash
    $imageBatchAncestorCollisionResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchAncestorSource,
        "--destination", $imageBatchAncestor,
        "--format", "png", "--recursive", "--overwrite", "replace",
        "--output", "json")
    Assert-Equal 6 $imageBatchAncestorCollisionResult.ExitCode (
        "image batch source-tree output collision exit code")
    $imageBatchAncestorCollision = Read-JsonEnvelope (
        $imageBatchAncestorCollisionResult)
    Assert-Equal "output_input_collision" (
        $imageBatchAncestorCollision.error.code) (
        "image batch source-tree output collision code")
    Assert-Equal "sourceRoot" (
        $imageBatchAncestorCollision.error.details.inputKind) (
        "image batch source-tree collision input kind")
    Assert-Equal $imageBatchAncestorVictimHash (
        Get-FileHash -LiteralPath $imageBatchAncestorVictim -Algorithm SHA256
    ).Hash (
        "image batch source-tree collision preserves source files")
    Assert-True (-not (Test-Path -LiteralPath (
        Join-Path $imageBatchAncestor "victim.png"))) (
        "image batch source-tree collision performs no earlier writes")

    $imageBatchHierarchySource = Join-Path `
        $testRoot "image-batch-hierarchy-source"
    $imageBatchHierarchyNested = Join-Path `
        $imageBatchHierarchySource "foo.png"
    New-Item -ItemType Directory -Path $imageBatchHierarchyNested -Force |
        Out-Null
    New-PngFixture -Path (Join-Path $imageBatchHierarchySource "foo.jpg")
    New-PngFixture -Path (Join-Path $imageBatchHierarchyNested "bar.jpg")
    $imageBatchHierarchyDestination = Join-Path `
        $testRoot "image-batch-hierarchy-output"
    $imageBatchHierarchyResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchHierarchySource,
        "--destination", $imageBatchHierarchyDestination,
        "--format", "png", "--recursive", "--output", "json")
    Assert-Equal 3 $imageBatchHierarchyResult.ExitCode (
        "image batch file-directory hierarchy collision exit code")
    $imageBatchHierarchy = Read-JsonEnvelope $imageBatchHierarchyResult
    Assert-Equal "unsafe_output_path" $imageBatchHierarchy.error.code (
        "image batch file-directory hierarchy collision error")
    Assert-Equal "destination_collision" (
        $imageBatchHierarchy.error.details.reason) (
        "image batch file-directory hierarchy collision reason")
    Assert-True (-not (Test-Path -LiteralPath $imageBatchHierarchyDestination)) (
        "image batch hierarchy collision performs no writes")

    $imageBatchDestination = Join-Path $testRoot "image-batch-output"
    $imageBatchDryResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--dry-run", "--output", "jsonl")
    Assert-Equal 0 $imageBatchDryResult.ExitCode (
        "image batch recursive signature dry-run exit code")
    $imageBatchDryEvents = Read-JsonLines $imageBatchDryResult
    $imageBatchDryItems = @(
        $imageBatchDryEvents | Where-Object event -eq "image"
    )
    Assert-Equal 3 $imageBatchDryItems.Count (
        "image batch recursive signature dry-run item count")
    Assert-Equal 3 @(
        $imageBatchDryItems | Where-Object { $_.data.status -eq "planned" }
    ).Count "image batch recursive signature planned events"
    $imageBatchDry = $imageBatchDryEvents[-1]
    Assert-Equal "summary" $imageBatchDry.event (
        "image batch dry-run terminal event")
    Assert-Equal 4 $imageBatchDry.data.scanned (
        "image batch recursive scanned count")
    Assert-Equal 3 $imageBatchDry.data.selected (
        "image batch signature selected count")
    Assert-Equal 3 $imageBatchDry.data.recognized (
        "image batch signature recognized count")
    Assert-Equal 1 $imageBatchDry.data.signatureCandidatesIgnored (
        "image batch ignored non-image signature count")
    Assert-Equal 3 $imageBatchDry.data.planned (
        "image batch dry-run planned count")
    Assert-Equal "imageBatchPlan" $imageBatchDry.data.limits.budgetSource (
        "image batch automatic budget source")
    Assert-True (-not (Test-Path -LiteralPath $imageBatchDestination)) (
        "image batch dry-run creates no destination")

    foreach ($imageLimitCase in @(
        [pscustomobject]@{
            Option = "--max-files"; Value = "2"
            Code = "file_count_limit_exceeded"
        },
        [pscustomobject]@{
            Option = "--max-entry-bytes"; Value = "1"
            Code = "entry_size_limit_exceeded"
        },
        [pscustomobject]@{
            Option = "--max-total-bytes"; Value = "1"
            Code = "total_size_limit_exceeded"
        },
        [pscustomobject]@{
            Option = "--max-depth"; Value = "1"
            Code = "unsafe_output_path"
        }
    )) {
        $imageLimitDestination = Join-Path $testRoot (
            "image-limit-" + $imageLimitCase.Option.TrimStart("-"))
        $imageLimitResult = Invoke-Cli -Arguments @(
            "image", "convert-batch",
            "--source-root", $imageBatchSource,
            "--destination", $imageLimitDestination,
            "--format", "png", "--recursive", "--detect-by-signature",
            $imageLimitCase.Option, $imageLimitCase.Value,
            "--dry-run", "--output", "json")
        Assert-Equal 3 $imageLimitResult.ExitCode (
            "image batch hard limit exit code: $($imageLimitCase.Option)")
        $imageLimit = Read-JsonEnvelope $imageLimitResult
        Assert-Equal $imageLimitCase.Code $imageLimit.error.code (
            "image batch hard limit code: $($imageLimitCase.Option)")
        Assert-True (-not (Test-Path -LiteralPath $imageLimitDestination)) (
            "image batch hard limit creates no destination: " +
            $imageLimitCase.Option)
    }

    $imageBatchRunResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--summary-only", "--output", "jsonl")
    Assert-Equal 0 $imageBatchRunResult.ExitCode (
        "image batch recursive signature conversion exit code")
    $imageBatchRunEvents = Read-JsonLines $imageBatchRunResult
    Assert-Equal 0 @(
        $imageBatchRunEvents | Where-Object event -eq "image"
    ).Count "image batch summary-only omits item events"
    $imageBatchRun = $imageBatchRunEvents[-1]
    Assert-Equal "summary" $imageBatchRun.event (
        "image batch conversion terminal event")
    Assert-Equal 3 $imageBatchRun.data.written (
        "image batch conversion written count")
    Assert-Equal 0 $imageBatchRun.data.failed (
        "image batch conversion failed count")
    Assert-True ($imageBatchRun.data.bytesWritten -gt 0) (
        "image batch conversion byte count")
    Assert-True (
        $imageBatchRun.data.observedBytes -ge
            $imageBatchRun.data.bytesWritten) (
        "image batch reports cumulative observed output bytes")
    $imageBatchRootOutput = Join-Path $imageBatchDestination "visible.png"
    $imageBatchNestedOutput = Join-Path $imageBatchDestination "nested\nested.png"
    $imageBatchExtensionlessOutput = Join-Path `
        $imageBatchDestination "nested\extensionless-image.png"
    [long]$imageBatchCommittedBytes = 0
    foreach ($outputPath in @(
        $imageBatchRootOutput,
        $imageBatchNestedOutput,
        $imageBatchExtensionlessOutput
    )) {
        Assert-True (Test-Path -LiteralPath $outputPath -PathType Leaf) (
            "image batch output exists: $outputPath")
        Assert-True ((Get-Item -LiteralPath $outputPath).Length -gt 0) (
            "image batch output is non-empty: $outputPath")
        $imageBatchCommittedBytes += (Get-Item -LiteralPath $outputPath).Length
    }
    Assert-Equal $imageBatchCommittedBytes $imageBatchRun.data.bytesWritten (
        "image batch bytesWritten equals committed file lengths")

    $imageBatchConflictResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--output", "json")
    Assert-Equal 6 $imageBatchConflictResult.ExitCode (
        "image batch existing destination exit code")
    $imageBatchConflict = Read-JsonEnvelope $imageBatchConflictResult
    Assert-Equal "destination_exists" $imageBatchConflict.error.code (
        "image batch existing destination error")
    Assert-True (-not [string]::IsNullOrWhiteSpace(
        $imageBatchConflict.error.details.sourcePath)) (
        "image batch existing destination source detail")

    $imageBatchVerifyHeaderResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--resume", "verify-header",
        "--output", "json")
    Assert-Equal 0 $imageBatchVerifyHeaderResult.ExitCode (
        "image batch verify-header exit code")
    $imageBatchVerifyHeader = Read-JsonEnvelope $imageBatchVerifyHeaderResult
    Assert-Equal 3 $imageBatchVerifyHeader.data.verifiedExisting (
        "image batch verify-header count")
    Assert-Equal 0 $imageBatchVerifyHeader.data.written (
        "image batch verify-header writes nothing")
    Assert-Equal "success" $imageBatchVerifyHeader.status (
        "image batch verified outputs do not downgrade status")

    [IO.File]::WriteAllBytes(
        $imageBatchNestedOutput, [Text.Encoding]::ASCII.GetBytes("not-png"))
    $imageBatchResumeConflictResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--resume", "verify-header",
        "--output", "json")
    Assert-Equal 6 $imageBatchResumeConflictResult.ExitCode (
        "image batch invalid resume output exit code")
    $imageBatchResumeConflict = Read-JsonEnvelope (
        $imageBatchResumeConflictResult)
    Assert-Equal "resume_verification_failed" (
        $imageBatchResumeConflict.error.code) (
        "image batch invalid resume output error")
    Assert-Equal "header_not_recognized" (
        $imageBatchResumeConflict.error.details.reason) (
        "image batch invalid resume output reason")
    Assert-Equal "not-png" (
        [Text.Encoding]::ASCII.GetString(
            [IO.File]::ReadAllBytes($imageBatchNestedOutput))) (
        "image batch resume conflict preserves invalid output")

    $imageBatchRepairResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--resume", "verify-header",
        "--overwrite", "replace", "--output", "json")
    Assert-Equal 0 $imageBatchRepairResult.ExitCode (
        "image batch resume repair exit code")
    $imageBatchRepair = Read-JsonEnvelope $imageBatchRepairResult
    Assert-Equal 1 $imageBatchRepair.data.written (
        "image batch resume repair written count")
    Assert-Equal 1 $imageBatchRepair.data.repaired (
        "image batch resume repair repaired count")
    Assert-Equal 2 $imageBatchRepair.data.verifiedExisting (
        "image batch resume repair verified count")

    $imageBatchVerifyDecodeResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchDestination,
        "--format", "png", "--recursive", "--detect-by-signature",
        "--budget", "auto", "--resume", "verify-decode",
        "--summary-only", "--output", "json")
    Assert-Equal 0 $imageBatchVerifyDecodeResult.ExitCode (
        "image batch verify-decode exit code")
    $imageBatchVerifyDecode = Read-JsonEnvelope $imageBatchVerifyDecodeResult
    Assert-Equal 3 $imageBatchVerifyDecode.data.verifiedExisting (
        "image batch verify-decode count")
    Assert-Equal 0 $imageBatchVerifyDecode.data.written (
        "image batch verify-decode writes nothing")

    $webpBatchSource = Join-Path $testRoot "image-batch-webp-source"
    New-Item -ItemType Directory -Path $webpBatchSource | Out-Null
    New-PngFixture -Path (Join-Path $webpBatchSource "fixture.png")
    $webpOutputs = @{}
    foreach ($webpCase in @(
        [pscustomobject]@{ Tag = "WEBP/80"; Name = "quality-80" },
        [pscustomobject]@{ Tag = "WEBP/LOSSLESS"; Name = "lossless" }
    )) {
        $webpBatchDestination = Join-Path $testRoot (
            "image-batch-webp-" + $webpCase.Name)
        $webpBatchConvertResult = Invoke-Cli -Arguments @(
            "image", "convert-batch",
            "--source-root", $webpBatchSource,
            "--destination", $webpBatchDestination,
            "--format", $webpCase.Tag,
            "--budget", "auto", "--summary-only", "--output", "json")
        Assert-Equal 0 $webpBatchConvertResult.ExitCode (
            "WebP $($webpCase.Name) batch conversion exit code")
        $webpBatchConvert = Read-JsonEnvelope $webpBatchConvertResult
        Assert-Equal 1 $webpBatchConvert.data.written (
            "WebP $($webpCase.Name) batch conversion written count")
        $webpBatchOutput = Join-Path $webpBatchDestination "fixture.webp"
        Assert-True (Test-Path -LiteralPath $webpBatchOutput -PathType Leaf) (
            "WebP $($webpCase.Name) batch output exists")
        $webpBytes = [IO.File]::ReadAllBytes($webpBatchOutput)
        Assert-True ($webpBytes.Length -ge 12) (
            "WebP $($webpCase.Name) batch output has a complete header")
        Assert-Equal "RIFF" (
            [Text.Encoding]::ASCII.GetString($webpBytes, 0, 4)) (
            "WebP $($webpCase.Name) RIFF signature")
        Assert-Equal "WEBP" (
            [Text.Encoding]::ASCII.GetString($webpBytes, 8, 4)) (
            "WebP $($webpCase.Name) container signature")
        $expectedWebpChunk = if ($webpCase.Tag -eq "WEBP/LOSSLESS") {
            "VP8L"
        }
        else {
            "VP8 "
        }
        Assert-Equal $expectedWebpChunk (
            [Text.Encoding]::ASCII.GetString($webpBytes, 12, 4)) (
            "WebP $($webpCase.Name) encoding variant")
        $webpOutputs[$webpCase.Tag] = $webpBatchOutput

        foreach ($resumeMode in @("verify-header", "verify-decode")) {
            $webpBatchResumeResult = Invoke-Cli -Arguments @(
                "image", "convert-batch",
                "--source-root", $webpBatchSource,
                "--destination", $webpBatchDestination,
                "--format", $webpCase.Tag,
                "--budget", "auto", "--resume", $resumeMode,
                "--summary-only", "--output", "json")
            Assert-Equal 0 $webpBatchResumeResult.ExitCode (
                "WebP $($webpCase.Name) $resumeMode exit code")
            $webpBatchResume = Read-JsonEnvelope $webpBatchResumeResult
            Assert-Equal 1 $webpBatchResume.data.verifiedExisting (
                "WebP $($webpCase.Name) $resumeMode verified count")
            Assert-Equal 0 $webpBatchResume.data.written (
                "WebP $($webpCase.Name) $resumeMode writes nothing")
        }
    }

    foreach ($webpCrossCase in @(
        [pscustomobject]@{
            Existing = "WEBP/LOSSLESS"
            Requested = "WEBP/80"
            Mode = "verify-header"
        },
        [pscustomobject]@{
            Existing = "WEBP/80"
            Requested = "WEBP/LOSSLESS"
            Mode = "verify-decode"
        }
    )) {
        $webpCrossOutput = $webpOutputs[$webpCrossCase.Existing]
        $webpCrossHash = (
            Get-FileHash -LiteralPath $webpCrossOutput -Algorithm SHA256
        ).Hash
        $webpCrossResult = Invoke-Cli -Arguments @(
            "image", "convert-batch",
            "--source-root", $webpBatchSource,
            "--destination", ([IO.Path]::GetDirectoryName($webpCrossOutput)),
            "--format", $webpCrossCase.Requested,
            "--budget", "auto", "--resume", $webpCrossCase.Mode,
            "--summary-only", "--output", "json")
        Assert-Equal 6 $webpCrossResult.ExitCode (
            "WebP preset cross-resume exit code: " +
            "$($webpCrossCase.Existing) -> $($webpCrossCase.Requested)")
        $webpCross = Read-JsonEnvelope $webpCrossResult
        Assert-Equal "resume_verification_failed" $webpCross.error.code (
            "WebP preset cross-resume error code")
        Assert-Equal "target_format_mismatch" (
            $webpCross.error.details.reason) (
            "WebP preset cross-resume mismatch reason")
        Assert-Equal $webpCrossHash (
            Get-FileHash -LiteralPath $webpCrossOutput -Algorithm SHA256
        ).Hash (
            "WebP preset cross-resume preserves existing output")
    }

    $extensionlessWebp = Join-Path $webpBatchSource "extensionless-webp"
    [IO.File]::Copy(
        $webpOutputs["WEBP/LOSSLESS"], $extensionlessWebp, $true)
    $extensionlessWebpResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $webpBatchSource,
        "--destination", (Join-Path $testRoot "extensionless-webp-output"),
        "--format", "png", "--detect-by-signature",
        "--include", "extensionless-webp", "--budget", "auto",
        "--dry-run", "--summary-only", "--output", "json")
    Assert-Equal 0 $extensionlessWebpResult.ExitCode (
        "extensionless WebP signature discovery exit code")
    $extensionlessWebpRun = Read-JsonEnvelope $extensionlessWebpResult
    Assert-Equal 1 $extensionlessWebpRun.data.selected (
        "extensionless WebP signature discovery selected count")
    Assert-Equal 1 $extensionlessWebpRun.data.recognized (
        "extensionless WebP signature discovery recognized count")
    Assert-Equal 0 $extensionlessWebpRun.data.signatureCandidatesIgnored (
        "extensionless WebP signature discovery is not ignored")

    $imageBatchManifest = Join-Path $testRoot "image-batch-inputs.jsonl"
    [IO.File]::WriteAllLines(
        $imageBatchManifest,
        [string[]]@(
            "visible.png",
            (@{ sourcePath = $imageBatchNestedImage } |
                ConvertTo-Json -Compress)
        ),
        [Text.UTF8Encoding]::new($false))

    $imageBatchManifestCollisionDestination = Join-Path `
        $testRoot "image-batch-manifest-collision"
    New-Item -ItemType Directory `
        -Path $imageBatchManifestCollisionDestination -Force | Out-Null
    $imageBatchManifestCollisionPath = Join-Path `
        $imageBatchManifestCollisionDestination "visible.png"
    [IO.File]::WriteAllText(
        $imageBatchManifestCollisionPath, "visible.png",
        [Text.UTF8Encoding]::new($false))
    $imageBatchManifestCollisionHash = (
        Get-FileHash -LiteralPath $imageBatchManifestCollisionPath `
            -Algorithm SHA256
    ).Hash
    $imageBatchManifestCollisionResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchManifestCollisionDestination,
        "--format", "png", "--manifest", $imageBatchManifestCollisionPath,
        "--overwrite", "replace", "--output", "json")
    Assert-Equal 6 $imageBatchManifestCollisionResult.ExitCode (
        "image batch manifest-output collision exit code")
    $imageBatchManifestCollision = Read-JsonEnvelope (
        $imageBatchManifestCollisionResult)
    Assert-Equal "output_input_collision" (
        $imageBatchManifestCollision.error.code) (
        "image batch manifest-output collision code")
    Assert-Equal "sourceManifest" (
        $imageBatchManifestCollision.error.details.inputKind) (
        "image batch manifest-output collision input kind")
    Assert-Equal $imageBatchManifestCollisionHash (
        Get-FileHash -LiteralPath $imageBatchManifestCollisionPath `
            -Algorithm SHA256
    ).Hash (
        "image batch manifest-output collision preserves manifest")

    $imageBatchManifestDestination = Join-Path `
        $testRoot "image-batch-manifest-output"
    $imageBatchManifestResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchManifestDestination,
        "--format", "png", "--manifest", $imageBatchManifest,
        "--budget", "auto", "--output", "json")
    Assert-Equal 0 $imageBatchManifestResult.ExitCode (
        "image batch source manifest exit code")
    $imageBatchManifestRun = Read-JsonEnvelope $imageBatchManifestResult
    Assert-Equal 2 $imageBatchManifestRun.data.scanned (
        "image batch source manifest scanned count")
    Assert-Equal 2 $imageBatchManifestRun.data.selected (
        "image batch source manifest selected count")
    Assert-Equal 2 $imageBatchManifestRun.data.written (
        "image batch source manifest written count")
    Assert-True (Test-Path -LiteralPath (
        Join-Path $imageBatchManifestDestination "visible.png") -PathType Leaf) (
        "image batch plain manifest path output")
    Assert-True (Test-Path -LiteralPath (
        Join-Path $imageBatchManifestDestination "nested\nested.png") -PathType Leaf) (
        "image batch JSONL manifest path output")

    $imageBatchDuplicateManifest = Join-Path `
        $testRoot "image-batch-duplicate-manifest.txt"
    [IO.File]::WriteAllLines(
        $imageBatchDuplicateManifest,
        [string[]]@("visible.png", "visible.png"),
        [Text.UTF8Encoding]::new($false))
    $imageBatchDuplicateManifestResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", (Join-Path $testRoot "image-batch-duplicate-output"),
        "--format", "png", "--manifest", $imageBatchDuplicateManifest,
        "--dry-run", "--output", "json")
    Assert-Equal 3 $imageBatchDuplicateManifestResult.ExitCode (
        "image batch duplicate manifest exit code")
    $imageBatchDuplicateManifestError = Read-JsonEnvelope (
        $imageBatchDuplicateManifestResult)
    Assert-Equal "duplicate_manifest_path" (
        $imageBatchDuplicateManifestError.error.code) (
        "image batch duplicate manifest error")
    Assert-Equal 2 $imageBatchDuplicateManifestError.error.details.line (
        "image batch duplicate manifest line detail")

    $imageBatchRootedManifest = Join-Path `
        $testRoot "image-batch-rooted-manifest.txt"
    [IO.File]::WriteAllText(
        $imageBatchRootedManifest, $imageBatchRootImage,
        [Text.UTF8Encoding]::new($false))
    $imageBatchRootedManifestResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", (Join-Path $testRoot "image-batch-rooted-output"),
        "--format", "png", "--manifest", $imageBatchRootedManifest,
        "--dry-run", "--output", "json")
    Assert-Equal 3 $imageBatchRootedManifestResult.ExitCode (
        "image batch rooted manifest exit code")
    $imageBatchRootedManifestError = Read-JsonEnvelope (
        $imageBatchRootedManifestResult)
    Assert-Equal "invalid_manifest_path" (
        $imageBatchRootedManifestError.error.code) (
        "image batch rooted manifest error")
    Assert-Equal "rooted_plain_text_path" (
        $imageBatchRootedManifestError.error.details.reason) (
        "image batch rooted manifest reason")

    $imageBatchUtf16Manifest = Join-Path `
        $testRoot "image-batch-utf16-manifest.txt"
    [IO.File]::WriteAllText(
        $imageBatchUtf16Manifest, "visible.png", [Text.Encoding]::Unicode)
    $imageBatchUtf16Result = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", (Join-Path $testRoot "image-batch-utf16-output"),
        "--format", "png", "--manifest", $imageBatchUtf16Manifest,
        "--dry-run", "--output", "json")
    Assert-Equal 3 $imageBatchUtf16Result.ExitCode (
        "image batch UTF-16 manifest rejection exit code")
    $imageBatchUtf16 = Read-JsonEnvelope $imageBatchUtf16Result
    Assert-Equal "invalid_manifest_encoding" $imageBatchUtf16.error.code (
        "image batch UTF-16 manifest stable error")

    $imageBatchInvalidUtf8Manifest = Join-Path `
        $testRoot "image-batch-invalid-utf8-manifest.txt"
    [IO.File]::WriteAllBytes(
        $imageBatchInvalidUtf8Manifest,
        [byte[]](0x76, 0x69, 0x73, 0xC3, 0x28))
    $imageBatchInvalidUtf8Result = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", (Join-Path $testRoot "image-batch-invalid-utf8-output"),
        "--format", "png", "--manifest", $imageBatchInvalidUtf8Manifest,
        "--dry-run", "--output", "json")
    Assert-Equal 3 $imageBatchInvalidUtf8Result.ExitCode (
        "image batch invalid UTF-8 manifest rejection exit code")
    $imageBatchInvalidUtf8 = Read-JsonEnvelope $imageBatchInvalidUtf8Result
    Assert-Equal "invalid_manifest_encoding" (
        $imageBatchInvalidUtf8.error.code) (
        "image batch invalid UTF-8 manifest stable error")

    $imageBatchPartialManifest = Join-Path `
        $testRoot "image-batch-partial-manifest.txt"
    [IO.File]::WriteAllLines(
        $imageBatchPartialManifest,
        [string[]]@("visible.png", "nested/note.txt"),
        [Text.UTF8Encoding]::new($false))
    $imageBatchPartialDestination = Join-Path `
        $testRoot "image-batch-partial-output"
    $imageBatchPartialResult = Invoke-Cli -Arguments @(
        "image", "convert-batch",
        "--source-root", $imageBatchSource,
        "--destination", $imageBatchPartialDestination,
        "--format", "png", "--manifest", $imageBatchPartialManifest,
        "--budget", "auto", "--dry-run", "--output", "jsonl")
    Assert-Equal 7 $imageBatchPartialResult.ExitCode (
        "image batch unrecognized manifest source exit code")
    $imageBatchPartialEvents = Read-JsonLines $imageBatchPartialResult
    Assert-Equal 1 @(
        $imageBatchPartialEvents |
            Where-Object { $_.event -eq "image" -and $_.status -eq "failed" }
    ).Count "image batch unrecognized manifest failure event"
    $imageBatchPartial = $imageBatchPartialEvents[-1]
    Assert-Equal "summary" $imageBatchPartial.event (
        "image batch partial terminal event")
    Assert-Equal "partial_success" $imageBatchPartial.status (
        "image batch partial terminal status")
    Assert-Equal 1 $imageBatchPartial.data.failed (
        "image batch partial failed count")
    Assert-Equal 1 $imageBatchPartial.data.planned (
        "image batch partial planned count")
    Assert-True (-not (Test-Path -LiteralPath $imageBatchPartialDestination)) (
        "image batch partial dry-run creates no destination")

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
