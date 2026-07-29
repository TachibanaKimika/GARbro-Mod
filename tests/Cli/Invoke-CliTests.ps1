[CmdletBinding()]
param(
    [ValidateSet("Debug", "Prerelease", "Release")]
    [string]$Configuration = "Debug",

    [string]$SampleRoot,

    [string]$HxV4UpstreamRoot
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
    Assert-True ($capabilities.data.commands -contains "archive.extract") (
        "capabilities command list")
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
    foreach ($hxAction in @(
        "schemes", "hash", "generate", "generate-archive", "clean",
        "find-missing-voices",
        "restore-structure", "rename", "krkrdump", "krkrdump-import"
    )) {
        Assert-True ($help.data.usage -like "*hxv4 $hxAction*") (
            "help discovers Hx v4 action: $hxAction")
    }

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

    $hxExistingDump = Join-Path $testRoot "existing-krkrdump"
    New-Item -ItemType Directory -Path $hxExistingDump | Out-Null
    $hxSyntheticLog = @"
Parsing archive: fake.xp3
Index Key: 000102030405060708090A0B0C0D0E0F
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
