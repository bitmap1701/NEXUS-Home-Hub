<#
.SYNOPSIS
    TASK-03 / Gate 3 verification (E1-E5) for NEXUS Home Hub. Green tier: builds and runs tests only.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror
    E2  MCP tools/list exposes list_agent_squads, dispatch_agent_squad, update_dispatch_verdict
    E3  dispatch "Toi uu UDP Socket C# ..." -> Squad1, persona loaded, row DISPATCHED (+ verdict round-trip, P95 < 50 ms)
    E4  preferredAgentId "../../GEMINI.md" is rejected
    E5  HostService: JSONL pipe NEXUS_Home_Cockpit_Pipe answers, log shows RenderMode=SoftwareOnly + NOACTIVATE|TOOLWINDOW
    Results are written to 03_Local_Data_Vault/handoff/task03_verification.json for Gemini (Reality Checker).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task03.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [int]$DispatchRuns = 20
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
$hostLog = Join-Path $handoff 'hostservice.log'

$results = [ordered]@{}
function Set-Result($id, [bool]$pass, $detail) {
    $results[$id] = [ordered]@{ pass = $pass; detail = $detail }
    $color = if ($pass) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} - {2}" -f $id, ($(if ($pass) { 'PASS' } else { 'FAIL' })), $detail) -ForegroundColor $color
}

function New-Rpc([int]$id, [string]$method, $params) {
    $msg = [ordered]@{ jsonrpc = '2.0'; id = $id; method = $method }
    if ($null -ne $params) { $msg.params = $params }
    return ($msg | ConvertTo-Json -Compress -Depth 8)
}

function New-ToolCall([int]$id, [string]$name, $arguments) {
    return New-Rpc $id 'tools/call' ([ordered]@{ name = $name; arguments = $arguments })
}

# Sends all requests through one MCP stdio session; returns responses keyed by id.
function Invoke-Mcp([string[]]$requests) {
    $stdin = ($requests -join "`n") + "`n"
    $lines = $stdin | & $mcpExe
    $byId = @{}
    foreach ($l in $lines) {
        if ([string]::IsNullOrWhiteSpace($l)) { continue }
        $resp = $l | ConvertFrom-Json
        if ($null -ne $resp.id) { $byId[[int]$resp.id] = $resp }
    }
    return $byId
}

function Get-ToolText($resp) {
    if ($null -eq $resp) { return $null }
    if ($resp.error) { return "JSON-RPC error: $($resp.error.message)" }
    return $resp.result.content[0].text
}

Write-Host "NEXUS Home Hub - TASK-03 verification (root: $Root)" -ForegroundColor Cyan

# ---------------- E1 ----------------
if ($SkipBuild) {
    Set-Result 'E1' (Test-Path $mcpExe) 'Build skipped (-SkipBuild); using existing binaries'
} else {
    $buildOut = & dotnet build $sln -c Release -warnaserror 2>&1
    $buildOut | Out-Host
    $summary = ($buildOut | Select-String -Pattern 'Warning\(s\)|Error\(s\)' | ForEach-Object { $_.Line.Trim() }) -join '; '
    Set-Result 'E1' ($LASTEXITCODE -eq 0) "dotnet build -warnaserror exit=$LASTEXITCODE ($summary)"
}
if (-not (Test-Path $mcpExe)) { throw "MCP server not found: $mcpExe" }

# ---------------- E2 + E3 + E4 (one MCP session) ----------------
$task = 'Tối ưu UDP Socket C# không rớt gói tin'
$requests = @(
    (New-Rpc 1 'initialize' @{}),
    (New-Rpc 2 'tools/list' $null),
    (New-ToolCall 3 'dispatch_agent_squad' ([ordered]@{ taskDescription = $task; nexusMode = 'Micro' })),
    (New-ToolCall 4 'dispatch_agent_squad' ([ordered]@{ taskDescription = $task; preferredAgentId = '../../GEMINI.md' })),
    (New-ToolCall 5 'list_agent_squads' ([ordered]@{ includeAgents = $false }))
)
for ($i = 0; $i -lt $DispatchRuns; $i++) {
    $requests += New-ToolCall (100 + $i) 'dispatch_agent_squad' ([ordered]@{ taskDescription = "$task (perf run $i)"; nexusMode = 'Sprint' })
}
$r = Invoke-Mcp $requests

$toolNames = @($r[2].result.tools | ForEach-Object { $_.name })
$required = 'list_agent_squads', 'dispatch_agent_squad', 'update_dispatch_verdict'
$missing = @($required | Where-Object { $toolNames -notcontains $_ })
Set-Result 'E2' ($missing.Count -eq 0) ("tools/list = {0} tools; missing: {1}" -f $toolNames.Count, ($(if ($missing) { $missing -join ',' } else { 'none' })))

$e3Text = Get-ToolText $r[3]
$e3Pass = $false; $e3Detail = $e3Text; $dispatchId = 0
try {
    $pkg = $e3Text | ConvertFrom-Json
    $dispatchId = [long]$pkg.dispatchId
    $hasPersona = $pkg.systemInstruction -match '## 1\. Identity' -and $pkg.systemInstruction.Length -gt 1000
    $e3Pass = ($pkg.squadId -eq 'Squad1') -and $hasPersona -and ($dispatchId -gt 0)
    $placeholder = $pkg.systemInstruction -match 'seeded: placeholder'
    $e3Detail = "squad=$($pkg.squadId) primary=$($pkg.primaryAgent.id) qa=$($pkg.qaAgent.id) dispatchId=$dispatchId instructionChars=$($pkg.systemInstruction.Length) placeholderPersona=$placeholder matched=[$($pkg.matchedKeywords -join ',')]"
} catch { $e3Detail = "Unparseable dispatch result: $e3Text" }

# Verdict round-trip proves the DISPATCHED row exists in agent_dispatch_logs.
if ($dispatchId -gt 0) {
    $v = Invoke-Mcp @(
        (New-ToolCall 6 'update_dispatch_verdict' ([ordered]@{ dispatchId = $dispatchId; verdict = 'PASSED'; evidenceNotes = 'Verify-Task03.ps1 E3 round-trip' })),
        (New-ToolCall 7 'update_dispatch_verdict' ([ordered]@{ dispatchId = 999999999; verdict = 'PASSED'; evidenceNotes = 'must fail' }))
    )
    $verdictText = Get-ToolText $v[6]
    $okRow = $verdictText -match '"saved":true'
    $noGhost = [bool]$v[7].result.isError
    $e3Pass = $e3Pass -and $okRow -and $noGhost
    $e3Detail += " | verdictRoundTrip=$okRow unknownIdRejected=$noGhost"
    if (-not $okRow) {
        # Show exactly what the server answered, plus the MCP debug log, so the failure can be diagnosed.
        $e3Detail += " | verdictResponse=[$verdictText] isError=$($v[6].result.isError) responses=$($v.Count)"
        $mcpLog = Join-Path $handoff 'mcp_debug.log'
        if (Test-Path $mcpLog) {
            Write-Host '--- mcp_debug.log (last 8 lines) ---' -ForegroundColor Yellow
            Get-Content $mcpLog -Tail 8 -Encoding UTF8 | Out-Host
        }
    }
}

$lat = @()
for ($i = 0; $i -lt $DispatchRuns; $i++) {
    $t = Get-ToolText $r[100 + $i]
    try { $lat += [double](($t | ConvertFrom-Json).elapsedMs) } catch { }
}
$p95 = $null
if ($lat.Count -gt 0) {
    $sorted = $lat | Sort-Object
    $p95 = $sorted[[math]::Ceiling(0.95 * $sorted.Count) - 1]
    $e3Pass = $e3Pass -and ($p95 -lt 50)
    $e3Detail += (" | dispatch P95={0:N3} ms over {1} runs (in-process)" -f $p95, $lat.Count)
}
Set-Result 'E3' $e3Pass $e3Detail

$e4Text = Get-ToolText $r[4]
$e4Pass = [bool]$r[4].result.isError -and ($e4Text -match 'Rejected')
Set-Result 'E4' $e4Pass "preferredAgentId=../../GEMINI.md -> $e4Text"

$squads = Get-ToolText $r[5] | ConvertFrom-Json
$squadInfo = @($squads.squads | ForEach-Object { "$($_.squadId)=$($_.primaryAgent.id)($(if ($_.primaryAgent.available) {'ok'} else {'MISSING'}))" }) -join ' '
Write-Host "Registry: $($squads.totalAgents) agents, $($squads.dispatchLogCount) dispatch rows | $squadInfo" -ForegroundColor Gray

# ---------------- E5 ----------------
$e5Detail = @()
$e5Pass = $true
$running = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue
$startedHere = $false
if (-not $running) {
    if (-not (Test-Path $hostExe)) { throw "HostService not found: $hostExe" }
    Start-Process -FilePath $hostExe | Out-Null
    $startedHere = $true
    Start-Sleep -Seconds 3
}

function Send-PipeLine([string]$line) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'NEXUS_Home_Cockpit_Pipe', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(5000)
        $writer = [System.IO.StreamWriter]::new($client, $utf8); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $utf8)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}

try {
    $status = Send-PipeLine '{"cmd":"STATUS"}' | ConvertFrom-Json
    $e5Pass = $e5Pass -and [bool]$status.ok
    $e5Detail += "pipe JSONL STATUS ok=$($status.ok)"
} catch {
    $e5Pass = $false
    $e5Detail += "pipe JSONL failed: $($_.Exception.Message) (stop any HostService started from an older build, then re-run)"
}

if ($startedHere) {
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    Start-Sleep -Seconds 1
}

# Only judge the most recent HostService start, so stale log lines cannot produce a PASS.
$logText = if (Test-Path $hostLog) { Get-Content $hostLog -Raw -Encoding UTF8 } else { '' }
$lastStart = $logText.LastIndexOf('[HOST] NEXUSHome.HostService starting')
if ($lastStart -ge 0) { $logText = $logText.Substring($lastStart) }
$soft = $logText -match 'RenderMode=SoftwareOnly'
$hud = $logText -match 'HUD style: NOACTIVATE\|TOOLWINDOW'
$monitor = ([regex]::Matches($logText, 'Target monitor: (.+)') | Select-Object -Last 1).Groups[1].Value
$e5Pass = $e5Pass -and $soft -and $hud
$e5Detail += "RenderMode=SoftwareOnly logged=$soft", "HUD NOACTIVATE|TOOLWINDOW logged=$hud", "monitor=$monitor"
Set-Result 'E5' $e5Pass ($e5Detail -join ' | ')

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-03'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    dispatchP95Ms = $p95
    registryAgents = $squads.totalAgents
    results = $results
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task03_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 6), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL PASS (E1-E5)' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
