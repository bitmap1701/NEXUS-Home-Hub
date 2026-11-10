<#
.SYNOPSIS
    TASK-08 / Gate 8 verification (E1-E5) for NEXUS Home Hub - automated Gemini QA loop, /ask cold-start, memory slimming.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror (0/0) + qa_response / qa_latency_ms columns with every row kept
        (--selftest schema08) + 0 occurrences of RegexOptions.Compiled in the C# sources.
    E2  ADR-28 (--selftest ask-cold, mock): AskSyncBreakdown (GuardMs, DispatchAndDbMs, TaskSpawnMs, TotalSyncMs) of
        Cold Call #1 < 25 ms (LiveCombat 40) and P95 of the next 10 < 10 ms (LiveCombat 25), measured after the same
        warm-up HostService runs at startup; no HttpClient created by mock runs (ADR-29). A run without warm-up is
        printed for reference only.
    E3  ADR-30/31 with mocks: 3a "/ask --auto-qa Thiết kế gói tin UDP có seqId" -> PASSED with ai_* and qa_* stored;
        3b /qa x3 with NEEDS_WORK -> r1, r2, ESCALATED r3 + one escalation line; 3c /qa without key -> skipped, no strike.
        qa_report.md is restored afterwards.
    E4  MCP run_gemini_qa_review (JSON-RPC stdio) -> the running Cockpit shows the new qaResponse in < 300 ms.
    E5  HostService private working set < 60 MB (target 45-54 MB), idle CPU < 1 %, and Verify-Task07 -SkipBuild
        (-> 06 -> 05 -> 04 -> 03) all PASS.
    Mock variables are set only for this script's child processes, never for the long-running HostService.
    Results: 03_Local_Data_Vault/handoff/task08_verification.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task08.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$SkipLive   # Gate 15: Task08 makes no live call itself; passed down to Verify-Task07 (0 live calls)
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

function Invoke-SelfTest([string]$scenario, [string]$arg = $null) {
    $suffix = if ($arg -eq 'nowarm') { '-nowarm' } else { '' }
    $out = Join-Path $handoff "task08_selftest_$scenario$suffix.json"
    if (Test-Path $out) { Remove-Item $out }
    $argList = @('--selftest', $scenario); if ($arg) { $argList += $arg }
    $p = Start-Process -FilePath $hostExe -ArgumentList $argList -PassThru -Wait
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

function Format-Timing($t) {
    if ($null -eq $t) { return 'n/a' }
    $inner = if ($t.dispatch) { " [persona {0} / insert {1} / build {2}]" -f $t.dispatch.personaMs, $t.dispatch.insertMs, $t.dispatch.buildMs } else { '' }
    return "guard={0} dispatch+db={1} (dispatch {4}{6} + latest_dispatch.json {5}) spawn={2} total={3}" -f $t.guardMs, $t.dispatchAndDbMs, $t.taskSpawnMs, $t.totalSyncMs, $t.dispatchMs, $t.latestFileMs, $inner
}

Write-Host "NEXUS Home Hub - TASK-08 verification (root: $Root)" -ForegroundColor Cyan

# ---------------- E1: build + schema + no RegexOptions.Compiled ----------------
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
$e1 = Invoke-SelfTest 'schema08'
$compiled = @(Get-ChildItem $engine -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    Select-String -Pattern 'RegexOptions\.Compiled' -SimpleMatch:$false | ForEach-Object { "$($_.Filename):$($_.LineNumber)" })
Set-Result 'E1' ($buildOk -and [bool]$e1.pass -and $compiled.Count -eq 0) ("{0} | columns missing=[{1}] rows={2} | migration in this process: added=[{3}] rows {4}->{5} | RegexOptions.Compiled in sources: {6}" -f `
    $buildDetail, (@($e1.missingColumns) -join ','), $e1.rows, (@($e1.migrationThisProcess.addedColumns) -join ','),
    $e1.migrationThisProcess.rowsBefore, $e1.migrationThisProcess.rowsAfter, $(if ($compiled.Count) { $compiled -join ', ' } else { '0' }))

# The long-running HostService is started only before E4 (the one step that needs it): Gate 7's 2.9 s and Gate 8 run 1's
# 397 ms first /ask both happened within ~2 s of that process starting, i.e. they measured cross-process startup
# contention, not the /ask path. E2/E3 now run while no other NEXUS process is starting.

# ---------------- E2: /ask cold start breakdown ----------------
try {
    Set-Item "Env:$askMock" 'NEXUS Verify-Task08 mock'
    $e2 = Invoke-SelfTest 'ask-cold'
    $e2ref = Invoke-SelfTest 'ask-cold' 'nowarm'
} finally { Remove-Item "Env:$askMock" -ErrorAction SilentlyContinue }
Set-Result 'E2' ([bool]$e2.pass) ("SLA {0} | warm-up {1} ms (sqlite+audit write {2}, route+regex+{13} personas {3}, json {4}) | Cold Call #1: {5} (budget {6}) | P95 of 10 = {7} ms (budget {8}) | max per step: {9} | HttpClient created by mock runs={10} | reference without warm-up: Cold Call #1 {11}, P95 {12} ms" -f `
    $e2.slaProfile, $e2.warmUpReport.totalMs, $e2.warmUpReport.sqliteMs, $e2.warmUpReport.routeMs, $e2.warmUpReport.jsonMs,
    (Format-Timing $e2.coldCall1), $e2.coldBudgetMs, $e2.p95TotalSyncMs, $e2.p95BudgetMs, (Format-Timing $e2.maxOfRuns),
    $e2.httpClientCreatedInProcess, (Format-Timing $e2ref.coldCall1), $e2ref.p95TotalSyncMs, $e2.warmUpReport.personasRead)

# ---------------- E3: automated QA loop ----------------
$qaBackup = if (Test-Path $qaReport) { [System.IO.File]::ReadAllBytes($qaReport) } else { $null }
try {
    Set-Item "Env:$askMock" 'NEXUS Verify-Task08 mock'
    Set-Item "Env:$qaMock" 'PASSED: Kịch bản UDP đạt chuẩn'
    $e3a = Invoke-SelfTest 'autoqa'
    Set-Item "Env:$qaMock" 'NEEDS_WORK: Lỗi rò rỉ socket'
    $e3b = Invoke-SelfTest 'qa-strikes'
    Remove-Item "Env:$askMock", "Env:$qaMock" -ErrorAction SilentlyContinue
    $e3c = Invoke-SelfTest 'qa-missingkey'
} finally {
    Remove-Item "Env:$askMock", "Env:$qaMock" -ErrorAction SilentlyContinue
    if ($null -ne $qaBackup) { [System.IO.File]::WriteAllBytes($qaReport, $qaBackup) }
}
$strikeText = (@($e3b.steps) | ForEach-Object { "{0}:{1}/r{2}" -f $_.qa, $_.status, $_.retryCount }) -join ' '
Set-Result 'E3' ([bool]$e3a.pass -and [bool]$e3b.pass -and [bool]$e3c.pass) ("3a /ask --auto-qa #{0} {1} -> QA {2}: {3} | DB status={4} r{5}, ai={6} chars, qa='{7}', evidence='{8}' | latest_dispatch.json ok={9} || 3b #{10}: {11} | escalation lines={12} | /qa sync ms=[{13}] (budget {14}) || 3c no key #{15}: {16} r{17} '{18}' | qa_report.md restored" -f `
    $e3a.dispatchId, $e3a.squadId, $e3a.qaAgent, $e3a.verdict, $e3a.db.status, $e3a.db.retryCount, $e3a.db.aiChars, $e3a.db.qaResponse, $e3a.db.evidenceNotes, $e3a.latestOk,
    $e3b.dispatchId, $strikeText, $e3b.escalationLines, (@($e3b.qaSyncMs) -join ', '), $e3b.qaSyncBudgetMs,
    $e3c.dispatchId, $e3c.status, $e3c.retryCount, $e3c.qaResponse)

# ---------------- E4: MCP run_gemini_qa_review -> live Cockpit ----------------
# Long-running HostService, started WITHOUT mock variables; give it a moment to finish starting.
$hostInfo = Start-HostIfNeeded
if ($hostInfo.StartedHere) { Start-Sleep -Seconds 3 }
$mcpQa = $null; $seen = $null; $e4Err = $null
try {
    $d = Invoke-Mcp @(New-ToolCall 2 'dispatch_agent_squad' ([ordered]@{ taskDescription = 'E4 MCP QA: kiểm tra jitter UDP khi Delta Force mở'; nexusMode = 'Micro' }))
    $id = [long](([string]$d[2].result.content[0].text | ConvertFrom-Json).dispatchId)
    Start-Sleep -Milliseconds 400
    $before = Get-SyncState
    Set-Item "Env:$qaMock" 'PASSED: Jitter UDP 2 ms, đạt chuẩn'
    $q = Invoke-Mcp @(New-ToolCall 3 'run_gemini_qa_review' ([ordered]@{ dispatchId = $id; additionalEvidence = 'jitter p95 = 2 ms trong 10 phút' }))
    Remove-Item "Env:$qaMock" -ErrorAction SilentlyContinue
    $mcpQa = [string]$q[3].result.content[0].text | ConvertFrom-Json
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline) {
        $s = Get-SyncState
        if ($s.sequence -gt $before.sequence -and $s.dispatchId -eq $id -and $s.qaResponseChars -gt 0 -and $s.status -eq 'PASSED') { $seen = $s; break }
        Start-Sleep -Milliseconds 20
    }
} catch { $e4Err = $_.Exception.Message } finally { Remove-Item "Env:$qaMock" -ErrorAction SilentlyContinue }
$e4Pass = $null -eq $e4Err -and $mcpQa -and $mcpQa.reviewed -and $mcpQa.verdict -eq 'PASSED' -and $mcpQa.status -eq 'PASSED' -and $null -ne $seen -and $seen.latencyMs -lt 300
Set-Result 'E4' $e4Pass ("MCP run_gemini_qa_review #{0}: qaAgent={1} verdict={2} status={3} r{4} qaLatencyMs={5} | running Cockpit picked it up={6}, sync latency={7} ms (budget 300, includes 150 ms debounce){8}" -f `
    $mcpQa.dispatchId, $mcpQa.qaAgent, $mcpQa.verdict, $mcpQa.status, $mcpQa.retryCount, $mcpQa.qaLatencyMs, ($null -ne $seen), $seen.latencyMs,
    $(if ($e4Err) { " | error: $e4Err" } else { '' }))

# ---------------- E5: memory + regression ----------------
$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
$privateWsMb = $null
if ($hostProc) {
    Start-Sleep -Seconds 3
    $perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
    if ($perf) { $privateWsMb = [math]::Round($perf.WorkingSetPrivate / 1MB, 1) }
}
$regOk = $true; $regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    $reg07Args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Verify-Task07.ps1'), '-Root', $Root, '-SkipBuild')
    if ($SkipLive) { $reg07Args += '-SkipLive' }
    & powershell @reg07Args | Out-Host
    $flag = { param($n) $p = Join-Path $handoff "task0${n}_verification.json"; (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed) }
    $t7 = & $flag 7; $t6 = & $flag 6; $t5 = & $flag 5; $t4 = & $flag 4; $t3 = & $flag 3
    $regOk = $t7 -and $t6 -and $t5 -and $t4 -and $t3
    $t5Path = Join-Path $handoff 'task05_verification.json'
    $e7 = if (Test-Path $t5Path) { (Get-Content $t5Path -Raw -Encoding UTF8 | ConvertFrom-Json).results.E7.detail } else { '' }
    $regDetail = "Verify-Task07=$t7, 06=$t6, 05=$t5, 04=$t4, 03=$t3 | Task05 E7 (after the full chain): $e7"
}
$memOk = ($null -ne $privateWsMb) -and ($privateWsMb -lt 60)
Set-Result 'E5' ($memOk -and $regOk) ("HostService private working set after E1-E4 = {0} MB (budget 60, target 45-54) | {1}{2}" -f `
    $privateWsMb, $regDetail, $(if ($hostInfo.StartedHere) { ' | HostService started by this script and LEFT RUNNING' } else { '' }))

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-08'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    results = $results
    e2 = [ordered]@{ coldCall1 = $e2.coldCall1; p95 = $e2.p95TotalSyncMs; runs = $e2.runs; reference = [ordered]@{ coldCall1 = $e2ref.coldCall1; p95 = $e2ref.p95TotalSyncMs } }
    e4 = [ordered]@{ mcp = $mcpQa; cockpit = $seen }
    privateWorkingSetMb = $privateWsMb
    hostServiceStartedHere = $hostInfo.StartedHere
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task08_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 6), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL CHECKS PASS (E1-E5)' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
