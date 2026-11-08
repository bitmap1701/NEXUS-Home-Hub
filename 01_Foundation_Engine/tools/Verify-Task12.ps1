#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 12 automated verification (TASK-12, ADR-61: Multimodal Vision /img & /snap, JPEG <= 250 KB,
    Vision SHA-256 cache, Combat-Mode Shield, Privacy Window Guard, MCP analyze_visual_input) + the two Gate 13 polish items.
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), repository-wide Zero-BOM gate; Gate 13 polish: the Right MFD code card no longer
        repeats its title line (selftest + live PINSTATE) and /copy puts the Win+V history opt-out formats on the clipboard
        (live COPYWIDGET reads ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory back).
    E2  Selftest vision: 2560x1440 colour + noise PNG (> 2 MB) -> JPEG <= 256 000 B, edge <= 1280 px, mock answer echoes the
        exact size + SHA-256, latest_dispatch.json vision fields. Live: /img with a text-only clipboard -> [VISION INFO];
        /img with an image on the clipboard and /img --file on the running Cockpit -> mock vision answer on the HUD.
        TASK-12b: zero-copy decode (PNG / CF_DIB from unmanaged memory) and the 4096 px source limit (selftest); Peak Sampler
        (~100 ms reads) around one clipboard and one --file /img: transient peak reported (soft 120 / 200 MB, not a gate).
    E3  25 identical /img calls = 25 Vision cache hits, 0 tokens, P50 <= 2.0 / P95 <= 3.0 ms (selftest and live pipe);
        one changed pixel or another question = cache miss (selftest).
    E4  Selftest vision-guard (NEXUS_SIMULATE_COMBAT_MODE=1 -> [COMBAT SHIELD] < 2 ms, no screen copy; simulated
        "Vietcombank - Password Manager" -> [PRIVACY GUARD]; safe /snap). Live: MCP analyze_visual_input blocked by both
        shields, then a safe snap shown by the running Cockpit in < 300 ms with the foreground window unchanged.
    E5  TASK-12b: selftest vision-mem (headless: LOH compacted after each call, no growth over the last 3 2K images;
        settled growth reported against 4.3 MB; TASK-12c: every call in McpTools --vision-worker, growth <= 2.0 MB);
        TASK-12c: a fresh mock HostService does 6 x /img 2K in a row in the worker (gdiplus.dll not loaded by vision),
        after the 2K image load + idle trim: settled idle Private WS (median of 8 reads, trim >= 10 s old, shared
        Get-NexusSettledPrivateWs) <= 45.0 MB (Phase 3 gate 52.0), idle CPU < 0.5 %,
        Invoke-NexusRegression -FastRegression (< 90 s, [SLA-WARN SUMMARY]), git guard, CI.
    Gemini runs in mock mode only (NEXUS_GEMINI_MOCK_RESPONSE): no live token is used. The user's mfd_pins.json and
    clipboard text are restored, and the HostService is restarted without the mock at the end.
    Writes 03_Local_Data_Vault/handoff/task12_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot  = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir = Join-Path $VaultRoot 'handoff'
$PinsPath   = Join-Path $VaultRoot 'config\mfd_pins.json'
$LatestPath = Join-Path $HandoffDir 'latest_dispatch.json'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe     = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath = Join-Path $HandoffDir 'task12_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$MockAnswer = 'Gate 12 vision mock'
$RunId      = Get-Date -Format 'HHmmss'
$PrivacyTitle = 'Vietcombank - Password Manager'
$CodeLine1  = 'var listener = new TcpListener(IPAddress.Any, 8888);'
$CodeLine2  = 'listener.Start();'
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$envNames = 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES', 'NEXUS_MEMORY_SLIMMING',
            'NEXUS_RELAXED_SLA', 'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE'
foreach ($v in $envNames) { if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue } }

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Result([string]$id, [bool]$pass, [string]$detail) {
    Write-Host ("{0}: {1} -> {2}" -f $id, $detail, $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}
function Test-HasUtf8Bom([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    $b = [System.IO.File]::ReadAllBytes($path)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}
function Send-PipeJson([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    $outer = Send-PipeLine ($req | ConvertTo-Json -Compress) | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}
function Send-CockpitCommand([string]$text) { return Send-PipeJson 'COCKPIT' $text }
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task12.ps1.'
    }
}
function Start-HostIfNeeded {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $HostExe | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline) { try { $null = Send-PipeJson 'SYNCSTATE' $null; break } catch { Start-Sleep -Milliseconds 300 } }
        Start-Sleep -Seconds 3
    }
    return (Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1)
}
# The mock lives in the HostService environment: restart it with (or without) NEXUS_GEMINI_MOCK_RESPONSE.
function Restart-Host([bool]$mock) {
    Stop-HostService
    if ($mock) { $env:NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer } elseif (Test-Path Env:NEXUS_GEMINI_MOCK_RESPONSE) { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE }
    $p = Start-HostIfNeeded
    if (-not $mock -and (Test-Path Env:NEXUS_GEMINI_MOCK_RESPONSE)) { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE }
    return $p
}
function Invoke-SelfTest([string]$scenario) {
    $path = Join-Path $HandoffDir "task12_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $($p.ExitCode))" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($w in @($data.slaWarnings)) {
        if ($null -ne $w) { Write-Host ("[SLA-WARN (FastRegression jitter)] {0}: {1} = {2} ms (SLA {3} ms)" -f $scenario, $w.metric, $w.measuredMs, $w.slaMs) -ForegroundColor Yellow }
    }
    return [ordered]@{ exitCode = $p.ExitCode; noBom = -not (Test-HasUtf8Bom $path); data = $data }
}
function Get-HostModuleNames([int]$processId) {
    try { return @((Get-Process -Id $processId -ErrorAction Stop).Modules | ForEach-Object { $_.ModuleName.ToLowerInvariant() }) } catch { return @() }
}
function Get-HudLabelMb($mem) {
    if ($null -ne $mem -and ([string]$mem.hudLabel) -match 'Private WS: ([0-9]+(\.[0-9]+)?) MB') { return [double]$Matches[1] }
    return $null
}
function Get-LastBurstStart {
    try { $b = (Send-PipeJson 'VISIONSTATE' $null).lastBurst; if ($b) { return [string]$b.StartedAtUtc } } catch { }
    return ''
}
<# TASK-12b Peak Sampler around one /img: reads Private WS every ~100 ms until the HostService reports a new burst (then one
   more read), at most 8 s. Returns the sampler result plus the in-process burst (start / peak / after LOH compaction). #>
function Measure-VisionPeak([int]$processId, [string]$command) {
    $script:PrevBurstStart = Get-LastBurstStart
    $m = Measure-NexusPeakPrivateWs -ProcessId $processId -DurationMs 8000 -IntervalMs 100 -Action { Send-CockpitCommand $command } `
        -Until { (Get-LastBurstStart) -ne $script:PrevBurstStart }
    $burst = $null
    try { $burst = (Send-PipeJson 'VISIONSTATE' $null).lastBurst } catch { }
    return [pscustomobject]@{ Sampler = $m; Burst = $burst; Command = $command; Ok = [bool]$m.Result.ok }
}
function Get-Percentile([double[]]$values, [double]$p) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $rank = ($p / 100.0) * ($sorted.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    return [math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($rank - $lo), 3)
}
function Get-FailedNames($checks) { return (($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ',') }
# Number of vision snapshots the Cockpit has shown so far (baseline: the host also shows snapshots written by selftests).
function Get-VisionCount {
    try { return [long](Send-PipeJson 'VISIONSTATE' $null).lastSync.count } catch { return -1 }
}
# Polls the Cockpit's last shown vision snapshot until one newer than $sinceCount satisfies $condition (scriptblock over lastSync).
function Wait-VisionState([scriptblock]$condition, [int]$timeoutSec, [long]$sinceCount) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $state = $null
    do {
        Start-Sleep -Milliseconds 100
        try { $state = Send-PipeJson 'VISIONSTATE' $null } catch { $state = $null }
        if ($null -ne $state -and ([long]$state.lastSync.count -gt $sinceCount) -and (& $condition $state.lastSync)) { return $state }
    } while ((Get-Date) -lt $deadline)
    return $null
}
# One McpTools process (its own environment) running initialize + the given tools/call requests; returns results by id.
function Invoke-Mcp([object[]]$calls, [hashtable]$extraEnv) {
    $saved = @{}
    foreach ($k in $extraEnv.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $extraEnv[$k]) }
    try {
        $OutputEncoding = $Utf8NoBom
        $req = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
        $id = 2
        foreach ($c in $calls) {
            $req += ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $c.name; arguments = $c.arguments } } | ConvertTo-Json -Compress -Depth 6)
            $id++
        }
        $lines = (($req -join "`n") + "`n") | & $McpExe
        $byId = @{}
        foreach ($ln in @($lines)) {
            if ([string]::IsNullOrWhiteSpace($ln)) { continue }
            try { $o = $ln | ConvertFrom-Json; if ($null -ne $o.id -and $null -ne $o.result) { $byId[[int]$o.id] = ([string]$o.result.content[0].text | ConvertFrom-Json) } } catch { }
        }
        return $byId
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

$script:FgApi = $false
try {
    if (-not ('NexusGate12.User32' -as [type])) {
        Add-Type -Namespace NexusGate12 -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'
    }
    $script:FgApi = $true
} catch { }
function Get-Foreground { if ($script:FgApi) { return ('0x{0:X}' -f [NexusGate12.User32]::GetForegroundWindow().ToInt64()) } else { return $null } }

$script:ClipApi = $false
try { Add-Type -AssemblyName System.Windows.Forms, System.Drawing; $script:ClipApi = ([System.Threading.Thread]::CurrentThread.ApartmentState -eq 'STA') } catch { }
# Puts the PNG on the clipboard as a bitmap, with the Win+V history opt-out so the test image is not kept in the history.
function Set-ClipboardImage([string]$path) {
    $img = [System.Drawing.Image]::FromFile($path)
    try {
        $do = New-Object System.Windows.Forms.DataObject
        $do.SetImage($img)
        $do.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-Object System.IO.MemoryStream(, [byte[]](0, 0, 0, 0))))
        [System.Windows.Forms.Clipboard]::SetDataObject($do, $true)
    } finally { $img.Dispose() }
}

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan
$pinsBackup = if (Test-Path $PinsPath) { [System.IO.File]::ReadAllBytes($PinsPath) } else { $null }
$latestBackup = Save-NexusLatestDispatch $HandoffDir   # TASK-12c (B1): put back in the final finally, after the regression chain
$clipboardBackup = $null
try { $clipboardBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }
$script:Restored = $false

function Restore-UserState {
    if ($script:Restored) { return }
    if ($null -ne $pinsBackup) { [System.IO.File]::WriteAllBytes($PinsPath, $pinsBackup) }
    elseif (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) { try { $null = Send-CockpitCommand '/unpin all' } catch { } }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    foreach ($v in 'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE') { if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue } }
    $script:Restored = $true
}

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, Gate 13 polish (MFD preview, Win+V opt-out)'
$buildCode = 0; $buildWarnings = 0; $buildErrors = 0
Stop-HostService
if (-not $SkipBuild) {
    $buildText = (& dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1 | Out-String)
    $buildCode = $LASTEXITCODE
    Write-Host $buildText.Trim()
    if ($buildText -match '(\d+)\s+Warning\(s\)') { $buildWarnings = [int]$Matches[1] }
    if ($buildText -match '(\d+)\s+Error\(s\)')   { $buildErrors   = [int]$Matches[1] }
}
$bom = Test-NexusBom $RepoRoot
foreach ($o in $bom.Offenders) { Write-Host "  BOM rule violated: $o" -ForegroundColor Red }
$vSelf = Invoke-SelfTest 'vision'
$vData = $vSelf.data
$hostProc = Restart-Host $true
# A current response with a 2-line csharp block, written the way McpTools would (ADR-14, no BOM).
$testAnswer = "Gate 12 MFD preview test`n``````csharp`n$CodeLine1`n$CodeLine2`n```````nXong."
$now = (Get-Date).ToUniversalTime().ToString('o')
$latestObj = [ordered]@{ event = 'ai_response'; source = 'Verify-Task12'; dispatchId = 1; status = 'PASSED'; retryCount = 0; evidenceNotes = ''
    timestampUtc = $now; updatedAtUtc = $now; taskSummary = 'Gate 12 MFD preview'; nexusMode = 'Micro'; squadId = 'Squad1'; squadName = 'Core .NET, Socket & DB'
    primaryAgent = [ordered]@{ id = 'verify'; name = 'Verify' }; qaAgent = [ordered]@{ id = '-'; name = '-' }; realityChecker = 'testing-reality-checker'
    aiResponse = $testAnswer; aiModel = 'verify'; turnCount = 1 }
[System.IO.File]::WriteAllText($LatestPath, ($latestObj | ConvertTo-Json -Depth 5), $Utf8NoBom)
Start-Sleep -Milliseconds 600
$null = Send-CockpitCommand '/unpin all'
$pinCode = Send-CockpitCommand '/pin #1'
$pinState = Send-PipeJson 'PINSTATE' $null
$slot1 = [string]@($pinState.slots)[0]
$slotLines = @($slot1 -split "`n")
$copy = Send-PipeJson 'COPYWIDGET' '1'
$e1Checks = [ordered]@{
    build           = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom         = [bool]$bom.Pass
    previewSelftest = [bool]$vData.checks.previewOk
    previewLive     = [bool]$pinCode.ok -and ($slotLines.Count -eq 2) -and ($slotLines[0] -like "*csharp*$CodeLine1*") -and ($slotLines[1].Trim() -eq $CodeLine2)
    optOutInSource  = [bool]$vData.checks.historyOptOutInSource
    optOutLive      = [bool]$copy.ok -and [bool]$copy.systemClipboard -and [bool]$copy.clipboardMatches -and [bool]$copy.historyExcludedOnWrite -and [bool]$copy.historyExcluded
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | MFD slot: '{5}' | clipboard opt-out: exclude={6} canIncludeInHistory={7} | failed=[{8}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, ($slotLines -join ' / '),
    $copy.historyFlags.ExcludeFromMonitorProcessing, $copy.historyFlags.CanIncludeInClipboardHistoryValue, (Get-FailedNames $e1Checks))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: JPEG <= 256 000 B from a 2K image, /img clipboard + --file on the live Cockpit'
$imageA = [string]$vData.images.imageA
$compressedSha = [string]$vData.compressed.sha256Hex
Write-Host ("  selftest: source {0:N0} B {1} -> JPEG {2:N0} B {3}x{4} q{5} ({6} attempts, decode {7} ms, encode {8} ms) sha256 {9} | pure noise -> {10:N0} B {11}x{12} q{13}" -f `
    [long]$vData.images.sourceBytesA, '2560x1440', [long]$vData.compressed.bytes, $vData.compressed.width, $vData.compressed.height, $vData.compressed.quality,
    $vData.compressed.attempts, $vData.compressed.decodeMs, $vData.compressed.encodeMs, $compressedSha,
    [long]$vData.compressedWorst.bytes, $vData.compressedWorst.width, $vData.compressedWorst.height, $vData.compressedWorst.quality)
$noImage = $null; $clipState = $null; $clipCmd = $null
if ($script:ClipApi) {
    Set-Clipboard -Value "Gate 12 text only $RunId"
    $noImage = Send-CockpitCommand "/img Gate 12 clipboard trống $RunId"
    Set-ClipboardImage $imageA
    $base = Get-VisionCount
    $clipCmd = Send-CockpitCommand "/img Gate 12 clipboard $RunId"
    $clipState = Wait-VisionState { param($s) $s.visionSource -eq 'clipboard' -and $s.eventName -eq 'ai_response' -and ([string]$s.aiResponsePrefix).StartsWith('[VISION-MOCK') } 30 $base
}
$base = Get-VisionCount
$fileCmd = Send-CockpitCommand ("/img --file `"{0}`" Gate 12 live {1}" -f $imageA, $RunId)
$fileState = Wait-VisionState { param($s) $s.visionSource -eq 'file' -and $s.eventName -eq 'ai_response' -and ([string]$s.aiResponsePrefix).StartsWith('[VISION-MOCK') } 30 $base
$clipSync = if ($clipState) { $clipState.lastSync } else { $null }
$fileSync = if ($fileState) { $fileState.lastSync } else { $null }
# TASK-12b Transient Burst Envelope: Private WS every ~100 ms while one 2K image is decoded and compressed (soft 120 / 200 MB).
$hostProc = Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1
$peakClip = $null
if ($script:ClipApi) {
    Set-ClipboardImage $imageA
    $peakClip = Measure-VisionPeak $hostProc.Id "/img --fresh Gate 12b peak clipboard $RunId"
}
$peakFile = Measure-VisionPeak $hostProc.Id ("/img --fresh --file `"{0}`" Gate 12b peak file {1}" -f $imageA, $RunId)
foreach ($pk in @($peakClip, $peakFile)) {
    if ($null -eq $pk) { continue }
    Write-Host ("  peak [{0}]: baseline {1} MB -> peak {2} MB ({3} reads, {4}, {5}) | in-process: start {6} -> peak {7} -> after LOH compact {8} MB, compact {9} ms, clipboard hold {10} ms | worker {11}: {12} ms, exit {13}, its peak WS {14} MB {15}" -f `
        $pk.Command, $pk.Sampler.BaselineMb, $pk.Sampler.PeakMb, $pk.Sampler.Samples, $pk.Sampler.Envelope, $pk.Sampler.Source,
        $pk.Burst.StartMb, $pk.Burst.PeakMb, $pk.Burst.AfterCompactMb, $pk.Burst.CompactMs, $pk.Burst.ClipboardHoldMs,
        $pk.Burst.WorkerMode, $pk.Burst.WorkerMs, $pk.Burst.WorkerExitCode, $pk.Burst.WorkerPeakWorkingSetMb, $pk.Burst.WorkerError)
}
$peakValues = @(@($peakClip, $peakFile) | Where-Object { $null -ne $_ -and $null -ne $_.Sampler.PeakMb } | ForEach-Object { [double]$_.Sampler.PeakMb })
$peakMax = if ($peakValues.Count -gt 0) { ($peakValues | Measure-Object -Maximum).Maximum } else { $null }
if ($null -ne $peakMax -and $peakMax -gt 120) { Write-Host ("  [PEAK-WARN] transient peak {0} MB > 120 MB soft warning (not a gate)" -f $peakMax) -ForegroundColor Yellow }
$e2Checks = [ordered]@{
    selftest        = ($vSelf.exitCode -eq 0) -and $vSelf.noBom -and [bool]$vData.checks.compressOk -and [bool]$vData.checks.budgetLoopOk -and [bool]$vData.checks.flowOk
    noImageInfo     = (-not $script:ClipApi) -or (([string]$noImage.message).StartsWith('[VISION INFO] No image found in Clipboard'))
    clipboardImage  = (-not $script:ClipApi) -or ([bool]$clipCmd.ok -and $null -ne $clipSync -and [int]$clipSync.visionImageBytes -gt 0 -and [int]$clipSync.visionImageBytes -le 256000 -and
                       ([string]$clipSync.aiResponsePrefix) -like ("*bytes={0} sha256={1}*" -f $clipSync.visionImageBytes, ([string]$clipSync.visionImageSha256).Substring(0, [math]::Min(12, ([string]$clipSync.visionImageSha256).Length))))
    liveFileSameSha = [bool]$fileCmd.ok -and $null -ne $fileSync -and ([string]$fileSync.visionImageSha256 -eq $compressedSha) -and ([int]$fileSync.visionImageBytes -eq [int]$vData.compressed.bytes)
    zeroCopyLimit   = [bool]$vData.checks.zeroCopyOk -and [bool]$vData.checks.sourceLimitOk
    peakRecorded    = ($null -ne $peakMax) -and $peakFile.Ok -and ($null -ne $peakFile.Burst) -and [bool]$peakFile.Burst.Compacted -and
                      ((-not $script:ClipApi) -or ($null -ne $peakClip -and $peakClip.Ok -and $null -ne $peakClip.Burst -and [bool]$peakClip.Burst.Compacted))
    # TASK-12c: the image step ran in McpTools --vision-worker; HostService peak while /img clipboard runs <= 70 MB
    workerProcess   = ([string]$peakFile.Burst.WorkerMode -eq 'process') -and ((-not $script:ClipApi) -or ([string]$peakClip.Burst.WorkerMode -eq 'process'))
    peakClipUnder70 = (-not $script:ClipApi) -or ($null -ne $peakClip -and $null -ne $peakClip.Sampler.PeakMb -and [double]$peakClip.Sampler.PeakMb -le 70.0)
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("JPEG {0:N0} B (<= 256 000) {1}x{2} from {3:N0} B | mock tag '{4}' | clipboard (STA={5}): no-image '{6}', image {7:N0} B sha {8} | live --file sha {9} (same as selftest: {10}) | peak {12} MB (Gate 12 run: ~145) | failed=[{11}]" -f `
    [long]$vData.compressed.bytes, $vData.compressed.width, $vData.compressed.height, [long]$vData.images.sourceBytesA, $vData.expectedTag, $script:ClipApi,
    $noImage.message, [long]$clipSync.visionImageBytes, $clipSync.visionImageSha256, $fileSync.visionImageSha256, ([string]$fileSync.visionImageSha256 -eq $compressedSha),
    (Get-FailedNames $e2Checks), $peakMax)
if (-not $script:ClipApi) { Write-Host '  Clipboard image step skipped: this PowerShell is not STA (run Windows PowerShell 5.1 without -MTA).' -ForegroundColor Yellow }

function Get-ShortSha([object]$Value) {
    $text = [string]$Value
    return $text.Substring(0, [math]::Min(12, $text.Length))
}

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: Vision cache hits (25 x, 0 tokens; selftest P50 <= 2.0 / P95 <= 2.5 ms, live P50 <= 2.0 / P95 <= 3.0 ms), pixel / question change = miss'
$liveTimes = @(); $liveHits = 0; $liveZero = 0
$warmLive = Send-CockpitCommand ("/img --file `"{0}`" Gate 12 live {1}" -f $imageA, $RunId)   # first hit in this host: JIT, not timed
for ($i = 0; $i -lt 25; $i++) {
    $r = Send-CockpitCommand ("/img --file `"{0}`" Gate 12 live {1}" -f $imageA, $RunId)
    $liveTimes += [double]$r.elapsedMs
    if ([bool]$r.cacheHit) { $liveHits++ }
    if ($null -ne $r.totalTokens -and [int]$r.totalTokens -eq 0) { $liveZero++ }
}
$liveP50 = Get-Percentile $liveTimes 50
$liveP95 = Get-Percentile $liveTimes 95
$e3Checks = [ordered]@{
    selftestHits    = [bool]$vData.checks.hitsOk
    selftestLatency = [bool]$vData.checks.hitLatencyOk
    pixelMiss       = [bool]$vData.checks.pixelMissOk
    downscalePixel  = [bool]$vData.checks.downscaleConsistentOk
    questionMiss    = [bool]$vData.checks.promptMissOk
    freshBypass     = [bool]$vData.checks.freshOk
    liveHits        = [bool]$warmLive.cacheHit -and ($liveHits -eq 25) -and ($liveZero -eq 25)
    liveLatency     = ($null -ne $liveP50) -and ($liveP50 -le 2.0) -and ($liveP95 -le 3.0)
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("selftest 25 hits={0} P50 {1} / P95 {2} ms | live 25 hits={3}, 0-token={4}, P50 {5} / P95 {6} ms | pixel 1280x720 sha {7} != {8} | 2K pixel sameJpeg={9} hit={10} | failed=[{11}]" -f `
    $vData.hits.hits, $vData.hits.latency.p50, $vData.hits.latency.p95, $liveHits, $liveZero, $liveP50, $liveP95,
    (Get-ShortSha $vData.pixelMiss.changedSha256), (Get-ShortSha $vData.pixelMiss.originalSha256),
    $vData.downscalePixel.sameJpegBytes, $vData.downscalePixel.cacheHit,
    (Get-FailedNames $e3Checks))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: Combat-Mode Shield, Privacy Window Guard, MCP analyze_visual_input -> Cockpit < 300 ms'
$gSelf = Invoke-SelfTest 'vision-guard'
$gData = $gSelf.data
$hostProc = Start-HostIfNeeded
$mcpCombat = Invoke-Mcp @(@{ name = 'analyze_visual_input'; arguments = [ordered]@{ source = 'snap'; prompt = "Gate 12 MCP combat $RunId" } }) @{ NEXUS_SIMULATE_COMBAT_MODE = '1'; NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
$mcpPrivacy = Invoke-Mcp @(@{ name = 'analyze_visual_input'; arguments = [ordered]@{ source = 'snap'; prompt = "Gate 12 MCP privacy $RunId" } }) @{ NEXUS_SIMULATE_WINDOW_TITLE = $PrivacyTitle; NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 300
$fgBefore = Get-Foreground
$mcpSafe = Invoke-Mcp @(@{ name = 'analyze_visual_input'; arguments = [ordered]@{ source = 'snap'; prompt = "Gate 12 MCP safe snap $RunId" } }) @{ NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
$combatRes = $mcpCombat[2]; $privacyRes = $mcpPrivacy[2]; $safeRes = $mcpSafe[2]
$safeId = if ($safeRes) { [long]$safeRes.dispatchId } else { -1 }
$mcpState = Wait-VisionState { param($s) $s.source -eq 'McpTools' -and [long]$s.dispatchId -eq $safeId -and $s.eventName -eq 'ai_response' } 5 -1
$fgAfter = Get-Foreground
$mcpSync = if ($mcpState) { $mcpState.lastSync } else { $null }
$base = Get-VisionCount
$liveSnap = Send-CockpitCommand "/snap Gate 12 Cockpit snap $RunId"
$liveSnapState = if ([bool]$liveSnap.ok) { Wait-VisionState { param($s) $s.visionSource -eq 'snap' -and $s.source -ne 'McpTools' -and $s.eventName -eq 'ai_response' } 30 $base } else { $null }
$e4Checks = [ordered]@{
    selftest        = ($gSelf.exitCode -eq 0) -and $gSelf.noBom -and [bool]$gData.pass
    mcpCombat       = ($null -ne $combatRes) -and [bool]$combatRes.blocked -and ($combatRes.blockedBy -eq 'combat') -and ([string]$combatRes.message).StartsWith('[COMBAT SHIELD]')
    mcpPrivacy      = ($null -ne $privacyRes) -and [bool]$privacyRes.blocked -and ($privacyRes.blockedBy -eq 'privacy') -and ([string]$privacyRes.message).StartsWith('[PRIVACY GUARD]')
    mcpSafe         = ($null -ne $safeRes) -and [bool]$safeRes.ok -and [bool]$safeRes.hasVisionAttachment -and ([string]$safeRes.aiResponse).StartsWith('[VISION-MOCK') -and [int]$safeRes.visionImageBytes -le 256000
    cockpitUnder300 = ($null -ne $mcpSync) -and ($null -ne $mcpSync.latencyMs) -and ([double]$mcpSync.latencyMs -lt 300)
    focusKept       = (-not $script:FgApi) -or ($fgBefore -eq $fgAfter)
    liveCockpitSnap = [bool]$liveSnap.ok -and ($null -ne $liveSnapState) -and ([string]$liveSnapState.lastSync.aiResponsePrefix).StartsWith('[VISION-MOCK')
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("selftest combat P95 {0} ms (SLA 2), captures during blocks {1}, privacy '{2}', safe snap {3:N0} B | MCP combat '{4}' | MCP privacy '{5}' | MCP safe #{6} {7:N0} B -> Cockpit {8} ms | foreground {9} -> {10} | Cockpit /snap: {11} | failed=[{12}]" -f `
    $gData.combat.latency.p95, $gData.combat.capturesDuringBlocks, $gData.privacy.message, [long]$gData.safe.visionImageBytes,
    $combatRes.message, $privacyRes.message, $safeId, [long]$safeRes.visionImageBytes, $mcpSync.latencyMs, $fgBefore, $fgAfter, $liveSnap.message,
    (Get-FailedNames $e4Checks))
if (-not [bool]$gData.checks.safeOk) { Write-Host ("  Safe /snap in the selftest: {0}" -f $gData.safe.message) -ForegroundColor Yellow }
if ($safeRes -and -not [bool]$safeRes.ok) { Write-Host ("  MCP safe snap: {0}" -f $safeRes.message) -ForegroundColor Yellow }

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45.0 MB (gate 52.0) after the 2K image load, idle CPU < 0.5 %, fast regression, git, CI'
$memSelf = Invoke-SelfTest 'vision-mem'
$memData = $memSelf.data
Write-Host ("  selftest vision-mem (headless): baseline {0} -> settled {1} MB (growth {2}; target <= {3}: {7}) | after each LOH compaction: {8} MB (last-3 spread {9} <= {10}) | load peak {4} MB ({5}) | compactions {6}" -f `
    $memData.baseline.MedianMb, $memData.settled.MedianMb, $memData.settledDeltaMb, $memData.settledDeltaMaxMb,
    $memData.peak.loadPeakMb, $memData.peak.envelope, $memData.visionCompacts, $memData.settledWithinTarget,
    (@($memData.afterCompactMb) -join ', '), $memData.plateauSpreadMb, $memData.plateauMaxSpreadMb)
# TASK-12c: a fresh mock HostService, then 6 x /img --fresh on the 2K image one after the other (each waits for its burst).
$hostProc = Restart-Host $true
Start-Sleep -Seconds 2
$modulesStart = Get-HostModuleNames $hostProc.Id
$loadRuns = @()
for ($i = 0; $i -lt 6; $i++) {
    $loadRuns += Measure-VisionPeak $hostProc.Id ("/img --fresh --file `"{0}`" Gate 12 RAM load {1} {2}" -f $imageA, $i, $RunId)
}
$modulesAfter = Get-HostModuleNames $hostProc.Id
$gdiplusAtStart = $modulesStart -contains 'gdiplus.dll'
$gdiplusAfter = $modulesAfter -contains 'gdiplus.dll'
$loadModes = @($loadRuns | ForEach-Object { [string]$_.Burst.WorkerMode })
$loadAfterCompact = @($loadRuns | ForEach-Object { $_.Burst.AfterCompactMb })
Write-Host ("  6 x /img 2K: worker modes [{0}] | HostService after each LOH compaction: {1} MB | peaks: {2} MB | gdiplus.dll at start={3}, after={4}" -f `
    ($loadModes -join ','), ($loadAfterCompact -join ', '), (@($loadRuns | ForEach-Object { $_.Sampler.PeakMb }) -join ', '), $gdiplusAtStart, $gdiplusAfter)
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 8000
# TASK-12b: Settled Median Mode of NexusVerify.Common.ps1 (replaces the run-3 patch 3a7b00f): reads start once the idle trim
# is >= 10 s old (MEM is read-only, it does not re-arm the trim), 8 reads 250 ms apart, the median is the Gate value.
$idleRead = Get-NexusSettledPrivateWs -ProcessId $hostProc.Id -LastTrimUtc {
    $m = $null; try { $m = Send-PipeJson 'MEM' $null } catch { }
    if ($m -and $m.lastTrim) { $m.lastTrim.AtUtc } else { $null }
}
$idleMb = $idleRead.MedianMb
$idleReads = $idleRead.Samples
$trimAgeSec = $idleRead.TrimAgeSeconds
Write-Host ("  idle Private WS: median {0} MB (min {1}, max {2}; trim age {3} s; {4}): {5}" -f `
    $idleRead.MedianMb, $idleRead.MinMb, $idleRead.MaxMb, $idleRead.TrimAgeSeconds, $idleRead.Source, (@($idleRead.Samples) -join ', '))
$memJson = $null
try { $memJson = Send-PipeJson 'MEM' $null } catch { }
$immediateMb = Get-NexusPrivateWsMb $hostProc.Id   # Immediate Mode right after the HUD label was computed
$hudMb = Get-HudLabelMb $memJson
$hudDiff = if ($null -ne $hudMb -and $null -ne $immediateMb) { [math]::Round([math]::Abs($hudMb - $immediateMb), 2) } else { $null }
$cpuSeries = @()
for ($i = 0; $i -lt 10; $i++) {
    $hostProc.Refresh(); $c0 = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 1
    $hostProc.Refresh()
    $cpuSeries += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
}
$idleCpu = [math]::Round(($cpuSeries | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
Restore-UserState
$null = Restart-Host $false   # the regression chain and the user get a HostService without the Gemini mock
$fastLines = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-NexusRegression.ps1') -FastRegression -SkipBuild |
    ForEach-Object { Write-Host $_; [string]$_ })
$fastExit = $LASTEXITCODE
$summaryLine = @($fastLines | Where-Object { $_ -match '^\[SLA-WARN SUMMARY\] Count=\d+ \| Details=' }) | Select-Object -Last 1
$fastSummaryPath = Join-Path $HandoffDir 'regression_fast.json'
$fastSummary = if (Test-Path $fastSummaryPath) { Get-Content $fastSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task12'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable or repository private: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    selftestVisionMem = ($memSelf.exitCode -eq 0) -and $memSelf.noBom -and [bool]$memData.pass
    sixImagesInWorker = ($loadRuns.Count -eq 6) -and (@($loadModes | Where-Object { $_ -ne 'process' }).Count -eq 0) -and (@($loadRuns | Where-Object { -not $_.Ok }).Count -eq 0)
    noGdiplusFromVision = -not ($gdiplusAfter -and -not $gdiplusAtStart)
    hudWithinHalfMb  = ($null -ne $hudDiff) -and ($hudDiff -le 0.5)
    privateWsUnder45 = ($null -ne $idleMb) -and ($idleMb -le 45.0)   # TASK-12b target (Phase 3 hard gate 52.0)
    idleCpuUnderHalf = ($idleCpu -lt 0.5)
    fastRegression   = ($fastExit -eq 0) -and ($null -ne $fastSummary) -and [bool]$fastSummary.pass -and ([double]$fastSummary.seconds -lt 90) -and ($null -ne $summaryLine)
    gitUnchanged     = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Host ("  HUD label {0} MB vs immediate read {1} MB (diff {2}, <= 0.5)" -f $hudMb, $immediateMb, $hudDiff)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45.0; gate 52.0; HUD {1}) | idle CPU {2} % | fast {3} s, {4} | git same={5} {6} | CI: {7} | failed=[{8}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, $fastSummary.seconds, $summaryLine, $gitEnd.Same, ($gitEnd.Reasons -join '; '),
    $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))
if (-not ($ci.checked -and $ci.conclusion -eq 'success')) { Write-Host '  Confirm the green CI run in the GitHub Actions tab.' -ForegroundColor Yellow }

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-12'; gate = 'Gate 12'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart
    E1_BuildBomPolish = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; preview = $vData.preview; liveSlot = $slotLines; copy = $copy }
    E2_Compression = [ordered]@{ pass = $e2Pass; checks = $e2Checks; images = $vData.images; compressed = $vData.compressed; compressedWorst = $vData.compressedWorst; expectedTag = $vData.expectedTag; first = $vData.first; latestAfterFirst = $vData.latestAfterFirst; clipboardSta = $script:ClipApi; noImage = $noImage.message; clipboard = $clipSync; liveFile = $fileSync; zeroCopy = $vData.zeroCopy; sourceLimit = $vData.sourceLimit; peakClipboard = $peakClip; peakFile = $peakFile; peakMaxMb = $peakMax }
    E3_VisionCache = [ordered]@{ pass = $e3Pass; checks = $e3Checks; selftest = $vData.hits; pixelMiss = $vData.pixelMiss; downscalePixel = $vData.downscalePixel; promptMiss = $vData.promptMiss; liveTimesMs = $liveTimes; liveP50 = $liveP50; liveP95 = $liveP95; liveHits = $liveHits; liveZeroToken = $liveZero }
    E4_Shields = [ordered]@{ pass = $e4Pass; checks = $e4Checks; selftest = $gData; mcpCombat = $combatRes; mcpPrivacy = $privacyRes; mcpSafe = $safeRes; cockpitSync = $mcpSync; foregroundBefore = $fgBefore; foregroundAfter = $fgAfter; liveSnap = $liveSnap.message; liveSnapSync = $(if ($liveSnapState) { $liveSnapState.lastSync } else { $null }) }
    E5_MemoryCpuRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; visionMem = $memData; loadRuns = $loadRuns; gdiplusAtStart = $gdiplusAtStart; gdiplusAfter = $gdiplusAfter; hudMb = $hudMb; immediateMb = $immediateMb; hudDiffMb = $hudDiff; idleReadsMb = $idleReads; trimAgeSecAtFirstRead = $trimAgeSec; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; fast = $fastSummary; summaryLine = $summaryLine; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
Write-Host "`nGate 12 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored (B1).' }
    foreach ($v in 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE') { if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue } }
}
