<#
.SYNOPSIS
    TASK-05 / Gate 5 verification (E1-E7) for NEXUS Home Hub - Dev-QA loop from the Cockpit.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror + agent_dispatch_logs has retry_count / evidence_notes / updated_at_utc,
        CHECK accepts ESCALATED, and every row of the pre-task05 backup is still in the live table (no data lost)
    E2  Cockpit dispatch -> "/pass Kiểm thử UDP đạt chuẩn" -> SQLite row and latest_dispatch.json = PASSED + evidence
    E3  "/fail" x3 -> NEEDS_WORK (1), NEEDS_WORK (2), ESCALATED (3) + exactly one [3-STRIKE ESCALATION] line in
        qa_report.md. qa_report.md is tracked by git, so the script restores it afterwards (the line is kept in
        task05_selftest_devqa-strikes.json); use -KeepQaReportEntry to leave the line in the file.
    E4  McpTools update_dispatch_verdict (JSON-RPC stdio) -> latest_dispatch.json -> the running Cockpit's
        FileSystemWatcher picks it up in < 300 ms (latency measured by the Cockpit, read back via pipe SYNCSTATE)
    E5  GameBoost Access Denied branch (simulated Win32Exception 5, dry run) -> "ACE-Guard Protected (Background Yield
        Active)" without an exception; Ctrl+Shift+Space handler (pipe COMMANDMODE, same code path) < 35 ms
    E6  MANUAL (printed checklist) on the MSI G272
    E7  Running HostService: private working set < 60 MB, idle CPU < 1 %; regression Verify-Task04 -SkipBuild
        (which also runs Verify-Task03) must pass
    Results: 03_Local_Data_Vault/handoff/task05_verification.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task05.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$KeepQaReportEntry,
    [int]$SyncRuns = 3,
    [int]$LatencyRuns = 6
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8

$engine   = Join-Path $Root '01_Foundation_Engine'
$sln      = Join-Path $engine 'NEXUSHomeHub.slnx'
$mcpExe   = Join-Path $engine 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$hostExe  = Join-Path $engine 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$handoff  = Join-Path $Root '03_Local_Data_Vault\handoff'
$qaReport = Join-Path $handoff 'qa_report.md'
$latest   = Join-Path $handoff 'latest_dispatch.json'
$pipeName = 'NEXUS_Home_Cockpit_Pipe'

$results = [ordered]@{}
function Set-Result($id, [bool]$pass, $detail) {
    $results[$id] = [ordered]@{ pass = $pass; detail = $detail }
    $color = if ($pass) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} - {2}" -f $id, ($(if ($pass) { 'PASS' } else { 'FAIL' })), $detail) -ForegroundColor $color
}

function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $utf8); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $utf8)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}

# {"cmd":"X"} -> inner "result" string of the JSONL answer
function Invoke-Pipe([string]$cmd, [int]$timeoutMs = 5000) {
    $resp = Send-PipeLine (([ordered]@{ cmd = $cmd }) | ConvertTo-Json -Compress) $timeoutMs | ConvertFrom-Json
    return [string]$resp.result
}

function Get-SyncState { return (Invoke-Pipe 'SYNCSTATE') | ConvertFrom-Json }

function Invoke-SelfTest([string]$scenario, [string[]]$extra = @()) {
    $out = Join-Path $handoff "task05_selftest_$scenario.json"
    if (Test-Path $out) { Remove-Item $out }
    $p = Start-Process -FilePath $hostExe -ArgumentList (@('--selftest', $scenario) + $extra) -PassThru -Wait
    if (-not (Test-Path $out)) { return [pscustomobject]@{ pass = $false; error = "no report (exit code $($p.ExitCode))" } }
    return (Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function New-ToolCall([int]$id, [string]$name, $arguments) {
    return ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $name; arguments = $arguments } } | ConvertTo-Json -Compress -Depth 8)
}

# One McpTools session (initialize + calls) -> responses keyed by id
function Invoke-Mcp([string[]]$calls) {
    $requests = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}') + $calls
    $lines = (($requests -join "`n") + "`n") | & $mcpExe
    $byId = @{}
    foreach ($l in $lines) {
        if ([string]::IsNullOrWhiteSpace($l)) { continue }
        $r = $l | ConvertFrom-Json
        if ($null -ne $r.id) { $byId[[int]$r.id] = $r }
    }
    return $byId
}

function Get-ToolText($resp) {
    if ($null -eq $resp) { return $null }
    if ($resp.error) { return "ERROR: $($resp.error.message)" }
    return [string]$resp.result.content[0].text
}

function Start-HostIfNeeded {
    $proc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
    $started = $false
    if (-not $proc) {
        Start-Process -FilePath $hostExe | Out-Null
        $started = $true
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        try { $null = Get-SyncState; break } catch { Start-Sleep -Milliseconds 300 }
    }
    $proc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
    return [pscustomobject]@{ Process = $proc; StartedHere = $started }
}

Write-Host "NEXUS Home Hub - TASK-05 verification (root: $Root)" -ForegroundColor Cyan

# ---------------- E1: build + schema migration ----------------
if ($SkipBuild) {
    $buildOk = Test-Path $hostExe
    $buildDetail = 'Build skipped (-SkipBuild)'
} else {
    # A running HostService locks its .exe; ask it to exit gracefully through its own pipe first.
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        Write-Host 'HostService is running: sending SHUTDOWN through its pipe so the build can replace the .exe...' -ForegroundColor Yellow
        try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { Write-Host "  pipe SHUTDOWN failed: $($_.Exception.Message)" -ForegroundColor Yellow }
        Start-Sleep -Seconds 2
    }
    $buildOut = & dotnet build $sln -c Release -warnaserror 2>&1
    $buildOut | Out-Host
    $buildOk = ($LASTEXITCODE -eq 0)
    $buildDetail = "dotnet build -warnaserror exit=$LASTEXITCODE (" + (($buildOut | Select-String -Pattern 'Warning\(s\)|Error\(s\)' | ForEach-Object { $_.Line.Trim() }) -join '; ') + ")"
}
if (-not (Test-Path $hostExe)) { throw "HostService not found: $hostExe" }
$e1 = Invoke-SelfTest 'schema'
$mig = $e1.migrationThisProcess
Set-Result 'E1' ($buildOk -and [bool]$e1.pass) ("{0} | columns ok={1} (missing: {2}) CHECK has ESCALATED={3} rows={4} | migration here: rebuilt={5} added=[{6}] rows {7}->{8} error={9} | pre-task05 backup={10} rows={11} missing in live table={12}" -f `
    $buildDetail, (@($e1.missingColumns).Count -eq 0), (@($e1.missingColumns) -join ','), $e1.checkAllowsEscalated, $e1.rows,
    $mig.rebuilt, (@($mig.addedColumns) -join ','), $mig.rowsBefore, $mig.rowsAfter, $mig.error,
    $(if ($e1.preTask05Backup) { Split-Path $e1.preTask05Backup -Leaf } else { 'none' }), $e1.backupRows, $e1.backupRowsMissing)

# ---------------- E2: /pass ----------------
$e2 = Invoke-SelfTest 'devqa-pass'
Set-Result 'E2' ([bool]$e2.pass) ("dispatch #{0} {1} -> '{2}' | DB status={3} retry={4} evidence='{5}' | latest_dispatch.json status={6} evidence='{7}' valid={8} | bad /verdict rejected={9} unknown id rejected={10}" -f `
    $e2.dispatchId, $e2.squadId, $e2.command, $e2.db.status, $e2.db.retryCount, $e2.db.evidenceNotes,
    $e2.latestAfterPass.status, $e2.latestAfterPass.evidenceNotes, $e2.latestAfterPass.valid, $e2.badVerdictRejected, $e2.unknownIdRejected)

# ---------------- E3: /fail x3 ----------------
$qaBackup = if (Test-Path $qaReport) { [System.IO.File]::ReadAllBytes($qaReport) } else { $null }
$e3 = Invoke-SelfTest 'devqa-strikes'
$strikeText = (@($e3.strikes) | ForEach-Object { "{0}:{1}/r{2}" -f $_.strike, $_.status, $_.retryCount }) -join ' '
$escLine = @($e3.escalationLines) | Select-Object -First 1
if (-not $KeepQaReportEntry -and $null -ne $qaBackup) {
    [System.IO.File]::WriteAllBytes($qaReport, $qaBackup)
    $qaNote = 'qa_report.md restored (line kept in task05_selftest_devqa-strikes.json; -KeepQaReportEntry to keep it)'
} else { $qaNote = 'qa_report.md entry kept' }
Set-Result 'E3' ([bool]$e3.pass) ("dispatch #{0} {1} | {2} | 4th /fail no duplicate={3} | latest status={4} r{5} | escalation lines={6}: {7} | alias /needswork + /verdict -> '{8}' | {9}" -f `
    $e3.dispatchId, $e3.squadId, $strikeText, ($e3.fourthStrike.escalatedNow -eq $false), $e3.latestAfterStrike3.status, $e3.latestAfterStrike3.retryCount,
    @($e3.escalationLines).Count, $escLine, $e3.aliasTest.label, $qaNote)

# ---------------- E4: MCP -> Cockpit live sync ----------------
$hostInfo = Start-HostIfNeeded
$e4Runs = @(); $e4Errors = @()
if (-not $hostInfo.Process) {
    $e4Errors += 'HostService not running / pipe not ready'
} else {
    # One MCP session creates the dispatches (also exercises dispatch -> latest_dispatch.json from McpTools).
    $calls = @()
    for ($i = 0; $i -lt $SyncRuns; $i++) {
        $calls += New-ToolCall (100 + $i) 'dispatch_agent_squad' ([ordered]@{ taskDescription = "E4 live sync: kiểm tra frame pacing Delta Force lần $($i + 1)"; nexusMode = 'Micro' })
    }
    $d = Invoke-Mcp $calls
    for ($i = 0; $i -lt $SyncRuns; $i++) {
        $text = Get-ToolText $d[100 + $i]
        try { $id = [long](($text | ConvertFrom-Json).dispatchId) } catch { $id = 0 }
        if ($id -le 0) { $e4Errors += "dispatch $i failed: $text"; continue }
        Start-Sleep -Milliseconds 400   # let the Cockpit absorb the dispatch event first

        $before = Get-SyncState
        $v = Invoke-Mcp @(New-ToolCall 200 'update_dispatch_verdict' ([ordered]@{ dispatchId = $id; verdict = 'NEEDS_WORK'; evidenceNotes = "E4 live sync run $($i + 1): jitter 3 ms" }))
        $vText = Get-ToolText $v[200]
        $saved = $vText -match '"saved":true'

        $state = $null; $deadline = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $deadline) {
            $s = Get-SyncState
            if ($s.sequence -gt $before.sequence -and $s.dispatchId -eq $id -and $s.status -eq 'NEEDS_WORK') { $state = $s; break }
            Start-Sleep -Milliseconds 20
        }
        $file = $null
        try { $file = Get-Content $latest -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
        $fileOk = $null -ne $file -and $file.dispatchId -eq $id -and $file.status -eq 'NEEDS_WORK' -and $file.source -eq 'McpTools'
        $e4Runs += [pscustomobject]@{
            dispatchId = $id; saved = $saved; fileOk = $fileOk; detected = ($null -ne $state)
            latencyMs = $(if ($state) { $state.latencyMs } else { $null }); response = $vText
        }
    }
}
$detectedRuns = @($e4Runs | Where-Object { $_.detected -and $_.saved -and $_.fileOk -and $null -ne $_.latencyMs })
$maxSync = if ($detectedRuns.Count) { ($detectedRuns | Measure-Object -Property latencyMs -Maximum).Maximum } else { $null }
$e4Pass = $e4Errors.Count -eq 0 -and $detectedRuns.Count -eq $SyncRuns -and $SyncRuns -gt 0 -and $maxSync -lt 300
Set-Result 'E4' $e4Pass ("{0}/{1} runs: MCP saved + latest_dispatch.json (source McpTools) + Cockpit detected | sync latency ms (updatedAtUtc -> Cockpit render, includes 150 ms debounce) = [{2}] max={3} (budget 300){4}" -f `
    $detectedRuns.Count, $SyncRuns, (($e4Runs | ForEach-Object { "#$($_.dispatchId):$($_.latencyMs)" }) -join ', '), $maxSync,
    $(if ($e4Errors.Count) { ' | errors: ' + ($e4Errors -join ' | ') } else { '' }))

# ---------------- E5: ACE-Guard branch + command-mode latency ----------------
$e5a = Invoke-SelfTest 'boost-acedenied'
$wake = @()
if ($hostInfo.Process) {
    for ($i = 0; $i -lt $LatencyRuns; $i++) {
        $r = Invoke-Pipe 'COMMANDMODE'
        if ($r -match 'WakeMs=([\d\.,]+)') { $wake += [double]($Matches[1] -replace ',', '.') }
        Start-Sleep -Milliseconds 150
        $null = Invoke-Pipe 'PASSIVE'
        Start-Sleep -Milliseconds 250
    }
}
$measured = @(if ($wake.Count -gt 1) { $wake | Select-Object -Skip 1 } else { $wake })   # first call warms the path
$maxWake = if ($measured.Count) { ($measured | Measure-Object -Maximum).Maximum } else { $null }
$e5Pass = [bool]$e5a.pass -and $measured.Count -gt 0 -and $maxWake -lt 35
Set-Result 'E5' $e5Pass ("ACE branch: status='{0}' deniedErrorCode={1} exceptionEscaped={2} yieldPlanned={3} protected planned=[{4}] realAntiCheat={5}:{6} | Ctrl+Shift+Space handler (pipe COMMANDMODE) ms=[{7}] max (excl. 1st)={8} (budget 35)" -f `
    $e5a.targetStatus, $e5a.deniedErrorCode, $(if ($e5a.exceptionEscaped) { 'YES' } else { 'no' }), $e5a.yieldPlanned, (@($e5a.protectedProcessesPlanned) -join ','),
    $e5a.realAntiCheatProcess, $e5a.realAntiCheatStatus, ($wake -join ', '), $maxWake)

# ---------------- E7: resource budget (before regression load) ----------------
$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
$privateWsMb = $null; $cpuPct = $null; $wsMb = $null
if ($hostProc) {
    Start-Sleep -Seconds 2
    $hostProc.Refresh()
    $cpuStart = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 3
    $hostProc.Refresh()
    $cpuPct = ($hostProc.TotalProcessorTime - $cpuStart).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100
    $wsMb = [math]::Round($hostProc.WorkingSet64 / 1MB, 1)
    $perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
    if ($perf) { $privateWsMb = [math]::Round($perf.WorkingSetPrivate / 1MB, 1) }
}
$budgetOk = ($null -ne $privateWsMb) -and ($privateWsMb -lt 60) -and ($null -ne $cpuPct) -and ($cpuPct -lt 1.0)

$regOk = $true; $regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    $task04 = Join-Path $PSScriptRoot 'Verify-Task04.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $task04 -Root $Root -SkipBuild | Out-Host
    $t4 = Join-Path $handoff 'task04_verification.json'
    $t3 = Join-Path $handoff 'task03_verification.json'
    $t4Pass = (Test-Path $t4) -and [bool]((Get-Content $t4 -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    $t3Pass = (Test-Path $t3) -and [bool]((Get-Content $t3 -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    $regOk = $t4Pass -and $t3Pass
    $regDetail = "Verify-Task04 allPassed=$t4Pass, Verify-Task03 allPassed=$t3Pass"
}
Set-Result 'E7' ($budgetOk -and $regOk) ("private working set={0} MB (budget 60, ADR-09) | WorkingSet64={1} MB | idle CPU={2:N2}% over 3 s | {3}{4}" -f `
    $privateWsMb, $wsMb, $cpuPct, $regDetail, $(if ($hostInfo.StartedHere) { ' | HostService started by this script and LEFT RUNNING' } else { '' }))

# ---------------- E6 (manual) ----------------
Write-Host "`n[E6] MANUAL - do this on the real setup and report the result:" -ForegroundColor Yellow
Write-Host '  1. In Delta Force (or your IDE) press Ctrl+Shift+Space, type: Tối ưu UDP Socket C# chống rớt gói tin   then Enter.'
Write-Host '     -> MSI G272: ACTIVE SQUAD card with a light-blue border, status [DISPATCHED]; focus returns to the game.'
Write-Host '  2. Ctrl+Shift+Space again, type: /fail thiếu log   then Enter -> amber card [NEEDS_WORK (Strike 1/3)] + Evidence line.'
Write-Host '  3. Ctrl+Shift+Space, type: /pass đã sửa   then Enter -> green card [PASSED (Retries: 1)]; RECENT DISPATCHES shows the same colour.'
Write-Host '  4. Keyboard/mouse are back in the game after each Enter (no Alt+Tab needed).'
Write-Host '  5. (Optional, Delta Force running) Ctrl+Shift+Space, /boost -> "ACE-Guard Protected (Background Yield Active)", then /boost off.'

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-05'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    results = $results
    e4Runs = $e4Runs
    e5CommandModeMs = $wake
    e6 = 'manual - see console checklist'
    qaReportEntryKept = [bool]$KeepQaReportEntry
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task05_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 6), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL AUTOMATED CHECKS PASS (E1-E5, E7) - E6 is manual' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
