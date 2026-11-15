#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 22 automated verification (TASK-22: J2 Safe Hands-Free Auto-Run & Phonetic Bounded Fuzzy Matching -> v0.22.0).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate, exactly 1 \bRegisterHotKey call, ADR-76 locked v0.21.0 + ADR-77 registered,
        selftests voice-command-map and handsfree-autorun.
    E2  --selftest voice-command-map: Whisper small-q5_1 drift ("Kiếm cho mẹ.", "Kiếm cho mày.", "Kiểm tra máy?") & bounded fuzzy
        ("Kiếm cho má.") map 100% with SafetyTier = SafeReadOnly; >= 10 conversational queries kept as null (False Positive = 0);
        total <= 1,0 ms (worst <= 0,1 ms/sample).
    E3  --selftest handsfree-autorun: autoRunSafeCommands = true + "Kiếm cho mẹ." executes /net in <= 250 ms, InputBox.Text empty,
        calls LocalTtsService (silent memory sink); autoRunSafeCommands = false or free speech leaves in InputBox.Text;
        /ptt autorun on|off and /reload respond in <= 50 ms.
    E4  WAKE 40 -> 330 px N = 10: P50 <= 10,0 / P95 <= 16,0 ms (Test-NexusP95WithRetry).
    E5  Settled idle Private WS <= 45,0 MB (aim <= 36,5; [SLA-WARN] <= 50,0 with GC heap <= 10,5), idle CPU 0,0 %, 0 new .invalid-* files,
        regression chain Verify-Task21 (-> 20a -> 20 -> ... -> 03, -SkipLive) with stt.provider forced to "sapi", 0 live Gemini calls, git guard, CI.
    Writes 03_Local_Data_Vault/handoff/task22_verification.json (UTF-8 without BOM) and reports through MCP notify_shift_complete.
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5   # development loop: Verify-Task21 -FastE5 instead of the full chain
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
$ReportPath = Join-Path $HandoffDir 'task22_verification.json'
$ConfigPath = Join-Path $ConfigDir 'nexus_config.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$MockAnswer = "**Gate 22** mock: dùng ``Span<T>``.`n`n- ý một`n- ý hai`n`n``````csharp`nvar x = 1;`n```````nXong."
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$envNames = 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_MOCK_DELAY_MS', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES',
            'NEXUS_RELAXED_SLA', 'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE', 'NEXUS_UI_AUTOCOLLAPSE_MS', 'NEXUS_TTS_SILENT'
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
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Task Manager), then re-run Verify-Task22.ps1.'
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

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan
$invalidBefore = @(Get-ChildItem -Path $ConfigDir -Filter 'nexus_config.json.invalid-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$latestBackup = Save-NexusLatestDispatch $HandoffDir
$uiBackup = if (Test-Path $UiPath) { [System.IO.File]::ReadAllBytes($UiPath) } else { $null }
$configBackup = if (Test-Path $ConfigPath) { [System.IO.File]::ReadAllBytes($ConfigPath) } else { $null }
$clipboardBackup = $null
try { $clipboardBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }
$script:Restored = $false

# TASK-21 / TASK-22 (20a.5): Force stt.provider = "sapi" in nexus_config.json during the verification run and regression chain
function Set-VerifySttSapiConfig {
    if ($null -eq $configBackup) { return }
    $raw = $Utf8NoBom.GetString($configBackup)
    if ($raw.Length -gt 0 -and [int]$raw[0] -eq 0xFEFF) { $raw = $raw.Substring(1) }
    $cfgObj = $raw | ConvertFrom-Json
    $sttObj = [ordered]@{
        provider            = 'sapi'
        lanEndpoint         = ''
        cliPath             = ''
        cliArgs             = ''
        language            = 'vi-VN'
        lanTimeoutMs        = 10000
        autoRunSafeCommands = $true
    }
    if ($cfgObj.PSObject.Properties['stt']) { $cfgObj.stt = $sttObj }
    else { $cfgObj | Add-Member -NotePropertyName 'stt' -NotePropertyValue $sttObj -Force }
    [System.IO.File]::WriteAllText($ConfigPath, ($cfgObj | ConvertTo-Json -Depth 10), $Utf8NoBom)
}

# TASK-21 / TASK-22 (20a.3): Clean up any nexus_config.json.invalid-* created during the verification run / regression chain
function Remove-NewInvalidConfigFiles {
    $removed = @()
    $nowFiles = @(Get-ChildItem -Path $ConfigDir -Filter 'nexus_config.json.invalid-*' -ErrorAction SilentlyContinue)
    foreach ($f in $nowFiles) {
        if ($invalidBefore -notcontains $f.FullName) {
            try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop; $removed += $f.Name } catch { }
        }
    }
    return $removed
}

function Restore-UserState {
    if ($script:Restored) { return }
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $configBackup) {
        [System.IO.File]::WriteAllBytes($ConfigPath, $configBackup)
        if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) { try { $null = Send-PipeJson 'COCKPIT' '/reload' } catch { } }
    }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    $null = Remove-NewInvalidConfigFiles
    $script:Restored = $true
}

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

try {
if ($null -eq $configBackup) { throw "nexus_config.json not found at $ConfigPath (start the HostService once to create it)." }
Set-VerifySttSapiConfig

# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, 1 RegisterHotKey call, ADR-76 locked v0.21.0 + ADR-77 registered, selftests voice-command-map & handsfree-autorun'
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
$adr76Locked = $adrText -match '(?m)^\|\s*ADR-76\s*\|.*Chốt `?v0\.21\.0'
$adr77       = $adrText -match '(?m)^\|\s*ADR-77\s*\|'
$hotkeyCalls = @(Get-ChildItem -Path $EngineRoot -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    Select-String -Pattern '\bRegisterHotKey\s*\(' -CaseSensitive | Where-Object { $_.Line -notmatch '\bextern\b' -and $_.Line.TrimStart() -notmatch '^(//|///|\*)' } |
    ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber })
$selfMap = Invoke-SelfTest 'voice-command-map' 'task20'
$sMap = $selfMap.data
$selfAuto = Invoke-SelfTest 'handsfree-autorun' 'task22'
$sAuto = $selfAuto.data
$e1Checks = [ordered]@{
    build        = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom      = [bool]$bom.Pass
    oneHotkeyApi = ($hotkeyCalls.Count -eq 1)
    adr76Locked  = [bool]$adr76Locked
    adr77        = [bool]$adr77
    selftestMap  = ($selfMap.exitCode -eq 0) -and $selfMap.noBom -and [bool]$sMap.pass
    selftestAuto = ($selfAuto.exitCode -eq 0) -and $selfAuto.noBom -and [bool]$sAuto.pass
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | RegisterHotKey [{5}] | ADR-76 v0.21.0={6}, ADR-77={7} | voice-command-map pass={8} | handsfree-autorun pass={9} | failed=[{10}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, ($hotkeyCalls -join ', '), $adr76Locked, $adr77, $sMap.pass, $sAuto.pass, (Get-FailedNames $e1Checks))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: --selftest voice-command-map - Whisper drift ("Kiếm cho mẹ.", "Kiếm cho mày.", "Kiểm tra máy?") & bounded fuzzy <= 2 edits -> /net (SafeReadOnly), >= 10 free speech kept (0% false positive), total <= 1,0 ms (worst <= 0,1 ms)'
$cMap = $sMap.checks
$tbMap = $sMap.table
$e2Checks = [ordered]@{
    allCorrect     = [bool]$cMap.'e2.allCorrect'
    driftAndFuzzy  = [bool]$cMap.'e2.task22DriftAndFuzzy'
    zeroFalsePos   = [bool]$cMap.'e2.task22ConversationalZeroFalsePositives' -and [bool]$cMap.'e2.freeSpeechKept'
    eachUnder0_1ms = [bool]$cMap.'e2.eachUnder0_1ms'
    totalUnder1ms  = [bool]$cMap.'e2.totalUnder1ms'
}
foreach ($row in @($tbMap.rows)) { Write-Host ("  {0,-48} -> {1,-18} [{2,-12}] {3} ms {4}" -f $row.spoken, $(if ($row.got) { $row.got } else { '(giữ nguyên)' }), $(if ($row.tier) { $row.tier } else { '-' }), $row.ms, $(if ($row.ok) { '' } else { 'SAI' })) }
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("mapped={0} kept={1} wrong={2} | total={3} ms worst={4} ms (cold={5} ms) | failed=[{6}]" -f `
    $tbMap.mapped, $tbMap.kept, $tbMap.wrong, $tbMap.totalMs, $tbMap.worstMs, $tbMap.coldFirstCallMs, (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: --selftest handsfree-autorun - autoRunSafeCommands = true + "Kiếm cho mẹ." -> /net <= 250 ms, InputBox empty, LocalTtsService called; autoRun = false / convo stays in InputBox; /ptt autorun on|off & /reload <= 50 ms'
$cAuto = $sAuto.checks
$stAuto = $sAuto.steps.autorun
$e3Checks = [ordered]@{
    configAndReload    = [bool]$cAuto.'e3.configDefaultTrue' -and [bool]$cAuto.'e3.configParseAndReloadUnder50ms' -and [bool]$cAuto.'e3.reloadUnder50ms'
    pttAutorunCmd50ms  = [bool]$cAuto.'e3.pttAutorunOnOffUnder50ms' -and [bool]$cAuto.'e3.pttStatusReflectsAutorun'
    windowAutoRunSafe  = [bool]$cAuto.'e3.mapSafeCommand' -and [bool]$cAuto.'e3.windowAutoRunSafe'
    windowTtsCalled    = [bool]$cAuto.'e3.windowAutoRunTtsCalled'
    windowManualConvo  = [bool]$cAuto.'e3.windowManualLeavesInBox' -and [bool]$cAuto.'e3.windowConvoLeavesInBox'
    ledgerZero         = [bool]$cAuto.'e3.ledgerZeroEnd'
    noException        = -not (@($sAuto.checks.PSObject.Properties.Name) -contains 'noException')
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("autoFlow '{0}' -> autoRan={1} cmd='{2}' box='{3}' in {4} ms | TTS words={5} ({6} ms) | manualFlow box='{7}' | convoFlow box='{8}' | /ptt autorun off={9} on={10} reload={11} ms | failed=[{12}]" -f `
    $stAuto.autoFlow.spoken, $stAuto.autoFlow.end.AutoRan, $stAuto.autoFlow.end.AutoRanCommand, $stAuto.autoFlow.end.CommandText, $stAuto.autoFlow.end.LastMs,
    $(if ($stAuto.ttsLast) { $stAuto.ttsLast.wordCount } else { 0 }), $(if ($stAuto.ttsLast) { $stAuto.ttsLast.totalMs } else { 0 }),
    $stAuto.manualFlow.end.CommandText, $stAuto.convoFlow.end.CommandText,
    $stAuto.autoOffMs, $stAuto.autoOnMs, $stAuto.reloadMs, (Get-FailedNames $e3Checks))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: WAKE 40 -> 330 px N = 10, P50 <= 10,0 / P95 <= 16,0 ms'
$hostProc = Restart-Host $true
$warmup = Get-WakeSample
$wakes = @()
for ($i = 0; $i -lt 10; $i++) { $wakes += Get-WakeSample }
$wakeMs = @($wakes | Where-Object { $null -ne $_.ms } | ForEach-Object { [double]$_.ms })
$wakeP50 = Get-Percentile $wakeMs 50
$script:wakeRetrySamples = @()
$wakeRetry = Test-NexusP95WithRetry $wakeMs { for ($i = 0; $i -lt 10; $i++) { $s = Get-WakeSample; $script:wakeRetrySamples += $s; if ($null -ne $s.ms) { [double]$s.ms } } } 16.0 'E4 WAKE'
$wakeP95 = $wakeRetry.p95
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$e4Checks = [ordered]@{
    wakeP50 = ($null -ne $wakeP50) -and ($wakeP50 -le 10.0) -and ($wakeMs.Count -eq 10)
    wakeP95 = [bool]$wakeRetry.pass
    focused = (@(@($wakes) + @($script:wakeRetrySamples) | Where-Object { -not [bool]$_.focused }).Count -eq 0)
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("WAKE P50 {0} / P95 {1} ms (first P95 {2}, retried {3}; {4}) | failed=[{5}]" -f $wakeP50, $wakeP95, $wakeRetry.firstP95, $wakeRetry.retried, (($wakeMs | ForEach-Object { '{0:N2}' -f $_ }) -join ' / '), (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45,0 MB (aim <= 36,5; [SLA-WARN] <= 50,0), idle CPU 0,0 %, 0 new .invalid-* files, regression Verify-Task21 (-> ... -> 03, -SkipLive), git, CI'
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
if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
Set-VerifySttSapiConfig
if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored before the regression chain.' }
Stop-HostService
$chainArgs = @('-SkipBuild'); if ($FastE5) { $chainArgs += '-FastE5' }
$chainStartUtc = [DateTime]::UtcNow
$swChain = [System.Diagnostics.Stopwatch]::StartNew()
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task21.ps1') @chainArgs | Out-Host
$chainExit = $LASTEXITCODE
$chainSeconds = [math]::Round($swChain.Elapsed.TotalSeconds, 1)
Restore-UserState
$removedInvalid = @(Remove-NewInvalidConfigFiles)
$remainingNewInvalid = @(Get-ChildItem -Path $ConfigDir -Filter 'nexus_config.json.invalid-*' -ErrorAction SilentlyContinue | Where-Object { $invalidBefore -notcontains $_.FullName })
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
foreach ($n in @('task21', 'task20a', 'task20', 'task19', 'task18b', 'task18a', 'task18', 'task17b', 'task17a', 'task17', 'task16c', 'task15', 'task16b')) {
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
        -Headers @{ 'User-Agent' = 'nexus-verify-task22'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45   = [bool](Test-NexusIdleRam $idleMb $memJson 'Task22').pass
    idleCpuZero        = ([math]::Round($idleCpu, 1) -eq 0.0)
    noIdleTimer        = ($null -ne $idleUi) -and (-not [bool]$idleUi.visual.runningTimerEnabled) -and ($null -eq $idleUi.visual.running)
    noNewInvalidFiles  = ($remainingNewInvalid.Count -eq 0)
    regressionChain    = ($chainExit -eq 0) -and ($chainReports['task21'] -eq $true) -and ($chainReports['task20a'] -eq $true) -and ($chainReports['task20'] -eq $true) -and ($chainReports['task19'] -eq $true) -and ($chainReports['task18b'] -eq $true) -and ($chainReports['task18a'] -eq $true) -and ($chainReports['task18'] -eq $true) -and ($chainReports['task17b'] -eq $true) -and ($chainReports['task17a'] -eq $true) -and ($chainReports['task17'] -eq $true) -and ($chainReports['task16c'] -eq $true) -and ($chainReports['task15'] -eq $true) -and ($chainReports['task16b'] -eq $true)
    task16bUnder45     = ($chainRamOk['task16b'] -eq $true)
    zeroLiveCalls      = ($liveCalls.Count -eq 0) -and ($liveMissing.Count -eq 0)
    gitUnchanged       = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45,0, [SLA-WARN] <= 50,0; HUD {1}) | idle CPU {2} % ({3}) | new .invalid-* left={4} (cleaned={5}) | Verify-Task21 chain exit {6} in {7} s (task21..17={8}, task16c={9}, task15={10}, task16b={11}; RAM 21={12} 17={13} 16c={14} 15={15} 16b={16} MB) | live calls in chain: {17} (missing [{18}]) | git same={19} {20} | CI: {21} | failed=[{22}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $remainingNewInvalid.Count, $removedInvalid.Count, $chainExit, $chainSeconds,
    (($chainReports['task21'] -eq $true) -and ($chainReports['task20a'] -eq $true) -and ($chainReports['task20'] -eq $true) -and ($chainReports['task19'] -eq $true) -and ($chainReports['task18b'] -eq $true) -and ($chainReports['task18a'] -eq $true) -and ($chainReports['task18'] -eq $true) -and ($chainReports['task17b'] -eq $true) -and ($chainReports['task17a'] -eq $true) -and ($chainReports['task17'] -eq $true)),
    $chainReports['task16c'], $chainReports['task15'], $chainReports['task16b'],
    $chainRam['task21'], $chainRam['task17'], $chainRam['task16c'], $chainRam['task15'], $chainRam['task16b'], $liveCalls.Count, (@($liveMissing | ForEach-Object { $_.report }) -join ','),
    $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-22'; gate = 'Gate 22'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; registerHotKeyCalls = $hotkeyCalls }
    E2_VoiceCommandFuzzy = [ordered]@{ pass = $e2Pass; checks = $e2Checks; voiceMap = $tbMap }
    E3_HandsFreeAutoRun = [ordered]@{ pass = $e3Pass; checks = $e3Checks; autorun = $stAuto }
    E4_Wake = [ordered]@{ pass = $e4Pass; checks = $e4Checks; warmup = $warmup; samples = $wakes; p50 = $wakeP50; p95 = $wakeP95; retry = $wakeRetry; retrySamples = $script:wakeRetrySamples }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; removedInvalidFiles = $removedInvalid; remainingNewInvalidFiles = @($remainingNewInvalid | ForEach-Object { $_.Name }); chainExit = $chainExit; chainSeconds = $chainSeconds; chainReports = $chainReports; chainRamMb = $chainRam; chainRamOk = $chainRamOk; liveAudit = $liveAudit; liveCallsInChain = $liveCalls.Count; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
$null = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 22'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task22: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; map total {5} ms (worst {6} ms), autorun {7} ms, RAM 22 {8} MB, WAKE P95 {9} ms, live calls {10}" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $tbMap.totalMs, $tbMap.worstMs, $stAuto.autoFlow.end.LastMs, $idleMb, $wakeP95, $liveCalls.Count) } })
Write-Host "`nGate 22 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    $null = Remove-NewInvalidConfigFiles
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
