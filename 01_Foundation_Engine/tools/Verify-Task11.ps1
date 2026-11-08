#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub — Gate 11 automated verification (TASK-11 §4).
.DESCRIPTION
    E1  Build (0 warnings / 0 errors), Zero-BOM check on source/config/doc files, SQLite schema11 migration
        (gemini_response_cache table + columns; >= 1,296 baseline rows preserved with 0 data loss).
    TASK-11b (Gate 11b, v0.11.1): cache-hit latency is judged on 10 consecutive hits after 1 warm-up hit:
        cache_lookup_ms P95 <= 2.0 ms; total incl. the ADR-14 atomic write of latest_dispatch.json P50 <= 2.5 ms, P95 <= 4.0 ms.
        E1 also requires the standard configuration (maxDailyTokens = 250000, geminiModel = gemini-3.5-flash-lite);
        E2 also checks ADR-14 atomicity (a concurrent reader never sees a partial latest_dispatch.json).
    E2  ADR-44 Deterministic Response Cache: Cache Miss -> Cache Hit (< 2 ms, 0 tokens deducted, quota guard
        unchanged, cumulative prompt_tokens_saved + completion_tokens_saved = saved_total_tokens), /qa cache hit
        (< 2 ms), and Cache Bypass (/ask!, /ask --fresh, /f).
    E3  ADR-45 VaultGroundingEngine: selective local context extraction from 03_Local_Data_Vault/ (<= 3 sections,
        <= 1,500 chars, [file#section] grounding_sources in latest_dispatch.json), /ground Miss -> /g Hit (< 2 ms).
    E4  Slash commands (/cache, /cache stats, /cache clear) over Named Pipe + Local MCP Server tools
        get_gemini_cache_stats and search_local_vault (< 5 ms).
    E5  Live Gemini E2E with real DPAPI key (live-e2e11: Call #1 over network -> Call #2 hits SQLite WAL cache
        in < 2 ms with 0 additional tokens -> /qa PASSED), HostService RAM (< 60 MB Private WS) & idle CPU
        (< 1%, 10 x 1 s, avg of last 5), and full regression Verify-Task10.ps1 -> Verify-Task03.ps1.
    Writes 03_Local_Data_Vault/handoff/task11_verification.json (UTF-8 without BOM; never contains an API key).
#>
param(
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$SkipLive,
    [ValidateRange(1, 10)][int]$CpuSamples = 10,   # TASK-14b ADR-55: the fast tier passes 4 (idle CPU = average of the last 5 or fewer)
    [switch]$RelaxedSla   # TASK-14b (0b): micro-latency SLAs pass up to 2.5x with [SLA-WARN]; also inherited from NEXUS_RELAXED_SLA=1
)
if ($RelaxedSla) { $env:NEXUS_RELAXED_SLA = '1' }
$script:SlaRelaxed = ($env:NEXUS_RELAXED_SLA -eq '1')
$script:ScriptSlaWarnings = @()   # TASK-13a: script-side [SLA-WARN] entries, written to the report as slaWarnings
if ($script:SlaRelaxed) { Write-Host 'Relaxed SLA (FastRegression, decision 0b): micro-latency thresholds warn up to 2.5x; functional, data, security and memory checks stay hard.' -ForegroundColor Yellow }

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot    = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot   = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir  = Join-Path $VaultRoot 'handoff'
$ConfigPath  = Join-Path $VaultRoot 'config\nexus_config.json'
$HostExe     = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe      = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath  = Join-Path $HandoffDir 'task11_verification.json'
$PipeName    = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom   = New-Object System.Text.UTF8Encoding($false)

$MockAnswer  = 'Gate 11 mock answer: Deterministic SQLite WAL Response Cache (ADR-44) + Selective Local Vault Grounding (ADR-45).'

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
    # TASK-11b: graceful SHUTDOWN over the pipe only. Killing a process is Red tier (GEMINI.md): if HostService does not
    # exit by itself, stop here and let Nhat Hoa decide instead of Stop-Process -Force.
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 200
    }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Cockpit or Task Manager), then re-run Verify-Task11.ps1.'
    }
}

function Format-Lat($h) {
    if ($null -eq $h) { return 'n/a' }
    return ("lookup P95={0} ms [{1}] | total P50={2} P95={3} ms (min {4}, max {5}) [{6}]" -f `
        $h.lookup.p95, (@($h.lookup.samples) -join ', '), $h.total.p50, $h.total.p95, $h.total.min, $h.total.max, (@($h.total.samples) -join ', '))
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
    $path = Join-Path $HandoffDir "task11_selftest_$scenario.json"
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

# ───────────────────────────────────────── E1: Build, Zero-BOM & Schema11 ─────────────────────────────────────────
Write-Step 'E1: Release build (-warnaserror), Zero-BOM check & SQLite schema11 migration'
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

$schema11 = Invoke-SelfTest 'schema11'
# TASK-11b: Gate 11b must pass on the standard configuration, without a raised daily token cap.
$cfgStandard = ([int]$schema11.data.config.maxDailyTokens -eq 250000) -and ([string]$schema11.data.config.geminiModel -eq [string]$schema11.data.config.defaultModel)
if (-not $cfgStandard) {
    Write-Host ("nexus_config.json is not standard: maxDailyTokens={0} (need 250000), geminiModel={1} (need {2}). Edit 03_Local_Data_Vault\config\nexus_config.json and re-run." -f `
        $schema11.data.config.maxDailyTokens, $schema11.data.config.geminiModel, $schema11.data.config.defaultModel) -ForegroundColor Yellow
}
$e1Pass = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0) -and
          ($bomOffenders.Count -eq 0) -and ($schema11.exitCode -eq 0) -and $schema11.noBom -and [bool]$schema11.data.pass -and $cfgStandard
Write-Host ("E1: buildCode={0} warnings={1} errors={2} bomOffenders={3} rows={4} cacheCols={5} tokenSplitColsMissing=[{6}] config: maxDailyTokens={7} geminiModel={8} | tokens today: live={9} mock={10} -> {11}" -f `
    $buildCode, $buildWarnings, $buildErrors, $bomOffenders.Count, $schema11.data.rows, $schema11.data.cacheColumns.Count,
    (@($schema11.data.missingTokenSplitColumns) -join ','), $schema11.data.config.maxDailyTokens, $schema11.data.config.geminiModel,
    $schema11.data.tokensToday.liveTotal, $schema11.data.tokensToday.mock,
    $(if ($e1Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e1Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E2: Deterministic Response Cache ───────────────────────────────────────
Write-Step 'E2: ADR-44 Cache Miss -> Cache Hit (< 2 ms, 0 tokens deducted, saved_total_tokens) & Bypass (/ask!, /f)'
$e2 = Invoke-SelfTest 'cache-hit-bypass' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}
$e2Pass = ($e2.exitCode -eq 0) -and $e2.noBom -and [bool]$e2.data.pass
Write-Host ("E2: /ask hits (10 after warm-up): {0}" -f (Format-Lat $e2.data.askLatency))
Write-Host ("E2: /qa hits  (10 after warm-up): {0}" -f (Format-Lat $e2.data.qaLatency))
Write-Host ("E2: ADR-14 atomic writes under a concurrent reader: writes={0} hits={1} reads={2} torn/invalid={3}" -f `
    $e2.data.atomicity.writes, $e2.data.atomicity.cacheHits, $e2.data.atomicity.concurrentReads, $e2.data.atomicity.tornOrInvalidReads)
Write-Host ("E2: savedAfter2Hits={0} bypassBangCacheHit={1} followCacheHit={2} failedChecks=[{3}] -> {4}" -f `
    $e2.data.hit2.savedTotalTokens, $e2.data.bypass.askBang.cacheHit, $e2.data.bypass.followUp.cacheHit,
    (($e2.data.checks.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object { $_.Name }) -join ','),
    $(if ($e2Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e2Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E3: Local Vault Grounding Engine ───────────────────────────────────────
Write-Step 'E3: ADR-45 VaultGroundingEngine (<= 3 sections, <= 1,500 chars, grounding_sources, /ground -> /g < 2 ms)'
$e3 = Invoke-SelfTest 'vault-grounding' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}
$e3Pass = ($e3.exitCode -eq 0) -and $e3.noBom -and [bool]$e3.data.pass
Write-Host ("E3: /g hits (10 after warm-up): {0}" -f (Format-Lat $e3.data.groundLatency))
Write-Host ("E3: sections={0} chars={1}/1500 warmSearchMs={2}ms sources={3} failedChecks=[{4}] -> {5}" -f `
    $e3.data.directSearch.sectionCount, $e3.data.directSearch.attachedContextChars,
    $e3.data.directSearch.warmElapsedMs, ($e3.data.directSearch.sources -join ', '),
    (($e3.data.checks.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object { $_.Name }) -join ','),
    $(if ($e3Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e3Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E4: Slash Commands & MCP Tools ─────────────────────────────────────────
Write-Step 'E4: /cache, /cache clear, MCP get_gemini_cache_stats & MCP search_local_vault (< 5 ms)'
$e4Self = Invoke-SelfTest 'cache-commands' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}

# Start long-running HostService WITHOUT mock variables (rule 1 in claude_shift_handover.md)
$hostInfo = Start-HostIfNeeded

$pipeCache      = Send-CockpitCommand '/cache'
$pipeCacheStats = Send-CockpitCommand '/cache stats'
$pipeCacheClear = Send-CockpitCommand '/cache clear'

# Re-seed one cached entry + hit for MCP inspection
$null = Invoke-SelfTest 'cache-commands' @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}

$mcpResp = Invoke-Mcp @(
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_gemini_cache_stats","arguments":{}}}',
    '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_local_vault","arguments":{"query":"ADR-44 gemini_response_cache VaultGroundingEngine"}}}',
    '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"search_local_vault","arguments":{"query":"ADR-44 gemini_response_cache VaultGroundingEngine"}}}'
) @{
    NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
}
$toolNames     = @($mcpResp[2].result.tools | ForEach-Object { $_.name })
$mcpCacheStats = [string]$mcpResp[3].result.content[0].text | ConvertFrom-Json
$mcpVault      = [string]$mcpResp[4].result.content[0].text | ConvertFrom-Json
$mcpVaultWarm  = [string]$mcpResp[5].result.content[0].text | ConvertFrom-Json
$mcpSearchElapsedMs = [math]::Min([double]$mcpVault.elapsed_ms, [double]$mcpVaultWarm.elapsed_ms)

$e4PipeOk = [bool]$pipeCache.ok -and ($pipeCache.kind -eq 'cache') -and
            [bool]$pipeCacheStats.ok -and ($pipeCacheStats.kind -eq 'cache') -and
            [bool]$pipeCacheClear.ok -and ($pipeCacheClear.kind -eq 'cache')
$e4McpOk  = ($toolNames -contains 'get_gemini_cache_stats') -and
            ($toolNames -contains 'search_local_vault') -and
            ($null -ne $mcpCacheStats.stats) -and
            ([int]$mcpCacheStats.stats.total_entries -ge 1) -and
            ([long]$mcpCacheStats.stats.saved_total_tokens -gt 0) -and
            ([int]$mcpCacheStats.stats.ttl_seconds -eq 3600) -and
            ([int]$mcpVault.sectionCount -ge 1) -and
            ([int]$mcpVault.sectionCount -le 3) -and
            ([int]$mcpVault.total_chars -le 1500) -and
            (($mcpSearchElapsedMs -lt 5.0) -or ($script:SlaRelaxed -and $mcpSearchElapsedMs -le 12.5))
if (($mcpSearchElapsedMs -ge 5.0) -and $script:SlaRelaxed -and ($mcpSearchElapsedMs -le 12.5)) {
    Write-Host ("[SLA-WARN (FastRegression jitter)] E4: MCP search_local_vault = {0} ms (SLA 5.0 ms, ceiling 12.5 ms) -> PASS with warning" -f $mcpSearchElapsedMs) -ForegroundColor Yellow
    $script:ScriptSlaWarnings += [ordered]@{ tag = 'SLA-WARN (FastRegression jitter)'; metric = 'MCP search_local_vault'; measuredMs = $mcpSearchElapsedMs; slaMs = 5.0; ceilingMs = 12.5 }
}
$e4Pass   = ($e4Self.exitCode -eq 0) -and $e4Self.noBom -and [bool]$e4Self.data.pass -and $e4PipeOk -and $e4McpOk
Write-Host ("E4: pipeOk={0} mcpToolsListed={1} mcpSavedTokens={2} mcpSearchMs={3}ms (<5ms={4}) -> {5}" -f `
    $e4PipeOk, (($toolNames -contains 'get_gemini_cache_stats') -and ($toolNames -contains 'search_local_vault')),
    $mcpCacheStats.stats.saved_total_tokens, $mcpSearchElapsedMs, ($mcpSearchElapsedMs -lt 5.0),
    $(if ($e4Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e4Pass) { 'Green' } else { 'Red' })

# ───────────────────────────────────────── E5: Live E2E, RAM/CPU & Regression ─────────────────────────────────────
Write-Step 'E5: Live Gemini E2E (live-e2e11: Miss -> Hit < 2 ms -> /qa PASSED), RAM (< 60 MB), CPU (< 1%) & Regression 10..03'
if ($SkipLive) {
    $live11 = [ordered]@{ exitCode = 0; noBom = $true; data = [ordered]@{ scenario = 'live-e2e11'; pass = $true; skipped = $true; reason = '-SkipLive specified' } }
} else {
    $live11 = Invoke-SelfTest 'live-e2e11' @{
        NEXUS_GEMINI_MOCK_RESPONSE               = $null
        NEXUS_GEMINI_QA_MOCK_VERDICT             = $null
        NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES = $null
    }
}
$liveOk = ($live11.exitCode -eq 0) -and $live11.noBom -and [bool]$live11.data.pass
if (-not $live11.data.skipped) {
    Write-Host ("E5: live model={0} (configured {1}) modelFallbackAudits={2} retriedToPass={3} | live /ground hits (10 after warm-up): {4} | checks: {5}" -f `
        $live11.data.call1_NetworkMiss.model, $live11.data.configuredModel, $live11.data.modelFallbackAudits, $live11.data.retriedToPass,
        (Format-Lat $live11.data.liveHitLatency), (($live11.data.checks.PSObject.Properties | ForEach-Object { "{0}={1}" -f $_.Name, $_.Value }) -join ', '))
}

$hostProc = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $hostProc) {
    $hostInfo = Start-HostIfNeeded
    $hostProc = $hostInfo.Process
}
Start-Sleep -Seconds 2
$perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($hostProc.Id)" -ErrorAction SilentlyContinue
$privateWsMb  = if ($perf) { [math]::Round($perf.WorkingSetPrivate / 1MB, 1) } else { [math]::Round($hostProc.WorkingSet64 / 1MB, 1) }
$workingSetMb = [math]::Round($hostProc.WorkingSet64 / 1MB, 1)

# TASK-14b fix: PowerShell names are case-insensitive, so an array named $cpuSamples would be the [int] parameter $CpuSamples.
$cpuSeries = @()
for ($i = 0; $i -lt $CpuSamples; $i++) {
    $hostProc.Refresh()
    $c0 = $hostProc.TotalProcessorTime
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 1
    $hostProc.Refresh()
    $cpuSeries += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
}
$last5AvgCpu = [math]::Round(($cpuSeries | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
$ramOk       = ($privateWsMb -lt 60.0)
$cpuOk       = ($last5AvgCpu -lt 1.0)

$reg10Pass = $true
$reg10Exit = 0
$regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task10.ps1') -SkipBuild | Out-Host
    $reg10Exit = $LASTEXITCODE
    $flag = {
        param($name)
        $p = Join-Path $HandoffDir "${name}_verification.json"
        (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    }
    $t10 = & $flag 'task10'
    $t09 = & $flag 'task09'
    $t08 = & $flag 'task08'
    $t07 = & $flag 'task07'
    $t06 = & $flag 'task06'
    $t05 = & $flag 'task05'
    $t04 = & $flag 'task04'
    $t03 = & $flag 'task03'
    $reg10Pass = ($reg10Exit -eq 0) -and $t10 -and $t09 -and $t08 -and $t07 -and $t06 -and $t05 -and $t04 -and $t03
    $regDetail = "Verify-Task10=$t10, 09=$t09, 08=$t08, 07=$t07, 06=$t06, 05=$t05, 04=$t04, 03=$t03"

    # Re-populate cache after legacy regression tests cleared it
    $null = Invoke-SelfTest 'cache-commands' @{
        NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
    }
}

$e5Pass = $ramOk -and $cpuOk -and $liveOk -and $reg10Pass
Write-Host ("E5: privateWsMb={0}MB (<60={1}) workingSetMb={2}MB idleCpuLast5={3}% (<1%={4}) liveOk={5} (skipped={6}) reg={7} -> {8}" -f `
    $privateWsMb, $ramOk, $workingSetMb, $last5AvgCpu, $cpuOk, $liveOk, $live11.data.skipped, $regDetail,
    $(if ($e5Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($e5Pass) { 'Green' } else { 'Red' })

$overallPass = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task        = 'TASK-11'
    gate        = 'Gate 11'
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    machine     = $env:COMPUTERNAME
    allPassed   = $overallPass
    overallPass = $overallPass
    relaxedSla  = $script:SlaRelaxed
    slaWarnings = $script:ScriptSlaWarnings
    E1_BuildZeroBomAndSchema11 = [ordered]@{
        pass          = $e1Pass
        buildExitCode = $buildCode
        buildWarnings = $buildWarnings
        buildErrors   = $buildErrors
        bomOffenders  = $bomOffenders
        schema11      = $schema11.data
    }
    E2_DeterministicResponseCache = [ordered]@{
        pass           = $e2Pass
        cacheHitBypass = $e2.data
    }
    E3_LocalVaultGrounding = [ordered]@{
        pass           = $e3Pass
        vaultGrounding = $e3.data
    }
    E4_SlashCommandsAndMcpTools = [ordered]@{
        pass               = $e4Pass
        cacheCommands      = $e4Self.data
        pipeCache          = $pipeCache
        pipeCacheStats     = $pipeCacheStats
        pipeCacheClear     = $pipeCacheClear
        mcpToolsListed     = (($toolNames -contains 'get_gemini_cache_stats') -and ($toolNames -contains 'search_local_vault'))
        mcpCacheStats      = $mcpCacheStats
        mcpSearchVault     = $mcpVault
        mcpSearchElapsedMs = $mcpSearchElapsedMs
    }
    E5_LiveE2eResourceAndRegression = [ordered]@{
        pass                = $e5Pass
        privateWorkingSetMb = $privateWsMb
        workingSetMb        = $workingSetMb
        ramUnder60Mb        = $ramOk
        cpuSamplesPct       = $cpuSeries
        idleCpuLast5AvgPct  = $last5AvgCpu
        idleCpuUnder1Pct    = $cpuOk
        liveE2e11           = $live11.data
        regression          = [ordered]@{
            ran      = (-not $SkipRegression)
            exitCode = $reg10Exit
            detail   = $regDetail
            pass     = $reg10Pass
        }
    }
}

[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 12), $Utf8NoBom)
Write-Host "`nGate 11 verification report written to $ReportPath (allPassed=$overallPass)" -ForegroundColor $(if ($overallPass) { 'Green' } else { 'Red' })
if (-not $overallPass) { exit 1 }
exit 0
