#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 17a automated verification (TASK-17a, ADR-69: optional networkTargets in nexus_config.json with /reload,
    and light idle [MEM] UI trims, kept as harmless after the -Trim survey).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate, ADR-67 "Chốt v0.17.0" and ADR-69 in docs/ADR_REGISTRY.md, selftest
        net-targets (temp config: custom list, max 8, missing / [] / broken item / broken file -> 4 defaults).
    E2  Live HostService (Gemini mocked): nexus_config.json gets networkTargets LOOPBACK-TEST 127.0.0.1 + NITRO5-LAN .22, /reload
        (same process, no restart), /net -> the Radar uses them (LOOPBACK-TEST ONLINE, source config), MCP get_network_radar too.
    E3  networkTargets missing, [] and [{"label":"","host":null}] -> /reload + /net -> the 4 defaults (Gateway .1, NITRO5-LAN .22,
        NITRO5-WIFI .192, LAN-TV .97), no error. The user's nexus_config.json is put back and reloaded.
    E4  selftest ui-footprint (/net and hiddenTrimmed phases, default trims) and live: idle [MEM] pass on a visible PASSIVE Cockpit
        keeps 4 card rows and drops the MẠNG // /NET text, on a hidden one drops the answer view; a wake brings both back
        outside the timer; WAKE 40 -> 330 px N = 10 P50 <= 10,0 / P95 <= 16,0 ms.
    E5  Settled idle Private WS <= 45,0 MB (Architect 17a.2: 43,5 dropped, within WPF render-surface noise), idle CPU 0,0 %, regression chain Verify-Task17 (-> 16c -> 15 -> 16b (<= 45,0)
        -> 16 -> 13 -> 12 -> 13a -> 14 ... 03, -SkipLive) with 0 live Gemini calls, git guard, CI.
    latest_dispatch.json, cockpit_ui.json, nexus_config.json and the clipboard text are restored. Writes
    03_Local_Data_Vault/handoff/task17a_verification.json (UTF-8 without BOM) and reports through MCP notify_shift_complete.
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5   # development loop: Verify-Task17 -FastE5 instead of the full chain
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
$ReportPath = Join-Path $HandoffDir 'task17a_verification.json'
$ConfigPath = Join-Path $ConfigDir 'nexus_config.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$MockAnswer = "**Gate 17a** mock: dùng ``Span<T>``.`n`n- ý một`n- ý hai`n`n``````csharp`nvar x = 1;`n```````nXong."
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
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Task Manager), then re-run Verify-Task17a.ps1.'
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
# nexus_config.json of the user with "networkTargets" replaced (or removed with $null); UTF-8 without BOM like the service writes.
function Set-NetworkTargets($targets) {
    $text = [System.Text.Encoding]::UTF8.GetString($configBackup).TrimStart([char]0xFEFF)
    $obj = $text | ConvertFrom-Json
    if ($obj.PSObject.Properties.Name -contains 'networkTargets') { $obj.PSObject.Properties.Remove('networkTargets') }
    if ($null -ne $targets) { $obj | Add-Member -NotePropertyName 'networkTargets' -NotePropertyValue $targets }
    [System.IO.File]::WriteAllText($ConfigPath, ($obj | ConvertTo-Json -Depth 8), $Utf8NoBom)
}
function Invoke-NetScan {
    $before = [int](Get-UiState).task17.radarCardsAdded
    $null = Send-PipeJson 'COCKPIT' '/net'
    $u = Wait-Ui { param($x) [int]$x.task17.radarCardsAdded -gt $before } 4000
    return $u.task17.lastRadar
}

try {
if ($null -eq $configBackup) { throw "nexus_config.json not found at $ConfigPath (start the HostService once to create it)." }
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, ADR-67 locked v0.17.0 + ADR-69 registered, selftest net-targets'
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
$adr67Locked = $adrText -match '(?m)^\|\s*ADR-67\s*\|.*Chốt `?v0\.17\.0'
$adr69 = $adrText -match '(?m)^\|\s*ADR-69\s*\|'
$self = Invoke-SelfTest 'net-targets' 'task17a'
$sData = $self.data
$e1Checks = [ordered]@{
    build       = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom     = [bool]$bom.Pass
    adr67Locked = [bool]$adr67Locked
    adr69       = [bool]$adr69
    selftest    = ($self.exitCode -eq 0) -and $self.noBom -and [bool]$sData.pass
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | ADR-67 locked {5}, ADR-69 {6} | selftest net-targets pass={7} failed=[{8}] | failed=[{9}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $adr67Locked, $adr69, $sData.pass, (@($sData.failed) -join ','), (Get-FailedNames $e1Checks))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: custom networkTargets + /reload (no restart) -> /net and MCP get_network_radar use them'
$hostProc = Restart-Host $true
$pid0 = $hostProc.Id
$null = Send-PipeRaw 'COMMANDMODE' $null
Start-Sleep -Milliseconds 300
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
Set-NetworkTargets @([ordered]@{ label = 'LOOPBACK-TEST'; host = '127.0.0.1' }, [ordered]@{ label = 'NITRO5-LAN'; host = '192.168.1.22' })
$reloadCustom = Send-PipeJson 'COCKPIT' '/reload'
$customRadar = Invoke-NetScan
$mcp = Invoke-Mcp @(@{ name = 'get_network_radar'; arguments = [ordered]@{} })
$mcpSnap = $mcp[2].snapshot
$loopLive = @($customRadar.pings | Where-Object { [string]$_.Label -eq 'LOOPBACK-TEST' }) | Select-Object -First 1
$loopMcp = @($mcpSnap.Pings | Where-Object { [string]$_.Label -eq 'LOOPBACK-TEST' }) | Select-Object -First 1
$sameHost = [bool](Get-Process -Id $pid0 -ErrorAction SilentlyContinue)
$e2Checks = [ordered]@{
    reloadOk       = [bool]$reloadCustom.ok -and ([string]$reloadCustom.message -like '*networkTargets=2 (config)*')
    netUsesConfig  = ([string]$(Get-UiState).task17.targetsSource -eq 'config') -and (@($customRadar.pings).Count -eq 2) -and ($null -ne $loopLive) -and [bool]$loopLive.Ok
    mcpUsesConfig  = ($null -ne $mcpSnap) -and ([string]$mcpSnap.TargetsSource -eq 'config') -and ($null -ne $loopMcp) -and [bool]$loopMcp.Ok -and ([int]$mcp[2].tokens -eq 0)
    noRestart      = $sameHost -and ((Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1).Id -eq $pid0)
    pingsCut       = ($null -ne $customRadar) -and ([double]$customRadar.PingMs -le 300)
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("/reload: '{0}' | /net: [{1}] in {2} ms (source {3}) | MCP: [{4}] (source {5}) | NITRO5 {6} | same HostService pid {7} | failed=[{8}]" -f `
    $reloadCustom.message, (($customRadar.pings | ForEach-Object { '{0} {1}' -f $_.Label, $(if ($_.Ok) { 'ONLINE' } else { 'OFFLINE' }) }) -join ', '), $customRadar.PingMs, (Get-UiState).task17.targetsSource,
    (($mcpSnap.Pings | ForEach-Object { '{0} {1}' -f $_.Label, $(if ($_.Ok) { 'ONLINE' } else { 'OFFLINE' }) }) -join ', '), $mcpSnap.TargetsSource, $customRadar.Nitro5State, $pid0, (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: networkTargets missing / [] / broken item -> /reload + /net -> the 4 defaults'
$defaults = 'Gateway|192.168.1.1', 'NITRO5-LAN|192.168.1.22', 'NITRO5-WIFI|192.168.1.192', 'LAN-TV|192.168.1.97'
$e3Cases = [ordered]@{}
foreach ($case in @('missing', 'empty', 'broken')) {
    switch ($case) {
        'missing' { Set-NetworkTargets $null }
        'empty'   { Set-NetworkTargets @() }
        'broken'  { Set-NetworkTargets @([ordered]@{ label = ''; host = $null }) }
    }
    $rl = Send-PipeJson 'COCKPIT' '/reload'
    $r = Invoke-NetScan
    $pairs = @($r.pings | ForEach-Object { '{0}|{1}' -f $_.Label, $_.Host })
    $e3Cases[$case] = [ordered]@{ reloadOk = [bool]$rl.ok; reload = [string]$rl.message; source = [string](Get-UiState).task17.targetsSource; pairs = $pairs
        ok = [bool]$rl.ok -and ([string]$rl.message -like '*networkTargets=4 (mặc định)*') -and (($pairs -join ';') -eq ($defaults -join ';')) -and ([string](Get-UiState).task17.targetsSource -eq 'default') }
}
[System.IO.File]::WriteAllBytes($ConfigPath, $configBackup)
$reloadBack = Send-PipeJson 'COCKPIT' '/reload'
$e3Checks = [ordered]@{
    selftestFallbacks = [bool]$sData.checks.'e3.missing' -and [bool]$sData.checks.'e3.empty' -and [bool]$sData.checks.'e3.brokenItem' -and [bool]$sData.checks.'e3.brokenFile' -and [bool]$sData.checks.'e3.mixedKeepsValid'
    liveMissing       = [bool]$e3Cases['missing'].ok
    liveEmpty         = [bool]$e3Cases['empty'].ok
    liveBrokenItem    = [bool]$e3Cases['broken'].ok
    userConfigBack    = [bool]$reloadBack.ok
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("missing -> [{0}] | [] -> [{1}] | broken item -> [{2}] | user config restored + reloaded {3} | failed=[{4}]" -f `
    ($e3Cases['missing'].pairs -join ', '), ($e3Cases['empty'].pairs -join ', '), ($e3Cases['broken'].pairs -join ', '), $reloadBack.ok, (Get-FailedNames $e3Checks))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: ui-footprint (/net + hiddenTrimmed), live idle [MEM] UI trims and their undo on wake, WAKE P50 <= 10 / P95 <= 16 ms'
Stop-HostService
$fp = Invoke-SelfTest 'ui-footprint' 'task16c'
$fpPhases = @($fp.data.phases)
$fpHidden = @($fpPhases | Where-Object { $_.phase -eq 'hidden' }) | Select-Object -First 1
$fpTrimmed = @($fpPhases | Where-Object { $_.phase -eq 'hiddenTrimmed' }) | Select-Object -First 1
$hostProc = Restart-Host $true
$null = Send-PipeRaw 'COMMANDMODE' $null
Start-Sleep -Milliseconds 300
$null = Send-PipeJson 'COCKPIT' '/ask! Gate 17a E4 câu trả lời giả lập cho phép dọn khi rảnh'
Start-Sleep -Milliseconds 800
$null = Send-PipeJson 'HISTORY' 'refresh'
$null = Invoke-NetScan
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$visibleBefore = (Get-UiState).task17.idleTrim
$visibleTrim = Wait-Ui { param($u) [int]$u.task17.idleTrim.trims -gt [int]$visibleBefore.trims } 9000
$vt = $visibleTrim.task17.idleTrim
$null = Send-PipeLine '{"cmd":"HIDE"}'
$hiddenTrim = Wait-Ui { param($u) [bool]$u.task17.idleTrim.liveReleased } 9000
$ht = $hiddenTrim.task17.idleTrim
$null = Send-PipeRaw 'COMMANDMODE' $null
Start-Sleep -Milliseconds 600
$woke = (Get-UiState).task17.idleTrim
$warmup = Get-WakeSample
$wakes = @()
for ($i = 0; $i -lt 10; $i++) { $wakes += Get-WakeSample }
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$wakeMs = @($wakes | Where-Object { $null -ne $_.ms } | ForEach-Object { [double]$_.ms })
$wakeP50 = Get-Percentile $wakeMs 50
$wakeP95 = Get-Percentile $wakeMs 95
$e4Checks = [ordered]@{
    footprintPhases = ($fp.exitCode -eq 0) -and [bool]$fp.data.pass -and (@($fpPhases | Where-Object { $_.phase -eq 'net' }).Count -eq 1) -and ($null -ne $fpTrimmed)
    visiblePoolTrim = ($null -ne $vt) -and [bool]$vt.poolShrunk -and ([int]$vt.pool -le 4) -and [bool]$vt.netPageReleased
    hiddenLiveTrim  = ($null -ne $ht) -and [bool]$ht.liveReleased
    wakeUndo        = ($null -ne $woke) -and (-not [bool]$woke.poolShrunk) -and (-not [bool]$woke.liveReleased)
    wakeP50         = ($null -ne $wakeP50) -and ($wakeP50 -le 10.0) -and ($wakeMs.Count -eq 10)
    wakeP95         = ($null -ne $wakeP95) -and ($wakeP95 -le 16.0)
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("ui-footprint hidden {0} MB -> after idle trim {1} MB (GC heap {2} -> {3}) | visible trim: pool {4}, net page released {5} | hidden trim: answer released {6} | after wake: pool shrunk {7}, answer released {8} | WAKE P50 {9} / P95 {10} ms ({11}) | failed=[{12}]" -f `
    $fpHidden.privateWsMb, $fpTrimmed.privateWsMb, $fpHidden.gcHeapMb, $fpTrimmed.gcHeapMb, $vt.pool, $vt.netPageReleased, $ht.liveReleased, $woke.poolShrunk, $woke.liveReleased,
    $wakeP50, $wakeP95, (($wakeMs | ForEach-Object { '{0:N2}' -f $_ }) -join ' / '), (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45,0 MB, idle CPU 0,0 %, regression Verify-Task17 (-> 16c ... 03, -SkipLive), git, CI'
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
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task17.ps1') @chainArgs | Out-Host
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
foreach ($n in @('task17', 'task16c', 'task15', 'task16b')) {
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
        -Headers @{ 'User-Agent' = 'nexus-verify-task17a'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45   = [bool](Test-NexusIdleRam $idleMb $memJson 'Task17a').pass
    idleCpuZero        = ([math]::Round($idleCpu, 1) -eq 0.0)
    noIdleTimer        = ($null -ne $idleUi) -and (-not [bool]$idleUi.visual.runningTimerEnabled) -and ($null -eq $idleUi.visual.running)
    regressionChain    = ($chainExit -eq 0) -and ($chainReports['task17'] -eq $true) -and ($chainReports['task16c'] -eq $true) -and ($chainReports['task15'] -eq $true) -and ($chainReports['task16b'] -eq $true)
    task16bUnder45     = ($chainRamOk['task16b'] -eq $true)   # Verify-Task16b E5: <= 45,0 or [SLA-WARN] <= 50,0 with GC heap <= 8,0
    zeroLiveCalls      = ($liveCalls.Count -eq 0) -and ($liveMissing.Count -eq 0)
    gitUnchanged       = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45,0; HUD {1}) | idle CPU {2} % ({3}) | Verify-Task17 chain exit {4} in {5} s (task17={6}, task16c={7}, task15={8}, task16b={9}; RAM 17={10} 16c={11} 15={12} 16b={13} MB) | live calls in chain: {14} (missing [{15}]) | git same={16} {17} | CI: {18} | failed=[{19}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $chainExit, $chainSeconds, $chainReports['task17'], $chainReports['task16c'], $chainReports['task15'], $chainReports['task16b'],
    $chainRam['task17'], $chainRam['task16c'], $chainRam['task15'], $chainRam['task16b'], $liveCalls.Count, (@($liveMissing | ForEach-Object { $_.report }) -join ','),
    $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-17a'; gate = 'Gate 17a'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; selftest = $sData }
    E2_TargetsReload = [ordered]@{ pass = $e2Pass; checks = $e2Checks; reload = $reloadCustom.message; radar = $customRadar; mcp = $mcpSnap; pid = $pid0 }
    E3_Fallbacks = [ordered]@{ pass = $e3Pass; checks = $e3Checks; cases = $e3Cases; restore = $reloadBack.message }
    E4_IdleTrimWake = [ordered]@{ pass = $e4Pass; checks = $e4Checks; footprint = $fp.data; visibleTrim = $vt; hiddenTrim = $ht; afterWake = $woke; warmup = $warmup; samples = $wakes; p50 = $wakeP50; p95 = $wakeP95 }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; chainExit = $chainExit; chainSeconds = $chainSeconds; chainReports = $chainReports; chainRamMb = $chainRam; liveAudit = $liveAudit; liveCallsInChain = $liveCalls.Count; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
$null = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 17a'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task17a: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; RAM 17a {5} MB, 16b {6} MB, WAKE P50 {7} / P95 {8} ms, live calls {9}" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $idleMb, $chainRam['task16b'], $wakeP50, $wakeP95, $liveCalls.Count) } })
Write-Host "`nGate 17a verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
