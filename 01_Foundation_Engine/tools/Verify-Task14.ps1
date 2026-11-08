#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub — Gate 14 automated verification (TASK-14 §4, ADR-59 Zero-Token Local Utilities).
.DESCRIPTION
    E1  Release build (-warnaserror, 0 warnings / 0 errors), Zero-BOM check (sources, handoff docs, config,
        zettelkasten/quick_notes.md), SQLite rows kept (>= 3,001), quick_notes.md git-ignored, Overwolf NOT in the default
        combatProcessPrefixes (selftest schema14).
    E2  /calc: mixed arithmetic, functions (sqrt(144) + 2^8), Hex/Bin (0xFF + 0b101), [CALC ERROR] guard (10 / 0, syntax),
        30 consecutive calls P50 <= 1.5 ms / P95 <= 2.5 ms (whole synchronous path incl. the ADR-14 write of
        latest_dispatch.json), live + mock token counters unchanged (selftest local-calc).
    E3  /timer: headless (local-timer: 250 ms -> ACTIVE -> EXPIRED within 400 ms, cancel all, 0 timer objects, idle CPU)
        + live HostService over the pipe: /timer 250ms with the Cockpit hidden -> Passive HUD alert (WS_EX_NOACTIVATE),
        foreground window identical before / after (HostService TIMERALERT and this script's own GetForegroundWindow),
        then /timer 60s ToCancel -> /timer cancel all -> /timer list empty.
    E4  /note: write (UTF-8 no BOM) -> /note list -> /note find 49152, Secret Guard on a dummy key, /ground (mock) right after
        the note returns it without a restart; test notes removed (selftest local-note).
    E5  MCP run_local_utility (calc) -> the running Cockpit picks it up in < 300 ms (SYNCSTATE latencyMs), MCP note with a
        dummy key refused, HostService Private WS < 60 MB and idle CPU < 1 %, regression Verify-Task11.ps1 -> Verify-Task03.ps1,
        and `git status --porcelain` empty at the end.
    TASK-14b (ADR-55, hardening a/b):
      -FullRegression (default): regression Verify-Task11.ps1 -> Verify-Task03.ps1 (release tags).
      -FastRegression: Verify-Task11.ps1 (Gate 11b, -SkipLive -SkipRegression, 4 CPU samples) + Verify-Task06.ps1
                       (config / state machine, -SkipRegression); no Gemini call; target < 90 s for the whole script.
      Git guard: branch and HEAD are captured at the start and checked after every step and at the end (with
      `git status --porcelain` clean); if they move, the script stops at once and the report says where.
      E5 cancels every Cockpit timer and waits 2 s (CPU settle window) before the idle CPU samples, and reports the
      Private WS against the soft warning (54 MB) and the hard limit (60 MB).
    Writes 03_Local_Data_Vault/handoff/task14_verification.json (UTF-8 without BOM; never contains an API key).
#>
param(
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$SkipLive,
    [switch]$FastRegression,
    [switch]$FullRegression
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot    = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot   = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir  = Join-Path $VaultRoot 'handoff'
$ConfigPath  = Join-Path $VaultRoot 'config\nexus_config.json'
$NotesPath   = Join-Path $VaultRoot 'zettelkasten\quick_notes.md'
$HostExe     = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe      = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath  = Join-Path $HandoffDir 'task14_verification.json'
$PipeName    = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom   = New-Object System.Text.UTF8Encoding($false)

$MockAnswer  = 'Gate 14 mock answer: cổng thử nghiệm lấy từ quick_notes.md (ADR-59).'
$TimerLabel  = 'Kiểm thử tự động Gate 14'
$DummyKey    = 'AIza' + 'SyDummySecretKeyForTest123456789'   # spec dummy key, assembled at run time

foreach ($v in 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

# ───────────────────────────── TASK-14b: regression mode, git guard, timing ─────────────────────────────
if ($FastRegression -and $FullRegression) { throw 'Use either -FastRegression or -FullRegression, not both.' }
$RegressionMode = if ($FastRegression) { 'Fast' } else { 'Full' }
# TASK-14b decision (0b): Fast = micro-latency SLAs warn up to 2.5x (inherited by every selftest and child script);
# Full / default = every SLA hard, so a variable left over from an earlier fast run is removed.
if ($RegressionMode -eq 'Fast') {
    $env:NEXUS_RELAXED_SLA = '1'
    Write-Host 'Relaxed SLA (FastRegression, decision 0b): micro-latency thresholds warn up to 2.5x; functional, data, security and memory checks stay hard.' -ForegroundColor Yellow
} elseif (Test-Path Env:NEXUS_RELAXED_SLA) {
    Remove-Item Env:NEXUS_RELAXED_SLA
}
$RamSoftWarnMb  = 54.0   # Gate 14 decision 4 (ADR-09): soft warning
$RamHardLimitMb = 60.0   # ADR-09 hard limit
$CpuSettleMs    = 2000   # hardening b: settle window before the idle CPU samples
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')
$ScriptWatch = [System.Diagnostics.Stopwatch]::StartNew()
$ScriptStartUtc = [DateTime]::UtcNow
$GitInitial  = Get-NexusGitState $RepoRoot
$GitChecks   = [System.Collections.Generic.List[object]]::new()
Write-Host ("Git guard: branch {0} @ {1} | regression mode: {2}" -f $GitInitial.Branch, $GitInitial.Head, $RegressionMode) -ForegroundColor Cyan

function Stop-IfGitMoved([string]$step) {
    $cmp = Compare-NexusGitState $GitInitial (Get-NexusGitState $RepoRoot)
    $GitChecks.Add([ordered]@{ step = $step; same = $cmp.Same; reasons = $cmp.Reasons })
    if ($cmp.Same) { return }
    Write-Host ("GIT GUARD: {0} after {1}. Results are no longer about the commit under test; stopping." -f ($cmp.Reasons -join '; '), $step) -ForegroundColor Red
    $abort = [ordered]@{
        task = 'TASK-14'; gate = 'Gate 14'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
        allPassed = $false; overallPass = $false; abortedAt = $step; regressionMode = $RegressionMode
        gitGuard = [ordered]@{ initial = $GitInitial; checks = $GitChecks; reasons = $cmp.Reasons }
        elapsedSeconds = [math]::Round($ScriptWatch.Elapsed.TotalSeconds, 1)
    }
    [System.IO.File]::WriteAllText($ReportPath, ($abort | ConvertTo-Json -Depth 8), $Utf8NoBom)
    exit 1
}

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

function Test-HasUtf8Bom([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    return ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
}

function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally {
        $client.Dispose()
    }
}

function Send-CockpitCommand([string]$text) {
    $req = ([ordered]@{ cmd = 'COCKPIT'; text = $text } | ConvertTo-Json -Compress)
    $outer = Send-PipeLine $req | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}

function Get-PipeJson([string]$cmd) {
    $outer = Send-PipeLine ('{"cmd":"' + $cmd + '"}') | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}

function Stop-HostService {
    # Graceful SHUTDOWN over the pipe only. Killing a process is Red tier (GEMINI.md): if HostService does not exit by
    # itself, stop here and let Nhat Hoa decide instead of Stop-Process -Force.
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 200
    }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Cockpit or Task Manager), then re-run Verify-Task14.ps1.'
    }
}

function Start-HostIfNeeded {
    $proc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
    $started = $false
    if (-not $proc) {
        Start-Process -FilePath $HostExe | Out-Null
        $started = $true
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        try { $null = Get-PipeJson 'SYNCSTATE'; break } catch { Start-Sleep -Milliseconds 300 }
    }
    if ($started) { Start-Sleep -Seconds 3 }
    return [pscustomobject]@{
        Process     = (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1)
        StartedHere = $started
    }
}

function Invoke-SelfTest([string]$scenario, [hashtable]$envOverrides = @{}) {
    $saved = @{}
    foreach ($k in $envOverrides.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
        [Environment]::SetEnvironmentVariable($k, $envOverrides[$k], 'Process')
    }
    $path = Join-Path $HandoffDir "task14_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    try {
        $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
        $code = $p.ExitCode
    } finally {
        foreach ($k in $envOverrides.Keys) {
            [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process')
        }
    }
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $code)" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    # TASK-14b (0b): micro-latency SLAs that only passed under -FastRegression jitter tolerance.
    foreach ($w in @($data.slaWarnings)) {
        if ($null -eq $w) { continue }
        Write-Host ("[SLA-WARN (FastRegression jitter)] {0}: {1} = {2} ms (SLA {3} ms, ceiling {4} ms) -> PASS with warning" -f `
            $scenario, $w.metric, $w.measuredMs, $w.slaMs, $w.ceilingMs) -ForegroundColor Yellow
    }
    return [ordered]@{
        exitCode = $code
        noBom    = -not (Test-HasUtf8Bom $path)
        data     = $data
    }
}

function Invoke-Mcp([string[]]$requests) {
    $OutputEncoding = $Utf8NoBom   # Windows PowerShell 5.1 pipes to native programs as ASCII by default
    $all = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}') + $requests
    $lines = (($all -join "`n") + "`n") | & $McpExe
    $byId = @{}
    foreach ($l in $lines) {
        if ([string]::IsNullOrWhiteSpace($l)) { continue }
        $r = $l | ConvertFrom-Json
        if ($null -ne $r.id) { $byId[[int]$r.id] = $r }
    }
    return $byId
}

function Format-Stats($s) {
    if ($null -eq $s) { return 'n/a' }
    return ("P50={0} P95={1} ms (min {2}, max {3}) [{4}]" -f $s.p50, $s.p95, $s.min, $s.max, (@($s.samples) -join ', '))
}

function Get-FailedChecks($checks) {
    return (($checks.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object { $_.Name }) -join ',')
}

# This script's own view of the foreground window (independent of HostService's TIMERALERT evidence).
$script:FgApi = $false
try {
    if (-not ('NexusGate14.User32' -as [type])) {
        Add-Type -Namespace NexusGate14 -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'
    }
    $script:FgApi = $true
} catch {
    Write-Host "GetForegroundWindow from the script is unavailable ($($_.Exception.Message)); E3 relies on TIMERALERT only." -ForegroundColor Yellow
}
function Get-Foreground {
    if (-not $script:FgApi) { return $null }
    return ('0x{0:X}' -f [NexusGate14.User32]::GetForegroundWindow().ToInt64())
}

# ───────────────────────────────────────── E1: Build, Zero-BOM & schema14 ─────────────────────────────────────────
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, SQLite rows (>= 3,001), quick_notes.md git-ignored'
$buildWarnings = 0
$buildErrors   = 0
$buildCode     = 0
Stop-HostService
if (-not $SkipBuild) {
    $buildLines = & dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1
    $buildCode = $LASTEXITCODE
    $buildText = ($buildLines | Out-String)
    Write-Host $buildText.Trim()
    if ($buildText -match '(\d+)\s+Warning\(s\)') { $buildWarnings = [int]$Matches[1] }
    if ($buildText -match '(\d+)\s+Error\(s\)')   { $buildErrors   = [int]$Matches[1] }
}

$bomTargets = @(Get-ChildItem -Path $EngineRoot -Recurse -Include *.cs,*.xaml,*.csproj -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName })
$bomTargets += @(Get-ChildItem -Path $HandoffDir -Filter *.md -File | ForEach-Object { $_.FullName })
$bomTargets += @((Join-Path $RepoRoot 'GEMINI.md'), (Join-Path $RepoRoot 'CLAUDE.md'), (Join-Path $RepoRoot 'ROADMAP_FUTURE.md'),
                 (Join-Path $RepoRoot 'docs\ADR_REGISTRY.md'), $ConfigPath, $NotesPath) | Where-Object { Test-Path $_ }
$bomOffenders = @($bomTargets | Where-Object { Test-HasUtf8Bom $_ })

$gitIgnored = $false
try {
    $null = & git -C $RepoRoot check-ignore -q -- '03_Local_Data_Vault/zettelkasten/quick_notes.md'
    $gitIgnored = ($LASTEXITCODE -eq 0)
} catch { $gitIgnored = $false }

$schema14 = Invoke-SelfTest 'schema14'
$e1Pass = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0) -and ($bomOffenders.Count -eq 0) -and
          $gitIgnored -and ($schema14.exitCode -eq 0) -and $schema14.noBom -and [bool]$schema14.data.pass
Write-Host ("E1: buildCode={0} warnings={1} errors={2} bomOffenders={3} rows={4} (>= 3001: {5}) quickNotesIgnored={6} overwolfInDefault={7} (must be False, decision 5) overwolfInConfigFile={8} -> {9}" -f `
    $buildCode, $buildWarnings, $buildErrors, $bomOffenders.Count, $schema14.data.rows, $schema14.data.rowsPreserved, $gitIgnored,
    $schema14.data.overwolfInDefaultCombatPrefixes, $schema14.data.overwolfInConfigFile,
    $(if ($e1Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e1Pass) { 'Green' } else { 'Red' })

Stop-IfGitMoved 'E1'

# ───────────────────────────────────────── E2: /calc ─────────────────────────────────────────
Write-Step 'E2: /calc precision, Hex/Bin, [CALC ERROR] guard, 30 calls P50 <= 1.5 ms / P95 <= 2.5 ms, 0 tokens'
$e2 = Invoke-SelfTest 'local-calc'
$e2Pass = ($e2.exitCode -eq 0) -and $e2.noBom -and [bool]$e2.data.pass
foreach ($c in @($e2.data.cases | Select-Object -First 3)) { Write-Host ("  {0}" -f $c.message) }
Write-Host ("  {0}" -f (@($e2.data.errors | Where-Object { $_.input -eq '/calc 10 / 0' } | Select-Object -First 1).message))
Write-Host ("E2: /calc total {0}" -f (Format-Stats $e2.data.calcLatency.total))
Write-Host ("E2: /calc compute only {0}" -f (Format-Stats $e2.data.calcLatency.compute))
Write-Host ("E2: tokens live {0}->{1}, mock {2}->{3}, session {4}->{5}, dispatch rows {6}->{7} | failedChecks=[{8}] -> {9}" -f `
    $e2.data.tokens.before.liveDailyTotal, $e2.data.tokens.after.liveDailyTotal, $e2.data.tokens.before.mockDaily, $e2.data.tokens.after.mockDaily,
    $e2.data.tokens.before.sessionTotal, $e2.data.tokens.after.sessionTotal, $e2.data.tokens.before.dispatchRows, $e2.data.tokens.after.dispatchRows,
    (Get-FailedChecks $e2.data.checks), $(if ($e2Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e2Pass) { 'Green' } else { 'Red' })

Stop-IfGitMoved 'E2'

# ───────────────────────────────────────── E3: /timer ─────────────────────────────────────────
Write-Step 'E3: /timer 250ms -> EXPIRED (Passive HUD, foreground unchanged), /timer 60s -> cancel all -> empty'
$e3Self = Invoke-SelfTest 'local-timer'
$e3SelfPass = ($e3Self.exitCode -eq 0) -and $e3Self.noBom -and [bool]$e3Self.data.pass
Write-Host ("E3 (headless): fired after {0} ms (budget 400) | {1} | idle CPU {2}% with {3} timer objects | failedChecks=[{4}]" -f `
    $e3Self.data.timer250.firedAfterMs, $e3Self.data.timer250.expectedMessage, $e3Self.data.idle.idleCpuPct,
    $e3Self.data.idle.activeTimerObjects, (Get-FailedChecks $e3Self.data.checks))

# Long-running HostService WITHOUT mock variables (claude_shift_handover.md rule 1).
$hostInfo = Start-HostIfNeeded
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 300
$alertBefore   = Get-PipeJson 'TIMERALERT'
$fgScriptBefore = Get-Foreground
$liveStart     = Send-CockpitCommand "/timer 250ms $TimerLabel"
$liveList      = Send-CockpitCommand '/timer list'
Start-Sleep -Milliseconds 400
$alert = $null
$deadline = (Get-Date).AddMilliseconds(1500)
do {
    $alert = Get-PipeJson 'TIMERALERT'
    if (([long]$alert.count -gt [long]$alertBefore.count) -and [bool]$alert.afterRenderChecked) { break }
    Start-Sleep -Milliseconds 100
} while ((Get-Date) -lt $deadline)
$fgScriptAfter = Get-Foreground
$liveListAfter = Send-CockpitCommand '/timer list'
$liveToCancel  = Send-CockpitCommand '/timer 60s ToCancel'
$liveCancelAll = Send-CockpitCommand '/timer cancel all'
$liveEmpty     = Send-CockpitCommand '/timer list'
$null = Send-PipeLine '{"cmd":"HIDE"}'

$expectedAlert = '[TIMER EXPIRED] #' + [string]$liveStart.localData.started.id + ': ' + $TimerLabel + ' (250ms)'
$scriptFgSame = (-not $script:FgApi) -or ($fgScriptBefore -eq $fgScriptAfter)
$e3LiveChecks = [ordered]@{
    startOk             = [bool]$liveStart.ok -and ($liveStart.kind -eq 'timer')
    listActiveOk        = [bool]$liveList.ok -and ([string]$liveList.message -like '*ACTIVE*') -and ([string]$liveList.message -like "*$TimerLabel*")
    alertRaised         = ([long]$alert.count -gt [long]$alertBefore.count) -and ([string]$alert.message -eq $expectedAlert)
    shownAsPassiveHud   = [bool]$alert.cockpitWasHidden -and [bool]$alert.shownPassive -and [bool]$alert.noActivate -and [bool]$alert.visibleNow
    foregroundUnchanged = [bool]$alert.foregroundUnchanged -and [bool]$alert.afterRenderChecked -and (-not [bool]$alert.foregroundIsCockpit)
    scriptForegroundSame = $scriptFgSame
    listShowsExpired    = [bool]$liveListAfter.ok -and ([string]$liveListAfter.message -like '*EXPIRED*')
    cancelAllOk         = [bool]$liveToCancel.ok -and [bool]$liveCancelAll.ok -and ([string]$liveCancelAll.message -like '*1*') -and
                          [bool]$liveEmpty.ok -and ([string]$liveEmpty.message -like '`[TIMER`] 0 *')
}
$e3LivePass = -not ($e3LiveChecks.Values -contains $false)
$e3Pass = $e3SelfPass -and $e3LivePass
Write-Host ("E3 (live): {0} | alert: hidden={1} passive={2} noActivate={3} style={4} | foreground before={5} after={6} afterRender={7} (script {8} -> {9}) | alertMs={10}" -f `
    $alert.message, $alert.cockpitWasHidden, $alert.shownPassive, $alert.noActivate, $alert.hudStyle,
    $alert.foregroundBefore, $alert.foregroundAfter, $alert.foregroundAfterRender, $fgScriptBefore, $fgScriptAfter, $alert.alertMs)
Write-Host ("E3: {0} | {1}" -f $liveCancelAll.message, $liveEmpty.message)
Write-Host ("E3: headless={0} live failed=[{1}] -> {2}" -f $e3SelfPass,
    (($e3LiveChecks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','),
    $(if ($e3Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e3Pass) { 'Green' } else { 'Red' })

Stop-IfGitMoved 'E3'

# ───────────────────────────────────────── E4: /note ─────────────────────────────────────────
Write-Step 'E4: /note -> list -> find 49152, Secret Guard, /ground (mock) sees the note at once'
$e4 = Invoke-SelfTest 'local-note' @{ NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
$e4Pass = ($e4.exitCode -eq 0) -and $e4.noBom -and [bool]$e4.data.pass
Write-Host ("  {0}" -f $e4.data.write.message)
Write-Host ("  {0}" -f $e4.data.find)
Write-Host ("  {0}" -f $e4.data.secretGuard.cockpit)
Write-Host ("E4: /ground sources=[{0}] noteSource={1} contextHasNote={2} | key in file={3} latest={4} sqlite={5} | notes {6} -> {7} (removed {8}) | failedChecks=[{9}] -> {10}" -f `
    (@($e4.data.ground.sources) -join ', '), $e4.data.ground.noteSource, $e4.data.ground.contextHasNote,
    $e4.data.secretGuard.keyInNotesFile, $e4.data.secretGuard.keyInLatestDispatch, $e4.data.secretGuard.sqliteMentions,
    $e4.data.file.countBefore, $e4.data.file.countAfter, $e4.data.file.removed,
    (Get-FailedChecks $e4.data.checks), $(if ($e4Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e4Pass) { 'Green' } else { 'Red' })

Stop-IfGitMoved 'E4'

# ───────────────────────────────────────── E5: MCP live sync, resources, regression, git ─────────────────────────────
Write-Step 'E5: MCP run_local_utility -> Cockpit < 300 ms, RAM (< 60 MB), CPU (< 1%), regression 11..03, git status clean'
$hostInfo = Start-HostIfNeeded
$calcReq = ([ordered]@{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = [ordered]@{ name = 'run_local_utility'; arguments = [ordered]@{ utility = 'calc'; commandText = 'sqrt(144) + 2^8' } } } | ConvertTo-Json -Compress -Depth 6)
$noteReq = ([ordered]@{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = [ordered]@{ name = 'run_local_utility'; arguments = [ordered]@{ utility = 'note'; commandText = "Khóa thử nghiệm $DummyKey" } } } | ConvertTo-Json -Compress -Depth 6)
$mcp = Invoke-Mcp @('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}', $calcReq, $noteReq)
$toolNames = @($mcp[2].result.tools | ForEach-Object { $_.name })
$mcpCalc   = [string]$mcp[3].result.content[0].text | ConvertFrom-Json
$mcpNote   = [string]$mcp[4].result.content[0].text | ConvertFrom-Json

$sync = $null
$deadline = (Get-Date).AddSeconds(2)
do {
    Start-Sleep -Milliseconds 100
    $sync = Get-PipeJson 'SYNCSTATE'
    if (($sync.localUtility -eq 'calc') -and ($sync.source -eq 'McpTools') -and ([string]$sync.updatedAtUtc -eq [string]$mcpCalc.latestDispatchUpdatedAtUtc)) { break }
} while ((Get-Date) -lt $deadline)
$syncOk = ($sync.localUtility -eq 'calc') -and ($sync.source -eq 'McpTools') -and ([string]$sync.updatedAtUtc -eq [string]$mcpCalc.latestDispatchUpdatedAtUtc) -and
          ($null -ne $sync.latencyMs) -and ([double]$sync.latencyMs -lt 300) -and ([string]$sync.localResult -like '*= 268 (Hex: 0x10C*') -and
          ($sync.aiModel -eq 'local-utility') -and ([int]$sync.totalTokens -eq 0)
$notesText = if (Test-Path $NotesPath) { [System.IO.File]::ReadAllText($NotesPath, $Utf8NoBom) } else { '' }
$mcpOk = ($toolNames -contains 'run_local_utility') -and [bool]$mcpCalc.ok -and ($mcpCalc.status -eq 'PASSED') -and
         ([int]$mcpCalc.totalTokens -eq 0) -and ($mcpCalc.aiModel -eq 'local-utility') -and ([string]$mcpCalc.message -like '*= 268 (Hex: 0x10C | Bin: 0b100001100)*') -and
         [bool]$mcp[4].result.isError -and [bool]$mcpNote.blocked -and (-not $notesText.Contains('SyDummySecretKeyForTest'))

$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
# Hardening b: no Cockpit timer may still be running, then a settle window so the E3/E4 alerts and file I/O are over.
$settleCancel = Send-CockpitCommand '/timer cancel all'
$settleList   = Send-CockpitCommand '/timer list'
$timersIdle   = [bool]$settleList.ok -and ([string]$settleList.message -like '`[TIMER`] 0 *')
Start-Sleep -Milliseconds $CpuSettleMs
$cpuSamples = @()
for ($i = 0; $i -lt 10; $i++) {
    $hostProc.Refresh()
    $c0 = $hostProc.TotalProcessorTime
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 1
    $hostProc.Refresh()
    $cpuSamples += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
}
$last5AvgCpu = [math]::Round(($cpuSamples | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
# TASK-13a: Private WS read after the idle samples, i.e. the idle steady state (the Cockpit's one-shot idle trim runs
# 5 s after the last command, inside the first CPU samples and outside the averaged last five).
$hostProc.Refresh()
$perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
$privateWsMb  = if ($perf) { [math]::Round($perf.WorkingSetPrivate / 1MB, 1) } else { [math]::Round($hostProc.WorkingSet64 / 1MB, 1) }
$workingSetMb = [math]::Round($hostProc.WorkingSet64 / 1MB, 1)
$machineCpu = $null
try { $machineCpu = [math]::Round((Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average, 0) } catch { }
$ramOk = ($privateWsMb -lt $RamHardLimitMb)
$ramSoftWarning = ($privateWsMb -ge $RamSoftWarnMb)
$cpuOk = ($last5AvgCpu -lt 1.0)
$cpuUnderHalf = ($last5AvgCpu -lt 0.5)
Write-Host ("E5: RAM Private WS {0} MB | soft warning {1} MB: {2} | hard limit {3} MB: {4}" -f $privateWsMb, $RamSoftWarnMb,
    $(if ($ramSoftWarning) { 'WARNING (at or above)' } else { 'ok' }), $RamHardLimitMb, $(if ($ramOk) { 'ok' } else { 'EXCEEDED' })) `
    -ForegroundColor $(if (-not $ramOk) { 'Red' } elseif ($ramSoftWarning) { 'Yellow' } else { 'Green' })

$regPass = $true
$regExit = 0
$regDetail = 'regression skipped (-SkipRegression)'
$regSeconds = 0
$regScripts = @()
if (-not $SkipRegression) {
    $regWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $flag = {
        param($name)
        $p = Join-Path $HandoffDir "${name}_verification.json"
        (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    }
    $flags = [ordered]@{}
    if ($RegressionMode -eq 'Fast') {
        # ADR-55 fast tier: previous gate (11b) without network or chain + one foundation gate (06: config / state machine).
        foreach ($t in 'task11', 'task06') {
            $p = Join-Path $HandoffDir "${t}_verification.json"
            if (Test-Path $p) { Remove-Item $p -Force }   # a stale PASS from an earlier run must not count
        }
        $regScripts = @('Verify-Task11.ps1 -SkipBuild -SkipLive -SkipRegression -CpuSamples 4', 'Verify-Task06.ps1 -SkipBuild -SkipRegression')
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task11.ps1') -SkipBuild -SkipLive -SkipRegression -CpuSamples 4 | Out-Host
        $exit11 = $LASTEXITCODE
        Stop-IfGitMoved 'regression Verify-Task11'
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task06.ps1') -SkipBuild -SkipRegression | Out-Host
        $exit06 = $LASTEXITCODE
        $regExit = if ($exit11 -ne 0) { $exit11 } else { $exit06 }
        foreach ($t in 'task11', 'task06') { $flags[$t] = & $flag $t }
    } else {
        $regScripts = @('Verify-Task11.ps1 -SkipBuild' + $(if ($SkipLive) { ' -SkipLive' } else { '' }) + ' (chains 10 -> 03)')
        $regArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Verify-Task11.ps1'), '-SkipBuild')
        if ($SkipLive) { $regArgs += '-SkipLive' }
        & powershell @regArgs | Out-Host
        $regExit = $LASTEXITCODE
        foreach ($t in 'task11', 'task10', 'task09', 'task08', 'task07', 'task06', 'task05', 'task04', 'task03') { $flags[$t] = & $flag $t }
    }
    $regPass = ($regExit -eq 0) -and -not ($flags.Values -contains $false)
    $regDetail = "${RegressionMode}: " + (($flags.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join ', ')
    $regSeconds = [math]::Round($regWatch.Elapsed.TotalSeconds, 1)
}
Stop-IfGitMoved 'regression'

$gitLines = @()
$gitOk = $false
try {
    $gitLines = @(& git -C $RepoRoot status --porcelain | ForEach-Object { [string]$_ } | Where-Object { $_.Trim().Length -gt 0 })
    $gitOk = ($LASTEXITCODE -eq 0) -and ($gitLines.Count -eq 0)
} catch { $gitLines = @("git failed: $($_.Exception.Message)") }

# Hardening a: final guard, branch + HEAD unchanged since the start and the tree clean.
$gitFinal = Compare-NexusGitState $GitInitial (Get-NexusGitState $RepoRoot) -RequireClean
$GitChecks.Add([ordered]@{ step = 'final'; same = $gitFinal.Same; reasons = $gitFinal.Reasons })

$e5Pass = $mcpOk -and $syncOk -and $ramOk -and $cpuOk -and $regPass -and $gitOk -and $gitFinal.Same -and $timersIdle
Write-Host ("E5: MCP calc: {0} | Cockpit SYNCSTATE localUtility={1} source={2} latencyMs={3} (<300={4}) | MCP note with key: blocked={5}" -f `
    $mcpCalc.message, $sync.localUtility, $sync.source, $sync.latencyMs, $syncOk, $mcpNote.blocked)
Write-Host ("E5: privateWsMb={0}MB (<60={1}) workingSetMb={2}MB idleCpuLast5={3}% (<1%={4}) machineCpu={5}% | reg: {6} | git status clean={7} [{8}] -> {9}" -f `
    $privateWsMb, $ramOk, $workingSetMb, $last5AvgCpu, $cpuOk, $machineCpu, $regDetail, $gitOk, ($gitLines -join '; '),
    $(if ($e5Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e5Pass) { 'Green' } else { 'Red' })
Write-Host ("E5: CPU settle {0} ms after /timer cancel all (timers idle={1}) | idle CPU < 0.5 % = {2} | git guard final: same={3} {4} | regression {5} s | script {6} s" -f `
    $CpuSettleMs, $timersIdle, $cpuUnderHalf, $gitFinal.Same, ($gitFinal.Reasons -join '; '), $regSeconds, [math]::Round($ScriptWatch.Elapsed.TotalSeconds, 1))

$overallPass = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
# TASK-13a (ADR-55 drift tracking): summary of every [SLA-WARN] of this run (fast tier only; Full has no tolerance).
$runSlaWarnings = @()
if ($RegressionMode -eq 'Fast') {
    $runSlaWarnings = @(Get-NexusSlaWarnings $HandoffDir $ScriptStartUtc)
    $null = Write-NexusSlaSummary $runSlaWarnings
}
$report = [ordered]@{
    task        = 'TASK-14'
    gate        = 'Gate 14'
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    machine     = $env:COMPUTERNAME
    allPassed   = $overallPass
    overallPass = $overallPass
    regressionMode = $RegressionMode
    relaxedSla  = ($RegressionMode -eq 'Fast')
    slaWarningSummary = [ordered]@{ count = @($runSlaWarnings).Count; details = @($runSlaWarnings) }
    elapsedSeconds = [math]::Round($ScriptWatch.Elapsed.TotalSeconds, 1)
    gitGuard    = [ordered]@{ initial = $GitInitial; checks = $GitChecks; finalSame = $gitFinal.Same; finalReasons = $gitFinal.Reasons }
    E1_BuildZeroBomAndSchema14 = [ordered]@{
        pass              = $e1Pass
        buildExitCode     = $buildCode
        buildWarnings     = $buildWarnings
        buildErrors       = $buildErrors
        bomOffenders      = $bomOffenders
        quickNotesIgnored = $gitIgnored
        schema14          = $schema14.data
    }
    E2_LocalCalc = [ordered]@{
        pass      = $e2Pass
        localCalc = $e2.data
    }
    E3_LocalTimer = [ordered]@{
        pass        = $e3Pass
        headless    = $e3Self.data
        live        = [ordered]@{
            checks                 = $e3LiveChecks
            start                  = $liveStart.message
            listWhileActive        = $liveList.message
            alert                  = $alert
            scriptForegroundBefore = $fgScriptBefore
            scriptForegroundAfter  = $fgScriptAfter
            listAfter              = $liveListAfter.message
            cancelAll              = $liveCancelAll.message
            listEmpty              = $liveEmpty.message
        }
    }
    E4_LocalNote = [ordered]@{
        pass      = $e4Pass
        localNote = $e4.data
    }
    E5_McpSyncResourcesRegression = [ordered]@{
        pass                = $e5Pass
        mcpToolListed       = ($toolNames -contains 'run_local_utility')
        mcpCalc             = $mcpCalc
        mcpNoteBlocked      = [bool]$mcpNote.blocked
        cockpitSync         = $sync
        cockpitSyncOk       = $syncOk
        privateWorkingSetMb = $privateWsMb
        workingSetMb        = $workingSetMb
        ramUnder60Mb        = $ramOk
        cpuSamplesPct       = $cpuSamples
        idleCpuLast5AvgPct  = $last5AvgCpu
        idleCpuUnder1Pct    = $cpuOk
        idleCpuUnderHalfPct = $cpuUnderHalf
        cpuSettleMs         = $CpuSettleMs
        timersIdleBeforeCpu = $timersIdle
        ramSoftWarnMb       = $RamSoftWarnMb
        ramHardLimitMb      = $RamHardLimitMb
        ramSoftWarning      = $ramSoftWarning
        machineCpuPct       = $machineCpu
        regression          = [ordered]@{ ran = (-not $SkipRegression); mode = $RegressionMode; scripts = $regScripts; skipLive = [bool]$SkipLive; exitCode = $regExit; detail = $regDetail; seconds = $regSeconds; pass = $regPass }
        gitStatusClean      = $gitOk
        gitStatusLines      = $gitLines
    }
}

[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 12), $Utf8NoBom)
Write-Host "`nGate 14 verification report written to $ReportPath (allPassed=$overallPass)" -ForegroundColor $(if ($overallPass) { 'Green' } else { 'Red' })
if (-not $overallPass) { exit 1 }
exit 0
