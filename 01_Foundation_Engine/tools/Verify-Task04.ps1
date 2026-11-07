<#
.SYNOPSIS
    TASK-04 / Gate 4 verification (E1-E6) for NEXUS Home Hub.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror + no hard-coded "D:\NEXUS_Home_Hub" in any .cs file
    E2  In-process Cockpit dispatch ("Thiết kế C# Async TCP Server với Redis caching"): P95 < 10 ms,
        new agent_dispatch_logs row, valid latest_dispatch.json (UTF-8 without BOM)
    E3  /boost Foreground + anti-cheat shield: foreground and whitelisted processes are never trimmed/yielded,
        no exception. Default is a DRY RUN (plan only); -LiveBoost really trims RAM and then restores priorities.
    E4  Cross-process SQLite WAL: HostService holds a WAL read transaction and writes continuously while
        McpTools runs 20 dispatch_agent_squad calls; zero errors on both sides (no SQLITE_BUSY)
    E5  MANUAL (printed checklist): Ctrl+Shift+Space -> type task -> Enter -> card on MSI G272 + focus back to game
    E6  Running HostService after 5 Cockpit dispatches: private working set < 60 MB, idle CPU ~0%
    R   Regression: Verify-Task03.ps1 (E1-E5) must still pass
    Results: 03_Local_Data_Vault/handoff/task04_verification.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task04.ps1
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task04.ps1 -LiveBoost   # while Delta Force runs
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [switch]$LiveBoost,
    [switch]$SkipRegression,
    [int]$WalSeconds = 10
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8

$engine  = Join-Path $Root '01_Foundation_Engine'
$sln     = Join-Path $engine 'NEXUSHomeHub.slnx'
$mcpExe  = Join-Path $engine 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$hostExe = Join-Path $engine 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$handoff = Join-Path $Root '03_Local_Data_Vault\handoff'
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

function Invoke-SelfTest([string]$scenario, [string[]]$extra = @()) {
    $out = Join-Path $handoff "task04_selftest_$scenario.json"
    if (Test-Path $out) { Remove-Item $out }
    $p = Start-Process -FilePath $hostExe -ArgumentList (@('--selftest', $scenario) + $extra) -PassThru -Wait
    if (-not (Test-Path $out)) { return [pscustomobject]@{ pass = $false; error = "no report (exit code $($p.ExitCode))" } }
    return (Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function New-ToolCall([int]$id, [string]$name, $arguments) {
    return ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $name; arguments = $arguments } } | ConvertTo-Json -Compress -Depth 8)
}

Write-Host "NEXUS Home Hub - TASK-04 verification (root: $Root)" -ForegroundColor Cyan

# ---------------- E1 ----------------
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
$hardcoded = @(Get-ChildItem -Path $Root -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern 'D:\NEXUS_Home_Hub' -SimpleMatch)
$hardDetail = if ($hardcoded.Count -eq 0) { 'no D:\NEXUS_Home_Hub in .cs' } else { ($hardcoded | ForEach-Object { "$($_.Filename):$($_.LineNumber)" }) -join ', ' }
Set-Result 'E1' ($buildOk -and $hardcoded.Count -eq 0) "$buildDetail | $hardDetail"
if (-not (Test-Path $hostExe)) { throw "HostService not found: $hostExe" }

# ---------------- E2 ----------------
$e2 = Invoke-SelfTest 'dispatch'
Set-Result 'E2' ([bool]$e2.pass) ("squad=$($e2.squadId) primary=$($e2.primaryAgent) qa=$($e2.qaAgent) | P95={0} ms (budget 10) over {1} runs, failures={2} | rowsAdded={3} rowOk={4} | latest_dispatch.json exists={5} noBom={6} valid={7} | slash={8}" -f `
    $e2.p95Ms, $e2.runs, $e2.failures, $e2.rowsAdded, $e2.sqliteRowOk, $e2.latestDispatchExists, $e2.latestDispatchNoBom, $e2.latestDispatchValid, ($e2.slashCommands | ConvertTo-Json -Compress))

# ---------------- E3 ----------------
$scenario = if ($LiveBoost) { 'boost-live' } else { 'boost' }
$e3 = Invoke-SelfTest $scenario
Set-Result 'E3' ([bool]$e3.pass) ("{0} | target={1} status='{2}' | foreground={3}({4}) shielded={5} touched={6} WS {7}->{8} MB | protected running=[{9}] touched=[{10}] | trimmed={11} freed={12} MB yielded={13} restored={14} | {15}" -f `
    $(if ($LiveBoost) { 'LIVE' } else { 'DRY RUN' }), $e3.targetProcess, $e3.targetStatus, $e3.foregroundProcess, $e3.foregroundPid, $e3.foregroundShielded, $e3.foregroundTouched,
    $e3.foregroundWorkingSetBeforeMb, $e3.foregroundWorkingSetAfterMb, ($e3.protectedProcessesRunning -join ','), ($e3.protectedProcessesTouched -join ','),
    $e3.trimmedCount, $e3.freedMb, $e3.yieldedCount, $e3.yieldRestored, $(if ($e3.error) { $e3.error } elseif ($e3.exception) { 'EXCEPTION' } else { 'no exception' }))

# ---------------- E4 ----------------
$marker = Join-Path $handoff 'task04_selftest_wal-stress.ready'
$walReport = Join-Path $handoff 'task04_selftest_wal-stress.json'
foreach ($f in @($marker, $walReport)) { if (Test-Path $f) { Remove-Item $f } }
$stress = Start-Process -FilePath $hostExe -ArgumentList @('--selftest', 'wal-stress', "$WalSeconds") -PassThru
$deadline = (Get-Date).AddSeconds(15)
while (-not (Test-Path $marker) -and (Get-Date) -lt $deadline -and -not $stress.HasExited) { Start-Sleep -Milliseconds 200 }
$mcpOk = 0; $mcpErr = @()
if (Test-Path $marker) {
    $requests = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
    for ($i = 0; $i -lt 20; $i++) {
        $requests += New-ToolCall (100 + $i) 'dispatch_agent_squad' ([ordered]@{ taskDescription = "WAL concurrency check $i"; nexusMode = 'Micro' })
    }
    $lines = (($requests -join "`n") + "`n") | & $mcpExe
    foreach ($l in $lines) {
        if ([string]::IsNullOrWhiteSpace($l)) { continue }
        $resp = $l | ConvertFrom-Json
        if ($resp.id -lt 100) { continue }
        if ($resp.error -or $resp.result.isError) { $mcpErr += $(if ($resp.error) { $resp.error.message } else { $resp.result.content[0].text }) } else { $mcpOk++ }
    }
} else { $mcpErr += 'HostService wal-stress did not signal ready' }
$stress.WaitForExit(($WalSeconds + 20) * 1000) | Out-Null
$wal = if (Test-Path $walReport) { Get-Content $walReport -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{ pass = $false; errors = @('no report'); writes = 0; reads = 0 } }
$e4Pass = [bool]$wal.pass -and $mcpOk -eq 20 -and $mcpErr.Count -eq 0
Set-Result 'E4' $e4Pass ("HostService: held WAL read tx, writes={0} reads={1} errors={2} | McpTools: {3}/20 dispatch ok, errors={4}{5}" -f `
    $wal.writes, $wal.reads, @($wal.errors).Count, $mcpOk, $mcpErr.Count, $(if ($mcpErr.Count) { " [" + (($mcpErr | Select-Object -First 3) -join ' | ') + "]" } else { '' }))

# ---------------- Regression (Verify-Task03) ----------------
if (-not $SkipRegression) {
    $task03 = Join-Path $PSScriptRoot 'Verify-Task03.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $task03 -Root $Root -SkipBuild | Out-Host
    $t3 = Join-Path $handoff 'task03_verification.json'
    $t3Pass = (Test-Path $t3) -and [bool]((Get-Content $t3 -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    Set-Result 'R' $t3Pass "Verify-Task03 E1-E5 regression allPassed=$t3Pass"
}

# ---------------- E6 ----------------
$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
$startedHere = $false
if (-not $hostProc) {
    Start-Process -FilePath $hostExe | Out-Null
    $startedHere = $true
    Start-Sleep -Seconds 4
    $hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
}
$e6Detail = @(); $dispatchOk = 0
$tasks = @('Tối ưu UDP Socket C# chống rớt gói tin', 'Kiểm tra frame pacing Delta Force trên GTX 1660',
           'Lên kế hoạch đồ án môn học UTH trên Jira', 'Ghi chú Zettelkasten cho MCP server', 'Mua SSD nâng cấp giá bao nhiêu')
foreach ($t in $tasks) {
    try {
        $req = [ordered]@{ cmd = 'COCKPIT'; text = $t } | ConvertTo-Json -Compress
        $resp = Send-PipeLine $req | ConvertFrom-Json
        $inner = $resp.result | ConvertFrom-Json
        if ($inner.ok -and $inner.dispatchId) { $dispatchOk++ ; $e6Detail += "#$($inner.dispatchId) $($inner.squadId)" }
        else { $e6Detail += "FAIL: $($resp.result)" }
    } catch { $e6Detail += "pipe error: $($_.Exception.Message)" }
}
Start-Sleep -Seconds 2
$hostProc = Get-Process -Id $hostProc.Id
$cpuStart = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds 3
$hostProc.Refresh()
$cpuPct = ($hostProc.TotalProcessorTime - $cpuStart).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100
$wsMb = [math]::Round($hostProc.WorkingSet64 / 1MB, 1)
$perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
$privateWsMb = if ($perf) { [math]::Round($perf.WorkingSetPrivate / 1MB, 1) } else { $null }
$e6Pass = ($dispatchOk -eq 5) -and ($null -ne $privateWsMb) -and ($privateWsMb -lt 60) -and ($cpuPct -lt 1.0)
Set-Result 'E6' $e6Pass ("5 Cockpit dispatches via pipe: {0}/5 [{1}] | private working set={2} MB (Task Manager 'Memory', budget 60) | WorkingSet64={3} MB | idle CPU={4:N2}% over 3 s{5}" -f `
    $dispatchOk, ($e6Detail -join '; '), $privateWsMb, $wsMb, $cpuPct, $(if ($startedHere) { ' | HostService started by this script and LEFT RUNNING' } else { '' }))

# ---------------- E5 (manual) ----------------
Write-Host "`n[E5] MANUAL - do this on the real setup and report the result:" -ForegroundColor Yellow
Write-Host '  1. In Delta Force (or your IDE) press Ctrl+Shift+Space.'
Write-Host '  2. Type: Kiểm tra frame pacing Delta Force trên GTX 1660   then press Enter.'
Write-Host '  3. On the MSI G272: ACTIVE SQUAD card shows Squad3 + agents + Dispatch ID; RECENT DISPATCHES updates.'
Write-Host '  4. Keyboard/mouse are back in the game immediately (no Alt+Tab needed). Also try Esc from command mode.'

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-04'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    liveBoost = [bool]$LiveBoost
    results = $results
    e5 = 'manual - see console checklist'
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task04_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 6), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL AUTOMATED CHECKS PASS (E1-E4, E6, R) - E5 is manual' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
