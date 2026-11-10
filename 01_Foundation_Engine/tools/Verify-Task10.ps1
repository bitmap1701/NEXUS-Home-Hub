#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub — Gate 10 automated verification (TASK-10 §4).
.DESCRIPTION
    E1  Build (0 warnings / 0 errors), Zero-BOM check on source/config/doc files, SQLite schema10 migration
        (prompt_tokens, completion_tokens, total_tokens; 100% existing rows preserved), nexus_config.json defaults.
    E2  usageMetadata / mock token accumulation across /ask -> /qa -> /f in SQLite WAL and latest_dispatch.json,
        plus ADR-43 turn_history_json cap = maxConversationTurns * 3 (18 entries when maxConversationTurns = 6).
    E3  HTTP 429 / 5xx Exponential Backoff + Jitter with NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES="2" (recovers on
        attempt #3, httpRetryCount == 2) and "3" (stops after 2 retries, returns [GEMINI API ERROR HTTP 429]).
    E4  Client-side GeminiQuotaGuard (< 2 ms synchronous block on RPM/TPM/Daily limit without network), live Named Pipe
        /usage (/tokens) & SYNCSTATE token fields, and MCP get_gemini_usage_stats (tools/list + tools/call).
    E5  Live Gemini E2E (live-e2e10: real usageMetadata tokens when key is configured, else SKIPPED), HostService RAM
        (< 60 MB Private WS) & idle CPU (< 1%, 10 x 1 s, avg of last 5), and full regression Verify-Task09.ps1 -SkipBuild.
    Writes 03_Local_Data_Vault/handoff/task10_verification.json (UTF-8 without BOM; never contains an API key).
#>
param(
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$SkipLive
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot    = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot   = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir  = Join-Path $VaultRoot 'handoff'
$ConfigPath  = Join-Path $VaultRoot 'config\nexus_config.json'
$HostExe     = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe      = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath  = Join-Path $HandoffDir 'task10_verification.json'
$PipeName    = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom   = New-Object System.Text.UTF8Encoding($false)

$MockAnswer  = 'Gate 10 mock answer: Exponential Backoff with Jitter + Sliding-Window Quota Guard.'

foreach ($v in 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
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

function Get-SyncState {
    $outer = Send-PipeLine '{"cmd":"SYNCSTATE"}' | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}

function Stop-HostService {
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
        Start-Sleep -Milliseconds 800
    }
    $procs = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600
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
        try { $null = Get-SyncState; break } catch { Start-Sleep -Milliseconds 300 }
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
    $path = Join-Path $HandoffDir "task10_selftest_$scenario.json"
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
    return [ordered]@{
        exitCode = $code
        noBom    = -not (Test-HasUtf8Bom $path)
        data     = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
}

function Invoke-Mcp([string[]]$requests, [hashtable]$envOverrides = @{}) {
    $saved = @{}
    foreach ($k in $envOverrides.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
        [Environment]::SetEnvironmentVariable($k, $envOverrides[$k], 'Process')
    }
    try {
        $all = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}') + $requests
        $lines = (($all -join "`n") + "`n") | & $McpExe
        $byId = @{}
        foreach ($l in $lines) {
            if ([string]::IsNullOrWhiteSpace($l)) { continue }
            $r = $l | ConvertFrom-Json
            if ($null -ne $r.id) { $byId[[int]$r.id] = $r }
        }
        return $byId
    } finally {
        foreach ($k in $envOverrides.Keys) {
            [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process')
        }
    }
}

# ───────────────────────────────────────── E1: Build, Zero-BOM & Schema10 ─────────────────────────────────────────
Write-Step 'E1: Release build (-warnaserror), Zero-BOM check & SQLite schema10 migration'
$buildWarnings = 0
$buildErrors   = 0
$buildCode     = 0
if (-not $SkipBuild) {
    Stop-HostService
    $buildLines = & dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1
    $buildCode = $LASTEXITCODE
    $buildText = ($buildLines | Out-String)
    Write-Host $buildText.Trim()
    if ($buildText -match '(\d+)\s+Warning\(s\)') { $buildWarnings = [int]$Matches[1] }
    if ($buildText -match '(\d+)\s+Error\(s\)')   { $buildErrors   = [int]$Matches[1] }
} else {
    Stop-HostService
}

$bomTargets = @(Get-ChildItem -Path $EngineRoot -Recurse -Include *.cs,*.xaml,*.csproj -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName })
$bomTargets += @(Get-ChildItem -Path $HandoffDir -Filter *.md -File | ForEach-Object { $_.FullName })
$bomTargets += @((Join-Path $RepoRoot 'GEMINI.md'), (Join-Path $RepoRoot 'CLAUDE.md'), $ConfigPath) | Where-Object { Test-Path $_ }
$bomOffenders = @($bomTargets | Where-Object { Test-HasUtf8Bom $_ })

$schema10 = Invoke-SelfTest 'schema10'
$e1Pass = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0) -and
          ($bomOffenders.Count -eq 0) -and ($schema10.exitCode -eq 0) -and $schema10.noBom -and [bool]$schema10.data.pass
Write-Host ("E1: buildCode={0} warnings={1} errors={2} bomOffenders={3} rows={4} -> {5}" -f `
    $buildCode, $buildWarnings, $buildErrors, $bomOffenders.Count, $schema10.data.rows, $(if ($e1Pass) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($e1Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E2: Token Accumulation & ADR-43 ────────────────────────────────────────
Write-Step 'E2: Token accumulation (/ask -> /qa -> /f) & ADR-43 turn_history_json cap (maxConversationTurns * 3)'
$e2 = Invoke-SelfTest 'token-accum' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}
$e2Pass = ($e2.exitCode -eq 0) -and $e2.noBom -and [bool]$e2.data.pass
Write-Host ("E2: dispatch=#{0} tokens({1} -> {2} -> {3}) adr43Cap={4} (at6={5}, turn7Contents={6}) -> {7}" -f `
    $e2.data.dispatchId, $e2.data.step1.totalTokens, $e2.data.step2.totalTokens, $e2.data.step3.totalTokens,
    $e2.data.adr43.expectedHistoryCap, $e2.data.adr43.entriesAtTurn6, $e2.data.adr43.turn7ContentsSent,
    $(if ($e2Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e2Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E3: HTTP 429/5xx Backoff + Jitter ──────────────────────────────────────
Write-Step 'E3: HTTP 429 / 5xx Exponential Backoff + Jitter (NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES=2 & 3)'
$e3 = Invoke-SelfTest 'backoff-retry' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}
$e3Pass = ($e3.exitCode -eq 0) -and $e3.noBom -and [bool]$e3.data.pass
Write-Host ("E3: caseA(ok={0}, retries={1}, latency={2}ms) caseB(ok={3}, retries={4}, msg={5}) -> {6}" -f `
    $e3.data.caseA_RecoverOnAttempt3.aiOk, $e3.data.caseA_RecoverOnAttempt3.httpRetryCount, $e3.data.caseA_RecoverOnAttempt3.aiLatencyMs,
    $e3.data.caseB_StopAtMaxRetries.aiOk, $e3.data.caseB_StopAtMaxRetries.httpRetryCount, $e3.data.caseB_StopAtMaxRetries.errorMessage,
    $(if ($e3Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e3Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E4: Client-Side Quota Guard & MCP ──────────────────────────────────────
Write-Step 'E4: Client-side GeminiQuotaGuard (< 2 ms block), live /usage (/tokens) & MCP get_gemini_usage_stats'
$e4Self = Invoke-SelfTest 'quota-guard' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}

# Start long-running HostService WITHOUT mock variables (rule 1 in claude_shift_handover.md)
$hostInfo = Start-HostIfNeeded

$pipeUsage  = Send-CockpitCommand '/usage'
$pipeTokens = Send-CockpitCommand '/tokens'

$mcpResp = Invoke-Mcp @(
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_gemini_usage_stats","arguments":{}}}'
) @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}
$toolNames = @($mcpResp[2].result.tools | ForEach-Object { $_.name })
$mcpStats  = [string]$mcpResp[3].result.content[0].text | ConvertFrom-Json

$e4McpOk = ($toolNames -contains 'get_gemini_usage_stats') -and
           ($null -ne $mcpStats.stats) -and
           # TASK-11b: dailyTotalTokens counts live API tokens only; mock tokens of this suite are in mockTokensUsed.
           (([long]$mcpStats.stats.dailyTotalTokens + [long]$mcpStats.stats.mockTokensUsed) -gt 0) -and
           ($mcpStats.config.maxRequestsPerMinute -eq 10)
$e4PipeOk = [bool]$pipeUsage.ok -and [bool]$pipeTokens.ok -and ($pipeUsage.kind -eq 'usage') -and ($pipeTokens.kind -eq 'usage')
$e4Pass = ($e4Self.exitCode -eq 0) -and $e4Self.noBom -and [bool]$e4Self.data.pass -and $e4PipeOk -and $e4McpOk
Write-Host ("E4: blockedElapsedMs={0}ms (<2ms={1}) pipeUsageOk={2} mcpToolListed={3} mcpDailyTokens={4} -> {5}" -f `
    $e4Self.data.blockedCall.elapsedMs, $e4Self.data.checks.blockedUnder2Ms, $e4PipeOk, ($toolNames -contains 'get_gemini_usage_stats'),
    $mcpStats.stats.dailyTotalTokens, $(if ($e4Pass) { 'PASS' } else { 'FAIL' })) `
    -ForegroundColor $(if ($e4Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E5: Live E2E, RAM/CPU & Regression ─────────────────────────────────────
Write-Step 'E5: Live Gemini E2E (usageMetadata), HostService RAM (< 60 MB) & idle CPU (< 1%), and Regression 09..03'
if ($SkipLive) {
    $live10 = [ordered]@{ exitCode = 0; noBom = $true; data = [ordered]@{ scenario = 'live-e2e10'; pass = $true; skipped = $true; reason = '-SkipLive specified' } }
} else {
    $live10 = Invoke-SelfTest 'live-e2e10' @{
        NEXUS_GEMINI_MOCK_RESPONSE               = $null
        NEXUS_GEMINI_QA_MOCK_VERDICT             = $null
        NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES = $null
    }
}
$liveOk = ($live10.exitCode -eq 0) -and $live10.noBom -and [bool]$live10.data.pass

$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $hostProc) {
    $hostInfo = Start-HostIfNeeded
    $hostProc = $hostInfo.Process
}
Start-Sleep -Seconds 2
$perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
$privateWsMb = if ($perf) { [math]::Round($perf.WorkingSetPrivate / 1MB, 1) } else { [math]::Round($hostProc.WorkingSet64 / 1MB, 1) }
$workingSetMb = [math]::Round($hostProc.WorkingSet64 / 1MB, 1)

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
$ramOk       = ($privateWsMb -lt 60.0)
$cpuOk       = ($last5AvgCpu -lt 1.0)

$reg09Pass = $true
$reg09Exit = 0
$regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    $reg09Args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Verify-Task09.ps1'), '-Root', $RepoRoot, '-SkipBuild')
    if ($SkipLive) { $reg09Args += '-SkipLive' }   # Gate 15: -SkipLive reaches 09 -> 08 -> 07 (0 live calls)
    & powershell @reg09Args | Out-Host
    $reg09Exit = $LASTEXITCODE
    $flag = { param($n) $p = Join-Path $HandoffDir "task0${n}_verification.json"; (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed) }
    $t9 = & $flag 9; $t8 = & $flag 8; $t7 = & $flag 7; $t6 = & $flag 6; $t5 = & $flag 5; $t4 = & $flag 4; $t3 = & $flag 3
    $reg09Pass = ($reg09Exit -eq 0) -and $t9 -and $t8 -and $t7 -and $t6 -and $t5 -and $t4 -and $t3
    $regDetail = "Verify-Task09=$t9, 08=$t8, 07=$t7, 06=$t6, 05=$t5, 04=$t4, 03=$t3"
}

$e5Pass = $ramOk -and $cpuOk -and $liveOk -and $reg09Pass
Write-Host ("E5: privateWsMb={0}MB (<60={1}) workingSetMb={2}MB idleCpuLast5={3}% (<1%={4}) liveOk={5} (skipped={6}) reg={7} -> {8}" -f `
    $privateWsMb, $ramOk, $workingSetMb, $last5AvgCpu, $cpuOk, $liveOk, $live10.data.skipped, $regDetail,
    $(if ($e5Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e5Pass) { 'Green' } else { 'Red' })

$overallPass = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task        = 'TASK-10'
    gate        = 'Gate 10'
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    machine     = $env:COMPUTERNAME
    skipLive    = [bool]$SkipLive
    liveCallAttempted = -not [bool]$live10.data.skipped
    allPassed   = $overallPass
    overallPass = $overallPass
    E1_BuildZeroBomAndSchema10 = [ordered]@{
        pass          = $e1Pass
        buildExitCode = $buildCode
        buildWarnings = $buildWarnings
        buildErrors   = $buildErrors
        bomOffenders  = $bomOffenders
        schema10      = $schema10.data
    }
    E2_TokenAccumulationAndAdr43 = [ordered]@{
        pass       = $e2Pass
        tokenAccum = $e2.data
    }
    E3_HttpBackoffAndRetry = [ordered]@{
        pass         = $e3Pass
        backoffRetry = $e3.data
    }
    E4_QuotaGuardAndUsageTelemetry = [ordered]@{
        pass           = $e4Pass
        quotaGuard     = $e4Self.data
        pipeUsage      = $pipeUsage
        pipeTokens     = $pipeTokens
        mcpToolListed  = ($toolNames -contains 'get_gemini_usage_stats')
        mcpUsageStats  = $mcpStats
    }
    E5_LiveE2eResourceAndRegression = [ordered]@{
        pass                  = $e5Pass
        privateWorkingSetMb   = $privateWsMb
        workingSetMb          = $workingSetMb
        ramUnder60Mb          = $ramOk
        cpuSamplesPct         = $cpuSamples
        idleCpuLast5AvgPct    = $last5AvgCpu
        idleCpuUnder1Pct      = $cpuOk
        liveE2e10             = $live10.data
        regression            = [ordered]@{
            ran      = (-not $SkipRegression)
            exitCode = $reg09Exit
            detail   = $regDetail
            pass     = $reg09Pass
        }
    }
}

[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 12), $Utf8NoBom)
Write-Host "`nGate 10 verification report written to $ReportPath (allPassed=$overallPass)" -ForegroundColor $(if ($overallPass) { 'Green' } else { 'Red' })
if (-not $overallPass) { exit 1 }
exit 0
