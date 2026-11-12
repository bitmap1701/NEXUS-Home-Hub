#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 18 automated verification (TASK-18, ADR-71: local STT for /ptt - SAPI en-US dictation in a short-lived
    McpTools --stt-worker child, VAD = False skips it, recognized text goes into the Cockpit command box, 0 token).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate, ADR-70 "Chốt v0.17.2" and ADR-71 in docs/ADR_REGISTRY.md, selftest ptt-stt.
    E2  selftest ptt-stt: silence -> VAD False, skip <= 5,0 ms; a SAPI voice sample -> VAD True -> non-empty text <= 1500 ms,
        PCM ledger 0 B, selftest process growth <= 2,5 MB (the recognizer lives in the child); 0 Gemini call.
    E3  Live HostService: /ptt status over the pipe <= 300 ms, /ptt test 1 answers at once and the UI thread keeps answering
        UISTATE during the session; NEXUS_STT_SIMULATE_MISSING=lang -> fallback (selftest c: <= 300 ms, 0 exception) and
        /ptt status names it.
    E4  WAKE 40 -> 330 px N = 10: P50 <= 10,0 / P95 <= 16,0 ms.
    E5  Settled idle Private WS <= 45,0 MB ([SLA-WARN] <= 50,0 with GC heap <= 10,5; aim <= 41,5), idle CPU 0,0 %, regression
        chain Verify-Task17b (-> 17a -> 17 -> 16c -> 15 -> 16b -> ... 03, -SkipLive) with 0 live Gemini calls, git guard, CI.
    latest_dispatch.json, cockpit_ui.json, nexus_config.json and the clipboard text are restored. Writes
    03_Local_Data_Vault/handoff/task18_verification.json (UTF-8 without BOM) and reports through MCP notify_shift_complete.
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5   # development loop: Verify-Task17b -FastE5 instead of the full chain
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot  = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir = Join-Path $VaultRoot 'handoff'
$ConfigDir  = Join-Path $VaultRoot 'config'
$UiPath     = Join-Path $ConfigDir 'cockpit_ui.json'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe     = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath = Join-Path $HandoffDir 'task18_verification.json'
$ConfigPath = Join-Path $ConfigDir 'nexus_config.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$MockAnswer = "**Gate 18** mock: dùng ``Span<T>``.`n`n- ý một`n- ý hai`n`n``````csharp`nvar x = 1;`n```````nXong."
$LocalBadge = 'cục bộ · 0 token'
$PortMaxMs  = 15.0
$PingCutMs  = 300.0
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$envNames = 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_MOCK_DELAY_MS', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES',
            'NEXUS_RELAXED_SLA', 'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE', 'NEXUS_UI_AUTOCOLLAPSE_MS'
$savedUserEnv = @{}
foreach ($v in $envNames) {
    if (Test-Path "Env:$v") { $savedUserEnv[$v] = (Get-Item "Env:$v").Value; Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Result([string]$id, [bool]$pass, [string]$detail) {
    Write-Host ("{0}: {1} -> {2}" -f $id, $detail, $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}
function Test-HasUtf8Bom([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    $b = [System.IO.File]::ReadAllBytes($path)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
# Same pipe client as Verify-Task16c (e8bd63d): the reply is awaited with Task.Wait (pumps on this STA thread, so a Cockpit
# clipboard write can never deadlock with this script) and a command that does not answer in 30 s fails the run by name.
$script:PipeReplyTimeoutMs = 30000
function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($script:PipeReplyTimeoutMs)) {
            throw ("HostService did not answer the pipe command {0} within {1} s (UI thread busy or hung)" -f $line, ($script:PipeReplyTimeoutMs / 1000))
        }
        return $task.Result
    } finally { $client.Dispose() }
}
function Send-PipeRaw([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    return [string](Send-PipeLine ($req | ConvertTo-Json -Compress) | ConvertFrom-Json).result
}
function Send-PipeJson([string]$cmd, [string]$text) { return ((Send-PipeRaw $cmd $text) | ConvertFrom-Json) }
function Get-UiState { return Send-PipeJson 'UISTATE' $null }
function Set-Ui([string]$action) { return Send-PipeJson 'UI' $action }
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Task Manager), then re-run Verify-Task18.ps1.'
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
function Restart-Host([bool]$mock) {
    Stop-HostService
    if ($mock) { $env:NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
    try { $p = Start-HostIfNeeded }
    finally { if (Test-Path Env:NEXUS_GEMINI_MOCK_RESPONSE) { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE } }
    return $p
}
function Invoke-SelfTest([string]$scenario, [string]$prefix) {
    $path = Join-Path $HandoffDir "${prefix}_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $($p.ExitCode))" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    return [ordered]@{ exitCode = $p.ExitCode; noBom = -not (Test-HasUtf8Bom $path); data = $data }
}
function Invoke-Mcp([object[]]$calls) {
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
}
function Get-FailedNames($checks) { return (($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ',') }
function Wait-Ui([scriptblock]$condition, [int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $s = Get-UiState
        if (& $condition $s) { return $s }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    return $s
}
function Get-ThreadItems($ui) { return @($ui.visual.thread.items) }

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan
$latestBackup = Save-NexusLatestDispatch $HandoffDir
$uiBackup = if (Test-Path $UiPath) { [System.IO.File]::ReadAllBytes($UiPath) } else { $null }
$configBackup = if (Test-Path $ConfigPath) { [System.IO.File]::ReadAllBytes($ConfigPath) } else { $null }
$clipboardBackup = $null
try { $clipboardBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }
$script:Restored = $false
function Restore-UserState {
    if ($script:Restored) { return }
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $configBackup) {
        [System.IO.File]::WriteAllBytes($ConfigPath, $configBackup)
        if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) { try { $null = Send-PipeJson 'COCKPIT' '/reload' } catch { } }
    }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    $script:Restored = $true
}


# Gate 15 run 2/3 convention (as Verify-Task16c): every WAKE sample waits for 40 px + 50 ms outside the timer.
function Get-WakeSample {
    $null = Set-Ui 'collapsed'
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    Start-Sleep -Milliseconds 300
    $pre = Wait-Ui { param($u) ([string]$u.level -eq 'collapsed') -and ([double]$u.height -le 40.5) } 1000
    $settled = ($null -ne $pre) -and ([string]$pre.level -eq 'collapsed') -and ([double]$pre.height -le 40.5)
    Start-Sleep -Milliseconds 50
    $null = Send-PipeRaw 'COMMANDMODE' $null
    Start-Sleep -Milliseconds 250
    $w = (Get-UiState).lastWake
    return [ordered]@{ ms = $(if ($w) { [double]$w.Ms } else { $null }); from = $w.From; to = $w.To; focused = $w.InputFocused; settledAt40 = $settled
        windowHeightMs = $(if ($w -and $w.LevelParts) { $w.LevelParts.WindowHeightMs } else { $null }) }
}
function Get-Percentile([double[]]$values, [double]$p) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $rank = ($p / 100.0) * ($sorted.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    return [math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($rank - $lo), 3)
}


function Get-PipeMs([scriptblock]$call) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $v = & $call; $sw.Stop()
    return [ordered]@{ value = $v; ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
}

try {
if ($null -eq $configBackup) { throw "nexus_config.json not found at $ConfigPath (start the HostService once to create it)." }
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, ADR-70 locked v0.17.2 + ADR-71 registered, selftest ptt-stt'
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
$adrText = Get-Content (Join-Path $RepoRoot 'docs\ADR_REGISTRY.md') -Raw -Encoding UTF8
$adr70Locked = $adrText -match '(?m)^\|\s*ADR-70\s*\|.*Chốt `?v0\.17\.2'
$adr71 = $adrText -match '(?m)^\|\s*ADR-71\s*\|'
$self = Invoke-SelfTest 'ptt-stt' 'task18'
$sData = $self.data
$e1Checks = [ordered]@{
    build       = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom     = [bool]$bom.Pass
    adr70Locked = [bool]$adr70Locked
    adr71       = [bool]$adr71
    selftest    = ($self.exitCode -eq 0) -and $self.noBom -and [bool]$sData.pass
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | ADR-70 locked {5}, ADR-71 {6} | selftest ptt-stt pass={7} failed=[{8}] | failed=[{9}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $adr70Locked, $adr71, $sData.pass, (@($sData.failed) -join ','), (Get-FailedNames $e1Checks))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: ptt-stt - silence skip <= 5 ms; voice sample -> text <= 1500 ms, PCM 0 B, growth <= 2,5 MB'
$c = $sData.checks
$sp = $sData.speech
$e2Checks = [ordered]@{
    silenceSkip   = [bool]$c.'a.silenceNoVad' -and [bool]$c.'a.skipUnder5ms' -and [bool]$c.'a.ledgerZero'
    onPc          = ($sData.runner -eq 'pc') -and [bool]$sData.synth.ok
    speechText    = [bool]$c.'b.speechVad' -and [bool]$c.'b.textNotEmpty' -and [bool]$c.'b.under1500ms' -and -not [bool]$sp.runnerWithoutSapi
    ledgerZero    = [bool]$c.'b.ledgerZero'
    growthUnder25 = [bool]$c.'b.hostGrowthUnder2_5Mb'
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("silence skip {0} ms | voice {1} ({2} s) -> '{3}' in {4} ms (worker load {5} ms, recognize {6} ms; {7}) | PCM {8} B | growth {9} MB | failed=[{10}]" -f `
    $sData.silence.TotalMs, $sData.synth.voice, $sData.synth.seconds,
    $sp.text, $sp.stt.TotalMs, $sp.stt.WorkerLoadMs, $sp.stt.WorkerWorkMs, $sp.stt.Engine, $sp.OutstandingBytesAfter, $sp.growthMb, (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: /ptt status <= 300 ms, /ptt test 1 never blocks the UI, simulated missing language pack -> fallback'
$hostProc = Restart-Host $true
$status = Get-PipeMs { Send-PipeJson 'COCKPIT' '/ptt status' }
$test = Get-PipeMs { Send-PipeJson 'COCKPIT' '/ptt test 1' }
$uiPings = @()
for ($i = 0; $i -lt 8; $i++) { $p = Get-PipeMs { Get-UiState }; $uiPings += $p.ms; Start-Sleep -Milliseconds 150 }
$done = Wait-Ui { param($u) ($null -ne $u.task17.mic.lastPtt) -and (-not [bool]$u.task17.mic.busy) } 8000
$lastPtt = $done.task17.mic.lastPtt
$env:NEXUS_STT_SIMULATE_MISSING = 'lang'
try { $hostProc = Restart-Host $true } finally { Remove-Item Env:NEXUS_STT_SIMULATE_MISSING -ErrorAction SilentlyContinue }
$simStatus = Get-PipeMs { Send-PipeJson 'COCKPIT' '/ptt status' }
$fb = @($sData.fallbacks)
$e3Checks = [ordered]@{
    statusUnder300   = [bool]$status.value.ok -and ($status.ms -le 300) -and ([string]$status.value.message -like '*STT*')
    testAnswersAtOnce = [bool]$test.value.ok -and ($test.ms -le 300)
    uiNotBlocked     = (@($uiPings | Where-Object { $_ -gt 300 }).Count -eq 0)
    sessionDone      = ($null -ne $lastPtt) -and ([string]$lastPtt.message -like '*bộ đệm còn giữ 0 B*')
    simulatedStatus  = ($simStatus.ms -le 300) -and ([string]$simStatus.value.message -like '*giả lập thiếu: lang*')
    fallbackSelftest = ($fb.Count -eq 2) -and (@($fb | Where-Object { [double]$_.stt.TotalMs -gt 300 -or $_.stt.Outcome -ne 'fallback' }).Count -eq 0)
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("/ptt status {0} ms | /ptt test 1 ack {1} ms | UISTATE during session max {2} ms | session: {3} | simulated lang: status {4} ms, selftest fallback {5} ms | failed=[{6}]" -f `
    $status.ms, $test.ms, (($uiPings | Measure-Object -Maximum).Maximum), $lastPtt.message, $simStatus.ms, (($fb | ForEach-Object { $_.stt.TotalMs }) -join ' / '), (Get-FailedNames $e3Checks))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: WAKE 40 -> 330 px N = 10, P50 <= 10,0 / P95 <= 16,0 ms'
$hostProc = Restart-Host $true
$warmup = Get-WakeSample
$wakes = @()
for ($i = 0; $i -lt 10; $i++) { $wakes += Get-WakeSample }
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$wakeMs = @($wakes | Where-Object { $null -ne $_.ms } | ForEach-Object { [double]$_.ms })
$wakeP50 = Get-Percentile $wakeMs 50
$wakeP95 = Get-Percentile $wakeMs 95
$e4Checks = [ordered]@{
    wakeP50 = ($null -ne $wakeP50) -and ($wakeP50 -le 10.0) -and ($wakeMs.Count -eq 10)
    wakeP95 = ($null -ne $wakeP95) -and ($wakeP95 -le 16.0)
    focused = (@($wakes | Where-Object { -not [bool]$_.focused }).Count -eq 0)
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("WAKE P50 {0} / P95 {1} ms ({2}) | failed=[{3}]" -f $wakeP50, $wakeP95, (($wakeMs | ForEach-Object { '{0:N2}' -f $_ }) -join ' / '), (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45,0 MB (aim <= 41,5; [SLA-WARN] <= 50,0 with GC heap <= 10,5), idle CPU 0,0 %, regression Verify-Task17b (-> 17a -> 17 ... 03, -SkipLive), git, CI'
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 8000
$idleRead = Get-NexusSettledPrivateWs -ProcessId $hostProc.Id -LastTrimUtc {
    $mm = $null; try { $mm = Send-PipeJson 'MEM' $null } catch { }
    if ($mm -and $mm.lastTrim) { $mm.lastTrim.AtUtc } else { $null }
}
$idleMb = $idleRead.MedianMb
$cpuSeries = @()
for ($i = 0; $i -lt 10; $i++) {
    $hostProc.Refresh(); $c0 = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 1
    $hostProc.Refresh()
    $cpuSeries += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
}
$idleCpu = [math]::Round(($cpuSeries | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
$memJson = $null; try { $memJson = Send-PipeJson 'MEM' $null } catch { }
$idleUi = $null; try { $idleUi = Get-UiState } catch { }
Restore-UserState
if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored before the regression chain.' }
Stop-HostService
$chainArgs = @('-SkipBuild'); if ($FastE5) { $chainArgs += '-FastE5' }
$chainStartUtc = [DateTime]::UtcNow
$swChain = [System.Diagnostics.Stopwatch]::StartNew()
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task17b.ps1') @chainArgs | Out-Host
$chainExit = $LASTEXITCODE
$chainSeconds = [math]::Round($swChain.Elapsed.TotalSeconds, 1)
$liveAudit = @()
foreach ($n in @('task07', 'task09', 'task10', 'task11')) {
    $p = Join-Path $HandoffDir "${n}_verification.json"
    $fresh = (Test-Path $p) -and ((Get-Item $p).LastWriteTimeUtc -ge $chainStartUtc)
    $j = if ($fresh) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $hasFields = ($null -ne $j) -and (@($j.PSObject.Properties.Name) -contains 'skipLive') -and (@($j.PSObject.Properties.Name) -contains 'liveCallAttempted')
    $liveAudit += [ordered]@{ report = $n; ranInChain = $fresh; hasFields = $hasFields
        skipLive = $(if ($hasFields) { [bool]$j.skipLive } else { $null }); liveCallAttempted = $(if ($hasFields) { [bool]$j.liveCallAttempted } else { $null }) }
}
$liveRequired = if ($FastE5) { @('task11') } else { @('task07', 'task09', 'task10', 'task11') }
$liveCalls = @($liveAudit | Where-Object { $_.ranInChain -and (-not $_.hasFields -or $_.liveCallAttempted -or -not $_.skipLive) })
$liveMissing = @($liveAudit | Where-Object { ($liveRequired -contains $_.report) -and -not $_.ranInChain })
$chainReports = [ordered]@{}
$chainRam = [ordered]@{}
$chainRamOk = [ordered]@{}
foreach ($n in @('task17b', 'task17a', 'task17', 'task16c', 'task15', 'task16b')) {
    $p = Join-Path $HandoffDir "${n}_verification.json"
    $j = if ((Test-Path $p) -and ((Get-Item $p).LastWriteTimeUtc -ge $chainStartUtc)) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $chainReports[$n] = $(if ($j) { [bool]$j.allPassed } else { $null })
    $chainRam[$n] = $(if ($j -and $j.E5_FootprintRegression) { $j.E5_FootprintRegression.idlePrivateWsMb } else { $null })
    $chainRamOk[$n] = $(if ($j -and $j.E5_FootprintRegression) { [bool]$j.E5_FootprintRegression.checks.privateWsUnder45 } else { $null })
}
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task18'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45   = [bool](Test-NexusIdleRam $idleMb $memJson 'Task18').pass
    idleCpuZero        = ([math]::Round($idleCpu, 1) -eq 0.0)
    noIdleTimer        = ($null -ne $idleUi) -and (-not [bool]$idleUi.visual.runningTimerEnabled) -and ($null -eq $idleUi.visual.running)
    regressionChain    = ($chainExit -eq 0) -and ($chainReports['task17b'] -eq $true) -and ($chainReports['task17a'] -eq $true) -and ($chainReports['task17'] -eq $true) -and ($chainReports['task16c'] -eq $true) -and ($chainReports['task15'] -eq $true) -and ($chainReports['task16b'] -eq $true)
    task16bUnder45     = ($chainRamOk['task16b'] -eq $true)   # Verify-Task16b E5: <= 45,0 or [SLA-WARN] <= 50,0 with GC heap <= 8,0
    zeroLiveCalls      = ($liveCalls.Count -eq 0) -and ($liveMissing.Count -eq 0)
    gitUnchanged       = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45,0, [SLA-WARN] <= 50,0; HUD {1}) | idle CPU {2} % ({3}) | Verify-Task17b chain exit {4} in {5} s (task17b+17a+17={6}, task16c={7}, task15={8}, task16b={9}; RAM 17={10} 16c={11} 15={12} 16b={13} MB) | live calls in chain: {14} (missing [{15}]) | git same={16} {17} | CI: {18} | failed=[{19}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $chainExit, $chainSeconds, (($chainReports['task17b'] -eq $true) -and ($chainReports['task17a'] -eq $true) -and ($chainReports['task17'] -eq $true)), $chainReports['task16c'], $chainReports['task15'], $chainReports['task16b'],
    $chainRam['task17'], $chainRam['task16c'], $chainRam['task15'], $chainRam['task16b'], $liveCalls.Count, (@($liveMissing | ForEach-Object { $_.report }) -join ','),
    $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))


$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-18'; gate = 'Gate 18'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders }
    E2_PttStt = [ordered]@{ pass = $e2Pass; checks = $e2Checks; selftest = $sData }
    E3_LivePtt = [ordered]@{ pass = $e3Pass; checks = $e3Checks; status = $status; test = $test; uiPingsMs = $uiPings; lastPtt = $lastPtt; simulatedStatus = $simStatus }
    E4_Wake = [ordered]@{ pass = $e4Pass; checks = $e4Checks; warmup = $warmup; samples = $wakes; p50 = $wakeP50; p95 = $wakeP95 }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; chainExit = $chainExit; chainSeconds = $chainSeconds; chainReports = $chainReports; chainRamMb = $chainRam; chainRamOk = $chainRamOk; liveAudit = $liveAudit; liveCallsInChain = $liveCalls.Count; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
$null = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 18'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task18: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; STT '{5}' {6} ms, RAM 18 {7} MB, 16b {8} MB, WAKE P95 {9} ms, live calls {10}" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $sp.text, $sp.stt.TotalMs, $idleMb, $chainRam['task16b'], $wakeP95, $liveCalls.Count) } })
Write-Host "`nGate 18 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
