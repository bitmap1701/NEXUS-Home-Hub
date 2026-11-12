#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 17 automated verification (TASK-17, ADR-67: /net radar + NITRO5-WORKER on the 2-column Cockpit of
    v0.16.5, MCP get_network_radar, FIFINE K669 PreferredMicSelector + /mic, /ptt 16 kHz VAD, clipboard writes that never hang
    the Cockpit, WAKE [SLA-WARN] VSync band in the regression chain).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate, PowerShell 5.1 parse of every tools\*.ps1, ADR-67 registered and
        ADR-68 "Chốt v0.16.5", no new global hotkey (RegisterHotKey calls unchanged), no animation in the Cockpit, selftest net-ptt.
    E2  Live HostService (Gemini mocked, 0 token): a TcpListener on an ephemeral loopback port opened by this script ->
        /net port shows LISTENING in < 15 ms as a local line, MCP get_network_radar (portFilter) sees it in < 15 ms; after Stop
        /net port says FREE. /net = one Radar card in the thread (physical NIC with gateway 192.168.1.1, Radmin 26.x skipped,
        pings <= 300 ms, NITRO5 ONLINE or OFFLINE (AWAY)), the right-column NITRO5 line changes, Alt+Q (pipe RIGHTPAGE) flips
        THẺ ĐÃ GHIM <-> MẠNG // /NET; /net ping 192.0.2.1 (TEST-NET) answers 0/3 in <= 1 s.
    E3  Selector unit checks of the selftest + this PC: with iVCam and the FIFINE K669 plugged in, /mic selects the FIFINE and
        marks iVCam [Ignored Virtual Mic]; /mic list labels it. (Plug the FIFINE in before the run.)
    E4  VAD + zero-leak (selftest: silence / 440 Hz / 1 kHz / noise, ledger back to 0) and live: /ptt test 2 on the FIFINE
        (16 kHz, 16-bit, mono) ends with 0 bytes held, /ptt test 10 stopped after 0,5 s (pipe PTTSTOP) ends early; clipboard worker
        vs a hung owner (decision 3.7) from the selftest.
    E5  Settled idle Private WS <= 45,0 MB, idle CPU 0,0 %, no timer, microphone closed, regression chain Verify-Task16c (-> Task15
        -> Task16b -> Task16 -> Task13 -> Task12 -> Task13a -> Task14 ... Task03, -SkipLive) with 0 live Gemini calls, git, CI.
    latest_dispatch.json, cockpit_ui.json and the clipboard text are restored. Writes
    03_Local_Data_Vault/handoff/task17_verification.json (UTF-8 without BOM) and reports through MCP notify_shift_complete.
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5   # development loop: Verify-Task16c -FastE5 instead of the full chain
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
$ReportPath = Join-Path $HandoffDir 'task17_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$MockAnswer = 'Gate 17 net-ptt mock'
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
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Task Manager), then re-run Verify-Task17.ps1.'
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
$clipboardBackup = $null
try { $clipboardBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }
$script:Restored = $false
function Restore-UserState {
    if ($script:Restored) { return }
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    $script:Restored = $true
}

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, PS 5.1 parse, ADR-67 / ADR-68, no new global hotkey, selftest net-ptt'
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
$parseErrors = @()
foreach ($f in Get-ChildItem (Join-Path $EngineRoot 'tools') -Filter '*.ps1' -File) {
    $tokens = $null; $errs = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errs)
    foreach ($e in @($errs)) { $parseErrors += ('{0}:{1} {2}' -f $f.Name, $e.Extent.StartLineNumber, $e.Message) }
}
$adrText = Get-Content (Join-Path $RepoRoot 'docs\ADR_REGISTRY.md') -Raw -Encoding UTF8
$adr67 = $adrText -match '(?m)^\|\s*ADR-67\s*\|'
$adr68Locked = $adrText -match '(?m)^\|\s*ADR-68\s*\|.*Chốt `?v0\.16\.5'
$hotkeySource = Get-Content (Join-Path $EngineRoot 'NEXUSHome.HostService\AulaHero68HotkeyListener.cs') -Raw -Encoding UTF8
$registerCalls = ([regex]::Matches($hotkeySource, 'Register\(hwnd,')).Count
$nativeRegister = @(Get-ChildItem $EngineRoot -Recurse -File -Include '*.cs' | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'AulaHero68HotkeyListener.cs' } |
    Select-String -Pattern 'RegisterHotKey\s*\(' | ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber })
$animationHits = @(Get-ChildItem (Join-Path $EngineRoot 'NEXUSHome.CockpitUI') -Recurse -File -Include '*.xaml', '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern '<Storyboard|new\s+\w*Animation\b|BeginAnimation\(|RepeatBehavior' | ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber })
$self = Invoke-SelfTest 'net-ptt' 'task17'
$sData = $self.data
$e1Checks = [ordered]@{
    build          = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom        = [bool]$bom.Pass
    ps51Parse      = ($parseErrors.Count -eq 0)
    adr67          = [bool]$adr67
    adr68Locked    = [bool]$adr68Locked
    noNewHotkey    = ($registerCalls -eq 4) -and ($nativeRegister.Count -eq 0)
    noAnimation    = ($animationHits.Count -eq 0)
    selftest       = ($self.exitCode -eq 0) -and $self.noBom -and [bool]$sData.pass
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | PS 5.1 parse errors {5} | ADR-67 {6}, ADR-68 locked {7} | RegisterHotKey calls {8} (+{9} elsewhere) | animations [{10}] | selftest net-ptt pass={11} failed=[{12}] | failed=[{13}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $parseErrors.Count, $adr67, $adr68Locked, $registerCalls, $nativeRegister.Count,
    ($animationHits -join ','), $sData.pass, (@($sData.failed) -join ','), (Get-FailedNames $e1Checks))
foreach ($pe in $parseErrors) { Write-Host "  parse: $pe" -ForegroundColor Red }

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: /net port (LISTENING -> FREE) + MCP get_network_radar < 15 ms, /net Radar card, NITRO5 line, Alt+Q page, ping cut'
$hostProc = Restart-Host $true
# Gate 17 run 1: a hidden Cockpit keeps no thread rows (Gate 16c ccf739f), so the lines were never rendered. Wake it once, back to
# PASSIVE (visible, Half) before the checks, like Verify-Task16c E2.
$null = Send-PipeRaw 'COMMANDMODE' $null
Start-Sleep -Milliseconds 300
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$null = Send-PipeJson 'HISTORY' 'refresh'
Start-Sleep -Milliseconds 300
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
try {
    $null = Send-PipeJson 'COCKPIT' '/net port 1'   # warms the IP helper tables of the HostService
    $portOpen = Send-PipeJson 'COCKPIT' "/net port $port"
    $mcp = Invoke-Mcp @(@{ name = 'get_network_radar'; arguments = [ordered]@{ portFilter = $port; ping = $false } })
    $mcpRadar = $mcp[2]
} finally { $listener.Stop() }
$portClosed = Send-PipeJson 'COCKPIT' "/net port $port"
$afterPort = Get-UiState
$portLine = @(Get-ThreadItems $afterPort | Where-Object { $_.kind -eq 'local' -and [string]$_.title -eq "/net port $port" }) | Select-Object -Last 1

$radarBefore = [int]$afterPort.task17.radarCardsAdded
$swNet = [System.Diagnostics.Stopwatch]::StartNew()
$netRes = Send-PipeJson 'COCKPIT' '/net'
$netReturnMs = [math]::Round($swNet.Elapsed.TotalMilliseconds, 1)
$radarUi = Wait-Ui { param($u) [int]$u.task17.radarCardsAdded -gt $radarBefore } 4000
$radarMs = [math]::Round($swNet.Elapsed.TotalMilliseconds, 1)
$radar = $radarUi.task17.lastRadar
$radarItem = @(Get-ThreadItems $radarUi | Where-Object { $_.kind -eq 'radar' }) | Select-Object -Last 1
$flip1 = Send-PipeRaw 'RIGHTPAGE' $null
$netPageUi = Get-UiState
$flip2 = Send-PipeRaw 'RIGHTPAGE' $null
$pinsPageUi = Get-UiState
$pingBefore = [int]$pinsPageUi.task17.localTasksDone
$swPing = [System.Diagnostics.Stopwatch]::StartNew()
$pingRes = Send-PipeJson 'COCKPIT' '/net ping 192.0.2.1'
$pingUi = Wait-Ui { param($u) [int]$u.task17.localTasksDone -gt $pingBefore } 3000
$pingMs = [math]::Round($swPing.Elapsed.TotalMilliseconds, 1)
$pingLine = @(Get-ThreadItems $pingUi | Where-Object { $_.kind -eq 'local' -and [string]$_.title -eq '/net ping 192.0.2.1' }) | Select-Object -Last 1
$e2Checks = [ordered]@{
    portListening   = ([string]$portOpen.localData.State -eq 'LISTENING') -and ([double]$portOpen.localComputeMs -lt $PortMaxMs) -and ([int]$portOpen.totalTokens -eq 0)
    portLocalLine   = ($null -ne $portLine) -and ([string]$portLine.badge -eq $LocalBadge)
    mcpRadarPort    = ($null -ne $mcpRadar) -and ([string]$mcpRadar.portCheck.State -eq 'LISTENING') -and ([double]$mcpRadar.portCheck.ElapsedMs -lt $PortMaxMs) -and ([int]$mcpRadar.tokens -eq 0)
    portFree        = ([string]$portClosed.localData.State -eq 'FREE')
    netAsync        = [bool]$netRes.localPending -and ($netReturnMs -lt 1000)
    radarCard       = ($null -ne $radarItem) -and ([string]$radarItem.badge -eq $LocalBadge) -and ($null -ne $radar)
    nicPhysicalLan  = ($null -ne $radar) -and ([string]$radar.gateway -eq '192.168.1.1') -and ([string]$radar.ipv4 -like '192.168.1.*') -and ([string]$radar.ipv4 -notlike '26.*')
    pingsCut        = ($null -ne $radar) -and ([double]$radar.PingMs -le $PingCutMs) -and (@($radar.pings | Where-Object { [double]$_.ElapsedMs -gt $PingCutMs }).Count -eq 0)
    nitro5State     = ($null -ne $radar) -and (([string]$radar.Nitro5State -like 'ONLINE*') -or ([string]$radar.Nitro5State -eq 'OFFLINE (AWAY)'))
    nitro5Line      = ([string]$radarUi.task17.nitro5Line -like '⚡ NITRO5*') -and ([string]$radarUi.task17.nitro5Line -notlike '*chưa dò*')
    altQNetPage     = ($flip1 -eq 'net') -and ([string]$netPageUi.task17.rightTopLabel -eq 'MẠNG // /NET') -and [bool]$netPageUi.task17.netPageVisible -and -not [bool]$netPageUi.task17.pinsVisible
    altQBack        = ($flip2 -eq 'pins') -and ([string]$pinsPageUi.task17.rightTopLabel -eq 'THẺ ĐÃ GHIM') -and [bool]$pinsPageUi.task17.pinsVisible
    pingTestNetCut  = ($null -ne $pingLine) -and ([string]$pingLine.result -like '*0/3*') -and ($pingMs -le 1500)
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("/net port {0}: {1} in {2} ms -> after Stop {3} | MCP radar port {4} in {5} ms | /net returned in {6} ms, Radar card after {7} ms: NIC {8} {9} GW {10}, pings {11} ms, NITRO5 {12} {13} | line '{14}' | Alt+Q {15}/{16} | ping TEST-NET '{17}' in {18} ms | failed=[{19}]" -f `
    $port, $portOpen.localData.State, $portOpen.localComputeMs, $portClosed.localData.State, $mcpRadar.portCheck.State, $mcpRadar.portCheck.ElapsedMs,
    $netReturnMs, $radarMs, $radar.nic, $radar.ipv4, $radar.gateway, $radar.PingMs, $radar.Nitro5State, $radar.Nitro5Address, $radarUi.task17.nitro5Line,
    $flip1, $flip2, $pingLine.result, $pingMs, (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: PreferredMicSelector (selftest) + this PC: FIFINE K669 selected, iVCam ignored'
$micStatus = Send-PipeJson 'COCKPIT' '/mic'
$micList = Send-PipeJson 'COCKPIT' '/mic list'
$devices = @($micStatus.localData.devices)
$hasFifine = @($devices | Where-Object { [string]$_.Name -match 'FIFINE' }).Count -gt 0
$hasIvcam = @($devices | Where-Object { [string]$_.Name -match 'iVCam' }).Count -gt 0
$selectedName = [string]$micStatus.localData.selected.Name
$e3Checks = [ordered]@{
    selectorUnits  = [bool]$sData.checks.'e3.fifineFirst' -and [bool]$sData.checks.'e3.noFifinePhysical' -and [bool]$sData.checks.'e3.forcedWins' -and
                     [bool]$sData.checks.'e3.onlyVirtualNone' -and [bool]$sData.checks.'e3.denylist' -and [bool]$sData.checks.'e3.selectPersists' -and [bool]$sData.checks.'e3.commandStatus'
    fifinePlugged  = $hasFifine
    fifineSelected = $hasFifine -and ($selectedName -match 'FIFINE') -and ([string]$micStatus.message -like '*[ACTIVE MIC]*FIFINE*')
    ivcamIgnored   = (-not $hasIvcam) -or (([string]$micStatus.message -like '*iVCam*Ignored Virtual Mic*') -and ([string]$micList.message -like '*iVCam*Virtual-Ignored*'))
    pcmFormat      = ([string]$micStatus.message -like '*16000 Hz, 16-bit, Mono PCM*') -and ([int]$micStatus.totalTokens -eq 0)
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("devices [{0}] | selected '{1}' ({2}) | iVCam present {3} | status '{4}' | failed=[{5}]" -f `
    (($devices | ForEach-Object { '{0}{1}' -f $_.Name, $(if ($_.Virtual) { ' (ảo)' } else { '' }) }) -join '; '), $selectedName, $micStatus.localData.reason, $hasIvcam, $micStatus.message, (Get-FailedNames $e3Checks))
if (-not $hasFifine) { Write-Host '  Cắm micro FIFINE K669 rồi chạy lại: E3 / E4 cần thiết bị thật.' -ForegroundColor Yellow }

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: VAD on synthetic PCM (selftest), live /ptt test 2 on the FIFINE, /ptt stopped early, 0 bytes held, clipboard worker'
$pttBefore = [int](Get-UiState).task17.localTasksDone
$swPtt = [System.Diagnostics.Stopwatch]::StartNew()
$pttRes = Send-PipeJson 'COCKPIT' '/ptt test 2'
$pttUi = Wait-Ui { param($u) [int]$u.task17.localTasksDone -gt $pttBefore -and -not [bool]$u.task17.mic.busy } 6000
$pttMs = [math]::Round($swPtt.Elapsed.TotalMilliseconds, 1)
$pttLast = $pttUi.task17.mic.lastPtt
$stopBefore = [int]$pttUi.task17.localTasksDone
$swStop = [System.Diagnostics.Stopwatch]::StartNew()
$null = Send-PipeJson 'COCKPIT' '/ptt test 10'
Start-Sleep -Milliseconds 500
$null = Send-PipeRaw 'PTTSTOP' $null
$stopUi = Wait-Ui { param($u) [int]$u.task17.localTasksDone -gt $stopBefore -and -not [bool]$u.task17.mic.busy } 4000
$stopMs = [math]::Round($swStop.Elapsed.TotalMilliseconds, 1)
$clip = $sData.clipboard
$e4Checks = [ordered]@{
    vadSynthetic   = [bool]$sData.checks.'e4.silenceNoVad' -and [bool]$sData.checks.'e4.tone440Vad' -and [bool]$sData.checks.'e4.tone1kVad' -and [bool]$sData.checks.'e4.noiseNoVad'
    buffersFreed   = [bool]$sData.checks.'e4.buffersFreed' -and [bool]$sData.checks.'e4.cancelFrees' -and [bool]$sData.checks.'e4.oneSession' -and [bool]$sData.checks.'e4.sttStub'
    pttLive        = [bool]$pttRes.localPending -and ($null -ne $pttLast) -and [bool]$pttLast.Ok -and ([string]$pttLast.message -like '*FIFINE*') -and
                     ([string]$pttLast.message -like '*VAD = *') -and ([string]$pttLast.message -like '*bộ đệm còn giữ 0 B*') -and ($pttMs -le 5000)
    pttStopEarly   = ([string]$stopUi.task17.mic.lastPtt.message -like '*dừng sớm*') -and ($stopMs -le 3000)
    micClosed      = (-not [bool]$stopUi.task17.mic.busy) -and ([long]$stopUi.task17.mic.outstandingBytes -eq 0)
    clipboardWorker = [bool]$sData.checks.'c37.clipboardWorker'
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("synthetic VAD silence/440/1k/noise = {0}/{1}/{2}/{3} | /ptt test 2 in {4} ms: {5} | /ptt test 10 stopped after {6} ms: {7} | held {8} B | clipboard hung owner: {9} | failed=[{10}]" -f `
    $sData.vad.silence.Vad, $sData.vad.tone440.Vad, $sData.vad.tone1k.Vad, $sData.vad.noise.Vad, $pttMs, $pttLast.message, $stopMs, $stopUi.task17.mic.lastPtt.message,
    $stopUi.task17.mic.outstandingBytes, $(if ($clip.skipped) { "skipped ($($clip.setupError))" } else { "Copy returned in $($clip.hungMs) ms, timedOut=$($clip.timedOut)" }), (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45,0 MB, idle CPU 0,0 %, mic closed, regression Verify-Task16c (-> 15 ... 03, -SkipLive), git, CI'
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
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
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task16c.ps1') @chainArgs | Out-Host
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
$wakeWarnings = @()
foreach ($n in @('task16c', 'task15', 'task16b')) {
    $p = Join-Path $HandoffDir "${n}_verification.json"
    $j = if ((Test-Path $p) -and ((Get-Item $p).LastWriteTimeUtc -ge $chainStartUtc)) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $chainReports[$n] = $(if ($j) { [bool]$j.allPassed } else { $null })
    if ($j) {
        foreach ($e4 in @($j.E4_Wake, $j.E4_Wake40to330)) {
            if ($null -eq $e4) { continue }
            foreach ($w in @($e4.slaWarnings)) { if ($null -ne $w) { $wakeWarnings += [ordered]@{ report = $n; p50 = $w.p50; p95 = $w.p95; fastSamples = $w.fastSamples; rule = $w.rule } } }
        }
    }
}
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task17'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45 = [bool](Test-NexusIdleRam $idleMb $memJson 'Task17').pass
    idleCpuZero      = ([math]::Round($idleCpu, 1) -eq 0.0)
    noIdleTimer      = ($null -ne $idleUi) -and (-not [bool]$idleUi.visual.runningTimerEnabled) -and ($null -eq $idleUi.visual.running)
    micClosedIdle    = ($null -ne $idleUi) -and (-not [bool]$idleUi.task17.mic.busy) -and ([long]$idleUi.task17.mic.outstandingBytes -eq 0)
    regressionChain  = ($chainExit -eq 0) -and ($chainReports['task16c'] -eq $true) -and ($chainReports['task15'] -eq $true) -and ($chainReports['task16b'] -eq $true)
    zeroLiveCalls    = ($liveCalls.Count -eq 0) -and ($liveMissing.Count -eq 0)
    gitUnchanged     = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
foreach ($w in $wakeWarnings) { Write-Host ("[SLA-WARN (one VSync, TASK-17 §2.3)] {0} WAKE P50 {1} / P95 {2} ms, {3}/10 <= 12 ms: {4}" -f $w.report, $w.p50, $w.p95, $w.fastSamples, $w.rule) -ForegroundColor Yellow }
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45,0; HUD {1}) | idle CPU {2} % ({3}) | Verify-Task16c chain exit {4} in {5} s (task16c={6}, task15={7}, task16b={8}; WAKE [SLA-WARN] {9}) | live calls in chain: {10} (audited {11}; missing [{12}]) | git same={13} {14} | CI: {15} | failed=[{16}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $chainExit, $chainSeconds, $chainReports['task16c'], $chainReports['task15'], $chainReports['task16b'], $wakeWarnings.Count,
    $liveCalls.Count, (@($liveAudit | Where-Object { $_.ranInChain } | ForEach-Object { $_.report }) -join ','), (@($liveMissing | ForEach-Object { $_.report }) -join ','),
    $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-17'; gate = 'Gate 17'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; parseErrors = $parseErrors; registerHotKeyCalls = $registerCalls; animationHits = $animationHits; selftest = $sData }
    E2_NetRadar = [ordered]@{ pass = $e2Pass; checks = $e2Checks; port = $port; portOpen = $portOpen; portClosed = $portClosed; mcpRadar = $mcpRadar; netReturnMs = $netReturnMs; radarMs = $radarMs; radar = $radar; radarItem = $radarItem; nitro5Line = $radarUi.task17.nitro5Line; netPage = $netPageUi.task17; pingMs = $pingMs; pingLine = $pingLine }
    E3_MicSelector = [ordered]@{ pass = $e3Pass; checks = $e3Checks; status = $micStatus; list = $micList.message }
    E4_PttVad = [ordered]@{ pass = $e4Pass; checks = $e4Checks; pttMs = $pttMs; ptt = $pttLast; stopMs = $stopMs; stopped = $stopUi.task17.mic; vad = $sData.vad; clipboard = $clip }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; chainExit = $chainExit; chainSeconds = $chainSeconds; chainReports = $chainReports; wakeSlaWarnings = $wakeWarnings; liveAudit = $liveAudit; liveCallsInChain = $liveCalls.Count; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
$null = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 17'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task17: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; /net port {5} ms, NITRO5 {6}, mic '{7}', RAM {8} MB, live calls {9}" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $portOpen.localComputeMs, $radar.Nitro5State, $selectedName, $idleMb, $liveCalls.Count) } })
Write-Host "`nGate 17 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
