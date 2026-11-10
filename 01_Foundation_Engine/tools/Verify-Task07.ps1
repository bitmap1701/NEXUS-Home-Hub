<#
.SYNOPSIS
    TASK-07 / Gate 7 verification (E1-E5) for NEXUS Home Hub - Gemini API in CockpitUI & McpTools, DPAPI key vault.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror (0/0) + agent_dispatch_logs has ai_response / ai_model / ai_latency_ms,
        every existing row kept (--selftest schema07)
    E2  DPAPI vault (--selftest vault): test key encrypted to config\gemini_key.dpapi, no plaintext on disk (UTF-8 and
        UTF-16), decrypts back, mask "AIza...456 (39 chars, Source: DPAPI)", /apikey set|status|clear, pasted key
        refused. Then this script scans SQLite (db/wal/shm), latest_dispatch.json, logs, qa_report.md and the test
        reports for the test key. A real key that existed before is restored byte for byte. The key file is git-ignored.
    E3  NEXUS_GEMINI_MOCK_RESPONSE (this script's child processes only, never the running HostService):
        "/ask Tối ưu C# SocketAsyncEventArgs chống phân mảnh bộ nhớ" (--selftest ask) and MCP consult_gemini_agent:
        Squad1, ai_* stored in SQLite and latest_dispatch.json, the running Cockpit picks up the MCP answer;
        synchronous part of /ask < 5 ms (25 ms in LiveCombat).
    E4  /ask without any key (--selftest ask-missingkey, isolated vault, no GEMINI_API_KEY): no exception, DISPATCHED,
        "[LOCAL DISPATCH ONLY] ..." stored as ai_response.
    E5  Live call when a key is configured (DPAPI file or GEMINI_API_KEY; otherwise SKIPPED, not failed) + full
        regression: Verify-Task06 -SkipBuild (-> 05 -> 04 -> 03) all PASS, private working set < 60 MB, CPU < 1 %.
    Results: 03_Local_Data_Vault/handoff/task07_verification.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task07.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$SkipLive
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8

$engine   = Join-Path $Root '01_Foundation_Engine'
$sln      = Join-Path $engine 'NEXUSHomeHub.slnx'
$mcpExe   = Join-Path $engine 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$hostExe  = Join-Path $engine 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$vault    = Join-Path $Root '03_Local_Data_Vault'
$handoff  = Join-Path $vault 'handoff'
$keyFile  = Join-Path $vault 'config\gemini_key.dpapi'
$latest   = Join-Path $handoff 'latest_dispatch.json'
$pipeName = 'NEXUS_Home_Cockpit_Pipe'
$mockVar  = 'NEXUS_GEMINI_MOCK_RESPONSE'

# The mock must never reach the long-running HostService.
if (Test-Path "Env:$mockVar") { Remove-Item "Env:$mockVar"; Write-Host "$mockVar was set in this shell: removed for this run." -ForegroundColor Yellow }

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
    $out = Join-Path $handoff "task07_selftest_$scenario.json"
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

# Read even while SQLite / HostService keep the file open.
function Read-Shared([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $fs = [System.IO.FileStream]::new($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite, Delete')
    try { $ms = [System.IO.MemoryStream]::new(); $fs.CopyTo($ms); return $ms.ToArray() } finally { $fs.Dispose() }
}

function Test-ContainsBytes([byte[]]$hay, [byte[]]$needle) {
    if ($null -eq $hay -or $hay.Length -lt $needle.Length) { return $false }
    $first = $needle[0]
    $i = [Array]::IndexOf($hay, $first)
    while ($i -ge 0 -and $i -le $hay.Length - $needle.Length) {
        $match = $true
        for ($j = 1; $j -lt $needle.Length; $j++) { if ($hay[$i + $j] -ne $needle[$j]) { $match = $false; break } }
        if ($match) { return $true }
        $i = [Array]::IndexOf($hay, $first, $i + 1)
    }
    return $false
}

Write-Host "NEXUS Home Hub - TASK-07 verification (root: $Root)" -ForegroundColor Cyan

# ---------------- E1: build + schema ----------------
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
$e1 = Invoke-SelfTest 'schema07'
Set-Result 'E1' ($buildOk -and [bool]$e1.pass) ("{0} | columns missing=[{1}] rows={2} | migration in this process: added=[{3}] rows {4}->{5} error={6}" -f `
    $buildDetail, (@($e1.missingColumns) -join ','), $e1.rows, (@($e1.migrationThisProcess.addedColumns) -join ','),
    $e1.migrationThisProcess.rowsBefore, $e1.migrationThisProcess.rowsAfter, $e1.migrationThisProcess.error)

# Long-running HostService (started WITHOUT the mock variable)
$hostInfo = Start-HostIfNeeded

# ---------------- E2: DPAPI vault + leak scan ----------------
$keyExistedBefore = Test-Path $keyFile
$keyHashBefore = if ($keyExistedBefore) { (Get-FileHash $keyFile -Algorithm SHA256).Hash } else { $null }
$e2 = Invoke-SelfTest 'vault'
$needles = @([System.Text.Encoding]::UTF8.GetBytes('AIzaSyTestKey'), [System.Text.Encoding]::Unicode.GetBytes('AIzaSyTestKey'))
$scan = [ordered]@{}
$scanFiles = @(
    (Join-Path $vault 'structured_db\nexus_memory.db'), (Join-Path $vault 'structured_db\nexus_memory.db-wal'),
    (Join-Path $vault 'structured_db\nexus_memory.db-shm'), $latest, (Join-Path $handoff 'hostservice.log'),
    (Join-Path $handoff 'mcp_debug.log'), (Join-Path $handoff 'qa_report.md'), (Join-Path $handoff 'task07_selftest_vault.json')
)
foreach ($f in $scanFiles) {
    $bytes = Read-Shared $f
    if ($null -eq $bytes) { $scan[(Split-Path $f -Leaf)] = 'absent'; continue }
    $hit = @($needles | Where-Object { Test-ContainsBytes $bytes $_ }).Count -gt 0
    $scan[(Split-Path $f -Leaf)] = $(if ($hit) { 'LEAK' } else { 'clean' })
}
$noLeak = -not ($scan.Values | Where-Object { $_ -eq 'LEAK' })
& git -C $Root check-ignore -q '03_Local_Data_Vault/config/gemini_key.dpapi'; $keyIgnored = ($LASTEXITCODE -eq 0)
$keyRestored = if ($keyExistedBefore) { (Test-Path $keyFile) -and ((Get-FileHash $keyFile -Algorithm SHA256).Hash -eq $keyHashBefore) } else { -not (Test-Path $keyFile) }
Set-Result 'E2' ([bool]$e2.pass -and $noLeak -and $keyIgnored -and $keyRestored) ("stored={0} blob={1} bytes, no plaintext UTF-8={2} UTF-16={3}, decrypt matches={4}, mask='{5}' ok={6} | /apikey set+status ok + pasted key blocked={7}, messages clean={8}, clear={9} | leak scan: {10} | key file git-ignored={11} | real key existed before={12}, state restored={13}" -f `
    $e2.fileExists, $e2.blobBytes, $e2.noPlaintextUtf8, $e2.noPlaintextUtf16, $e2.decryptedMatches, $e2.mask, $e2.maskOk,
    $e2.commands.pastedKeyBlocked, $e2.messagesClean, $e2.cleared,
    (($scan.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '), $keyIgnored, $keyExistedBefore, $keyRestored)

# ---------------- E3: /ask + MCP consult with the mock ----------------
$mcp = $null; $listOk = $false; $cockpitSaw = $null
try {
    Set-Item "Env:$mockVar" 'NEXUS Verify-Task07 mock'
    $e3 = Invoke-SelfTest 'ask'
    $consultCall = ([ordered]@{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = [ordered]@{ name = 'consult_gemini_agent'; arguments = [ordered]@{ taskDescription = 'Tối ưu C# SocketAsyncEventArgs chống phân mảnh bộ nhớ (MCP)' } } } | ConvertTo-Json -Compress -Depth 6)
    $m = Invoke-Mcp @('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}', $consultCall)
    $listOk = @($m[2].result.tools | Where-Object { $_.name -eq 'consult_gemini_agent' }).Count -eq 1
    $mcp = [string]$m[3].result.content[0].text | ConvertFrom-Json
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline -and $mcp) {
        $s = Get-SyncState
        if ($s.dispatchId -eq $mcp.dispatchId -and $s.aiModel -eq 'gemini-mock-verifier') { $cockpitSaw = $s; break }
        Start-Sleep -Milliseconds 50
    }
} finally {
    Remove-Item "Env:$mockVar" -ErrorAction SilentlyContinue
}
$latestJson = $null
try { $latestJson = Get-Content $latest -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
$mcpOk = $listOk -and $mcp -and $mcp.squadId -eq 'Squad1' -and $mcp.aiOk -and $mcp.aiModel -eq 'gemini-mock-verifier' -and
         ([string]$mcp.aiResponse).Contains('Squad1') -and $null -ne $latestJson -and $latestJson.source -eq 'McpTools' -and
         $latestJson.aiModel -eq 'gemini-mock-verifier' -and $null -ne $cockpitSaw
Set-Result 'E3' ([bool]$e3.pass -and $mcpOk) ("Cockpit /ask: dispatch #{0} {1} -> {2} | routed={3} ai={4} ({5}, {6} ms) db={7} latest_dispatch.json={8} | sync /ask ms first={9} warm=[{10}] budget {11} ({12}) ok={13} | MCP: tools/list has consult_gemini_agent={14}, #{15} {16} aiModel={17} aiOk={18}; latest_dispatch.json source={19}; running Cockpit picked it up={20}" -f `
    $e3.dispatchId, $e3.squadId, $e3.primaryAgent, $e3.routed, $e3.aiOk, $e3.ai.aiModel, $e3.ai.aiLatencyMs, $e3.dbOk, $e3.latestOk,
    $e3.firstSyncMs, (@($e3.warmSyncMs) -join ', '), $e3.syncBudgetMs, $e3.slaProfile, $e3.syncOk,
    $listOk, $mcp.dispatchId, $mcp.squadId, $mcp.aiModel, $mcp.aiOk, $latestJson.source, ($null -ne $cockpitSaw))

# ---------------- E4: missing key ----------------
$e4 = Invoke-SelfTest 'ask-missingkey'
Set-Result 'E4' ([bool]$e4.pass) ("status='{0}' | /ask #{1} sync {2} ms -> {3}, exception={4} | ai_response='{5}' (key: {6})" -f `
    $e4.statusMessage, $e4.dispatchId, $e4.syncMs, $e4.status, $(if ($e4.exceptionEscaped) { 'YES' } else { 'no' }), $e4.aiResponse, $e4.keySource)

# ---------------- E5: live call + regression ----------------
$hasKey = (Test-Path $keyFile) -or -not [string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY)
$liveAttempted = $false
if ($SkipLive -or -not $hasKey) {
    $liveOk = $true
    $liveDetail = if ($SkipLive) { 'live call SKIPPED (-SkipLive)' } else { 'live call SKIPPED: no key yet (Cockpit: /apikey set <KEY>, then re-run)' }
} else {
    $e5 = Invoke-SelfTest 'ask-live'
    $liveAttempted = -not [bool]$e5.skipped
    $liveOk = [bool]$e5.pass
    $liveDetail = if ($e5.skipped) { "live call SKIPPED: $($e5.reason)" } else {
        "live /ask #{0} {1}: model={2} aiOk={3} ai_latency_ms={4} (key: {5}) | '{6}'" -f $e5.dispatchId, $e5.squadId, $e5.model, $e5.aiOk, $e5.aiLatencyMs, $e5.keySource, $e5.responsePreview }
}
$regOk = $true; $regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task06.ps1') -Root $Root -SkipBuild | Out-Host
    $flag = { param($n) $p = Join-Path $handoff "task0${n}_verification.json"; (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed) }
    $t6 = & $flag 6; $t5 = & $flag 5; $t4 = & $flag 4; $t3 = & $flag 3
    $regOk = $t6 -and $t5 -and $t4 -and $t3
    $t5Path = Join-Path $handoff 'task05_verification.json'
    $e7 = if (Test-Path $t5Path) { (Get-Content $t5Path -Raw -Encoding UTF8 | ConvertFrom-Json).results.E7.detail } else { '' }
    $regDetail = "Verify-Task06=$t6, 05=$t5, 04=$t4, 03=$t3 | Task05 E7: $e7"
}
Set-Result 'E5' ($liveOk -and $regOk) ("{0} | {1}" -f $liveDetail, $regDetail)

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-07'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    results = $results
    leakScan = $scan
    mcpConsult = $mcp
    cockpitSyncState = $cockpitSaw
    liveKeyConfigured = $hasKey
    skipLive = [bool]$SkipLive
    liveCallAttempted = $liveAttempted
    hostServiceStartedHere = $hostInfo.StartedHere
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task07_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 6), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL CHECKS PASS (E1-E5)' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
