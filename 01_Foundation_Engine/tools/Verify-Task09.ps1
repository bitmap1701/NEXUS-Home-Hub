<#
.SYNOPSIS
    TASK-09 / Gate 9 verification (E1-E5) for NEXUS Home Hub - self-correcting /retry, multi-turn /followup, live Gemini E2E.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror (0/0) + turn_count / turn_history_json columns with every row kept
        (--selftest schema09) + no UTF-8 BOM in the C#/XAML/csproj sources, handoff docs and nexus_config.json.
    E2  ADR-36 (--selftest retry-loop, mocks): /ask -> /qa NEEDS_WORK "Thiếu xử lý đóng kết nối Socket" (r1) ->
        "/retry --auto-qa Đã bổ sung khối using cho Socket" with the QA mock switched to PASSED -> the Primary Agent's prompt
        carried task + previous answer + QA critique + hint, turn_count 2, PASSED, retry_count still 1, sync < 10 ms (25).
    E3  ADR-36 (--selftest retry-breaker, mocks): ESCALATED r3 -> /retry refused by the circuit breaker (no Gemini call,
        row byte-for-byte unchanged) -> "/retry --force --auto-qa" -> PASSED, retry_count 3. qa_report.md restored.
    E4  ADR-37: /ask (turn 1) -> /f (turn 2) in a selftest process, then MCP followup_gemini_dispatch (turn 3, JSON-RPC
        stdio): turn_count 3, history 1..3 user/model, the 2 earlier turns sent with the request, the running Cockpit picks
        turn 3 up in < 300 ms.
    E5  ADR-38: live /ask -> /f -> /qa against the configured Gemini model when a key is stored (DPAPI or GEMINI_API_KEY),
        else "SKIPPED (No live key configured)"; HostService private working set < 60 MB, idle CPU < 1 % (10 x 1 s, last 5),
        and Verify-Task08 -SkipBuild (-> 07 -> 06 -> 05 -> 04 -> 03) all PASS.
    Mock variables are set only for this script's child processes, never for the long-running HostService.
    Results: 03_Local_Data_Vault/handoff/task09_verification.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task09.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [switch]$SkipRegression
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
$configJson = Join-Path $Root '03_Local_Data_Vault\config\nexus_config.json'
$qaReport = Join-Path $handoff 'qa_report.md'
$pipeName = 'NEXUS_Home_Cockpit_Pipe'
$askMock  = 'NEXUS_GEMINI_MOCK_RESPONSE'
$qaMock   = 'NEXUS_GEMINI_QA_MOCK_VERDICT'

foreach ($v in $askMock, $qaMock) { if (Test-Path "Env:$v") { Remove-Item "Env:$v"; Write-Host "$v was set in this shell: removed for this run." -ForegroundColor Yellow } }

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

function Get-SyncState {
    $resp = Send-PipeLine '{"cmd":"SYNCSTATE"}' | ConvertFrom-Json
    return ([string]$resp.result | ConvertFrom-Json)
}

function Invoke-SelfTest([string]$scenario) {
    $out = Join-Path $handoff "task09_selftest_$scenario.json"
    if (Test-Path $out) { Remove-Item $out }
    $p = Start-Process -FilePath $hostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $out)) { return [pscustomobject]@{ pass = $false; error = "no report (exit code $($p.ExitCode))" } }
    return (Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Invoke-Mcp([string[]]$requests) {
    $all = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}') + $requests
    $lines = (($all -join "`n") + "`n") | & $mcpExe
    $byId = @{}
    foreach ($l in $lines) {
        if ([string]::IsNullOrWhiteSpace($l)) { continue }
        $r = $l | ConvertFrom-Json
        if ($null -ne $r.id) { $byId[[int]$r.id] = $r }
    }
    return $byId
}

function New-ToolCall([int]$id, [string]$name, $arguments) {
    return ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $name; arguments = $arguments } } | ConvertTo-Json -Compress -Depth 6)
}

function Start-HostIfNeeded {
    $proc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
    $started = $false
    if (-not $proc) { Start-Process -FilePath $hostExe | Out-Null; $started = $true }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        try { $null = Get-SyncState; break } catch { Start-Sleep -Milliseconds 300 }
    }
    return [pscustomobject]@{ Process = (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1); StartedHere = $started }
}

function Test-NoBom([string]$path) {
    if (-not (Test-Path $path)) { return 'missing' }
    $b = [System.IO.File]::ReadAllBytes($path)
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return 'BOM' }
    return 'ok'
}

function Format-Sync($t) {
    if ($null -eq $t) { return 'n/a' }
    return "guard={0} resolve+db={1} spawn={2} total={3} ms" -f $t.guardMs, $t.dispatchAndDbMs, $t.taskSpawnMs, $t.totalSyncMs
}

function Format-Checks($c) {
    if ($null -eq $c) { return 'n/a' }
    return (($c.PSObject.Properties | ForEach-Object { "{0}={1}" -f $_.Name, $_.Value }) -join ', ')
}

Write-Host "NEXUS Home Hub - TASK-09 verification (root: $Root)" -ForegroundColor Cyan

# ---------------- E1: build + schema + zero BOM ----------------
if ($SkipBuild) {
    $buildOk = Test-Path $hostExe
    $buildDetail = 'Build skipped (-SkipBuild)'
} else {
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
$e1 = Invoke-SelfTest 'schema09'
# .ps1 scripts keep their BOM on purpose (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).
$bomTargets = @(Get-ChildItem $engine -Recurse -Include *.cs, *.xaml, *.csproj -File | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object { $_.FullName })
$bomTargets += @(Get-ChildItem $handoff -Filter *.md -File | ForEach-Object { $_.FullName })
$bomTargets += @((Join-Path $Root 'GEMINI.md'), (Join-Path $Root 'CLAUDE.md'), $configJson) | Where-Object { Test-Path $_ }
$withBom = @($bomTargets | Where-Object { (Test-NoBom $_) -eq 'BOM' } | ForEach-Object { $_.Substring($Root.Length).TrimStart('\') })
Set-Result 'E1' ($buildOk -and [bool]$e1.pass -and $withBom.Count -eq 0) ("{0} | columns missing=[{1}] rows={2} rows with invalid turn data={3} | migration in this process: added=[{4}] rows {5}->{6} | maxConversationTurns={7} | UTF-8 BOM: {8} of {9} files{10}" -f `
    $buildDetail, (@($e1.missingColumns) -join ','), $e1.rows, $e1.rowsWithInvalidTurnData, (@($e1.migrationThisProcess.addedColumns) -join ','),
    $e1.migrationThisProcess.rowsBefore, $e1.migrationThisProcess.rowsAfter, $e1.maxConversationTurns, $withBom.Count, $bomTargets.Count,
    $(if ($withBom.Count) { " [" + ($withBom -join ', ') + "]" } else { '' }))

# As in Gate 8, the long-running HostService is started only before E4: E2/E3 run while no other NEXUS process is starting.

# ---------------- E2: self-correction loop ----------------
try {
    Set-Item "Env:$askMock" 'NEXUS Verify-Task09 mock'
    $e2 = Invoke-SelfTest 'retry-loop'
} finally { Remove-Item "Env:$askMock", "Env:$qaMock" -ErrorAction SilentlyContinue }
Set-Result 'E2' ([bool]$e2.pass) ("#{0} '{1}' | after /qa: {2}/r{3} turn {4} | after /retry: {5}/r{6} turn {7}, history [{8}], QA verdict {9} | /retry sync: {10} (budget {11}) | checks: {12} | ai='{13}'" -f `
    $e2.dispatchId, $e2.command, $e2.afterFirstQa.status, $e2.afterFirstQa.retryCount, $e2.afterFirstQa.turnCount,
    $e2.final.status, $e2.final.retryCount, $e2.final.turnCount, (@($e2.final.roles) -join ' '), $e2.secondVerdict,
    (Format-Sync $e2.retrySync), $e2.syncBudgetMs, (Format-Checks $e2.checks), $e2.final.aiResponse)

# ---------------- E3: circuit breaker on /retry + --force ----------------
$qaBackup = if (Test-Path $qaReport) { [System.IO.File]::ReadAllBytes($qaReport) } else { $null }
try {
    Set-Item "Env:$askMock" 'NEXUS Verify-Task09 mock'
    $e3 = Invoke-SelfTest 'retry-breaker'
} finally {
    Remove-Item "Env:$askMock", "Env:$qaMock" -ErrorAction SilentlyContinue
    if ($null -ne $qaBackup) { [System.IO.File]::WriteAllBytes($qaReport, $qaBackup) }
}
Set-Result 'E3' ([bool]$e3.pass) ("#{0} /qa x3: {1} | /retry -> '{2}' ({3}) | /retry --force --auto-qa -> {4}/r{5} turn {6} ({7}) | escalation lines={8} | checks: {9} | qa_report.md restored" -f `
    $e3.dispatchId, (@($e3.strikes) -join ' '), $e3.blockedMessage, (Format-Sync $e3.blockedSync), $e3.final.status, $e3.final.retryCount, $e3.final.turnCount,
    (Format-Sync $e3.forcedSync), $e3.escalationLines, (Format-Checks $e3.checks))

# ---------------- E4: multi-turn /followup + MCP + live Cockpit ----------------
# Long-running HostService, started WITHOUT mock variables; give it a moment to finish starting.
$hostInfo = Start-HostIfNeeded
if ($hostInfo.StartedHere) { Start-Sleep -Seconds 3 }
$e4 = $null; $mcpF = $null; $seen = $null; $e4Err = $null; $mcpOk = $false
try {
    Set-Item "Env:$askMock" 'NEXUS Verify-Task09 mock'
    $e4 = Invoke-SelfTest 'followup-chain'
    $id = [long]$e4.dispatchId
    Start-Sleep -Milliseconds 400
    $before = Get-SyncState
    $f = Invoke-Mcp @(New-ToolCall 2 'followup_gemini_dispatch' ([ordered]@{ followUpText = 'Turn 3: thêm thống kê tỉ lệ mất gói'; dispatchId = $id }))
    if ($f[2].error) { throw "JSON-RPC error: $($f[2].error.message)" }
    $mcpF = [string]$f[2].result.content[0].text | ConvertFrom-Json
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline) {
        $s = Get-SyncState
        if ($s.sequence -gt $before.sequence -and $s.dispatchId -eq $id -and $s.turnCount -eq 3 -and $s.event -eq 'followup_response') { $seen = $s; break }
        Start-Sleep -Milliseconds 20
    }
    $roles = (@($mcpF.turnHistory) | ForEach-Object { "{0}:{1}" -f $_.turn, $_.role }) -join ' '
    $mcpOk = $mcpF.aiOk -and $mcpF.turnCount -eq 3 -and $mcpF.status -eq 'DISPATCHED' -and $mcpF.contentsSent -eq 5 -and
        ([string]$mcpF.aiResponse).StartsWith('[MOCK-FOLLOWUP-TURN-3]') -and ([string]$mcpF.aiResponse).Contains('contents=5 (user=3, model=2)') -and
        ([string]$mcpF.aiResponse).Contains('prev_model="[MOCK-FOLLOWUP-TURN-2]') -and $roles -eq '1:user 1:model 2:user 2:model 3:user 3:model'
} catch { $e4Err = $_.Exception.Message } finally { Remove-Item "Env:$askMock" -ErrorAction SilentlyContinue }
$e4Pass = $null -eq $e4Err -and $e4 -and [bool]$e4.pass -and $mcpOk -and $null -ne $seen -and $seen.latencyMs -lt 300
Set-Result 'E4' $e4Pass ("selftest #{0}: /ask + /f -> turn {1}, history [{2}], /f sync {3} | checks: {4} | window: {5} || MCP followup_gemini_dispatch: turn {6}, status {7}, contents sent {8}, history [{9}], ai='{10}' | running Cockpit picked turn 3 up={11}, sync latency={12} ms (budget 300, includes 150 ms debounce){13}" -f `
    $e4.dispatchId, $e4.row.turnCount, (@($e4.row.roles) -join ' '), (Format-Sync $e4.followUpSync), (Format-Checks $e4.checks), (Format-Checks $e4.windowChecks),
    $mcpF.turnCount, $mcpF.status, $mcpF.contentsSent, $roles, $(if ($mcpF) { ([string]$mcpF.aiResponse).Substring(0, [Math]::Min(220, ([string]$mcpF.aiResponse).Length)) } else { '' }),
    ($null -ne $seen), $seen.latencyMs, $(if ($e4Err) { " | error: $e4Err" } else { '' }))

# ---------------- E5: live Gemini E2E (conditional) + memory/CPU + regression ----------------
Remove-Item "Env:$askMock", "Env:$qaMock" -ErrorAction SilentlyContinue
$live = Invoke-SelfTest 'live-e2e'
if ($live.skipped) {
    $liveOk = [bool]$live.pass
    $liveDetail = "Live Gemini: SKIPPED (No live key configured) - nạp khóa bằng /apikey set <KEY> trong Cockpit hoặc tools\Set-NexusGeminiKey.ps1"
    Write-Host $liveDetail -ForegroundColor Yellow
} else {
    $liveOk = [bool]$live.pass
    $liveDetail = "Live Gemini ({0}, key {1}) #{2}: /ask ok={3} {4} ms | /f ok={5} {6} ms, turn {7}, contents {8} | /qa reviewed={9} verdict={10} {11} ms | total {12} ms{13}" -f `
        $live.model, $live.keySource, $live.dispatchId, $live.ask.ok, $live.ask.latencyMs, $live.followUp.ok, $live.followUp.latencyMs, $live.followUp.turnCount,
        $live.followUp.contentsSent, $live.qa.reviewed, $live.qa.verdict, $live.qa.latencyMs, $live.totalMs,
        $(if (-not $live.pass) { " | errors: ask='$($live.ask.preview)' f='$($live.followUp.error)' qa='$($live.qa.error)' $($live.error)" } else { '' })
}

$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
$privateWsMb = $null; $cpuPct = $null; $cpuSeries = @()
if ($hostProc) {
    Start-Sleep -Seconds 2
    $perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
    if ($perf) { $privateWsMb = [math]::Round($perf.WorkingSetPrivate / 1MB, 1) }
    for ($i = 0; $i -lt 10; $i++) {
        $hostProc.Refresh()
        $c0 = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds 1
        $hostProc.Refresh()
        $cpuSeries += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
    }
    $cpuPct = [math]::Round(($cpuSeries | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
}
$regOk = $true; $regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task08.ps1') -Root $Root -SkipBuild | Out-Host
    $flag = { param($n) $p = Join-Path $handoff "task0${n}_verification.json"; (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed) }
    $t8 = & $flag 8; $t7 = & $flag 7; $t6 = & $flag 6; $t5 = & $flag 5; $t4 = & $flag 4; $t3 = & $flag 3
    $regOk = $t8 -and $t7 -and $t6 -and $t5 -and $t4 -and $t3
    $regDetail = "Verify-Task08=$t8, 07=$t7, 06=$t6, 05=$t5, 04=$t4, 03=$t3"
}
$memOk = ($null -ne $privateWsMb) -and ($privateWsMb -lt 60)
$cpuOk = ($null -ne $cpuPct) -and ($cpuPct -lt 1.0)
Set-Result 'E5' ($liveOk -and $memOk -and $cpuOk -and $regOk) ("{0} || HostService private working set after E1-E4 = {1} MB (budget 60, target 44-52) | idle CPU = {2} % (avg of last 5 x 1 s; series [{3}]) | {4}{5}" -f `
    $liveDetail, $privateWsMb, $cpuPct, ($cpuSeries -join ', '), $regDetail, $(if ($hostInfo.StartedHere) { ' | HostService started by this script and LEFT RUNNING' } else { '' }))

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-09'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    results = $results
    e2 = $e2
    e3 = [ordered]@{ dispatchId = $e3.dispatchId; strikes = $e3.strikes; checks = $e3.checks; blockedSync = $e3.blockedSync; forcedSync = $e3.forcedSync }
    e4 = [ordered]@{ selftest = $e4.checks; window = $e4.windowChecks; mcp = $mcpF; cockpit = $seen }
    live = $live
    privateWorkingSetMb = $privateWsMb
    idleCpuPct = $cpuPct
    hostServiceStartedHere = $hostInfo.StartedHere
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task09_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 8), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL CHECKS PASS (E1-E5)' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
