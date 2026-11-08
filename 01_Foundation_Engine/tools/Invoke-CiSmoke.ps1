#Requires -Version 5.1
<#
.SYNOPSIS
    ADR-56 headless CLI smoke test of NEXUSHome.McpTools over JSON-RPC stdio (GitHub Actions and local).
.DESCRIPTION
    Needs a Release build of NEXUSHome.McpTools. No window, no pipe, no Gemini call (NEXUS_GEMINI_MOCK_RESPONSE is set).
      1  initialize
      2  tools/list                 run_local_utility, dispatch_agent_squad, search_local_vault are listed
      3  run_local_utility calc     sqrt(144) + 2^8 = 268 (Hex: 0x10C | Bin: 0b100001100), 0 tokens, ai_model local-utility
      4  run_local_utility calc     10 / 0 -> isError + [CALC ERROR]
      5  run_local_utility timer    list -> ok
      6  run_local_utility note     a key-shaped text is refused by the Secret Guard (nothing written)
      7  dispatch_agent_squad       a C# networking task routes to a squad and gets a dispatch id (SQLite WAL)
    Exit code 0 = all checks pass.
    On a fresh checkout (CI) McpTools creates its config, SQLite database and placeholder core agents in the work tree;
    run this locally only in a clone you do not mind touching (it writes one dispatch row and latest_dispatch.json).
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$McpExe = Join-Path $RepoRoot "01_Foundation_Engine\NEXUSHome.McpTools\bin\$Configuration\net8.0\NEXUSHome.McpTools.exe"
if (-not (Test-Path $McpExe)) { Write-Host "McpTools not built: $McpExe" -ForegroundColor Red; exit 1 }

$env:NEXUS_ROOT = $RepoRoot
$env:NEXUS_GEMINI_MOCK_RESPONSE = 'CI smoke mock answer (no network).'
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

function New-Call([int]$id, [string]$name, $arguments) {
    return ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $name; arguments = $arguments } } | ConvertTo-Json -Compress -Depth 6)
}

$fakeKey = 'AIza' + ('Q' * 35)   # key-shaped, assembled at run time
$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}',
    (New-Call 3 'run_local_utility' ([ordered]@{ utility = 'calc'; commandText = 'sqrt(144) + 2^8' })),
    (New-Call 4 'run_local_utility' ([ordered]@{ utility = 'calc'; commandText = '10 / 0' })),
    (New-Call 5 'run_local_utility' ([ordered]@{ utility = 'timer'; commandText = 'list' })),
    (New-Call 6 'run_local_utility' ([ordered]@{ utility = 'note'; commandText = "CI key $fakeKey" })),
    (New-Call 7 'dispatch_agent_squad' ([ordered]@{ taskDescription = 'Thiết kế C# Async TCP Server với Redis caching' }))
)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lines = (($requests -join "`n") + "`n") | & $McpExe
$exitCode = $LASTEXITCODE
$sw.Stop()

$byId = @{}
foreach ($l in @($lines)) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    try { $r = $l | ConvertFrom-Json } catch { continue }
    if ($null -ne $r.id) { $byId[[int]$r.id] = $r }
}

function Get-ToolJson([int]$id) {
    if (-not $byId.ContainsKey($id) -or $null -eq $byId[$id].result) { return $null }
    try { return ([string]$byId[$id].result.content[0].text | ConvertFrom-Json) } catch { return $null }
}

$tools = @()
if ($byId.ContainsKey(2)) { $tools = @($byId[2].result.tools | ForEach-Object { $_.name }) }
$calc = Get-ToolJson 3
$div0 = Get-ToolJson 4
$timer = Get-ToolJson 5
$note = Get-ToolJson 6
$dispatchText = if ($byId.ContainsKey(7) -and $null -ne $byId[7].result) { [string]$byId[7].result.content[0].text } else { '' }
$dispatch = $null
try { $dispatch = $dispatchText | ConvertFrom-Json } catch { }

$checks = [ordered]@{
    initialize      = $byId.ContainsKey(1) -and ($byId[1].result.serverInfo.name -eq 'NEXUS-Local-MCP')
    toolsListed     = ($tools -contains 'run_local_utility') -and ($tools -contains 'dispatch_agent_squad') -and ($tools -contains 'search_local_vault')
    calcOk          = ($null -ne $calc) -and [bool]$calc.ok -and ([string]$calc.message -like '*= 268 (Hex: 0x10C | Bin: 0b100001100)*') -and
                      ([int]$calc.totalTokens -eq 0) -and ($calc.aiModel -eq 'local-utility')
    calcErrorOk     = ($null -ne $div0) -and [bool]$byId[4].result.isError -and ([string]$div0.message -like '`[CALC ERROR`]*')
    timerListOk     = ($null -ne $timer) -and [bool]$timer.ok
    noteSecretBlock = ($null -ne $note) -and [bool]$byId[6].result.isError -and [bool]$note.blocked
    dispatchOk      = ($null -ne $dispatch) -and ([long]$dispatch.dispatchId -gt 0) -and ([string]$dispatch.squadId -like 'Squad*')
}

foreach ($k in $checks.Keys) {
    Write-Host ("  {0,-16} {1}" -f $k, $(if ($checks[$k]) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($checks[$k]) { 'Green' } else { 'Red' })
}
Write-Host ("  calc: {0}" -f $calc.message)
Write-Host ("  /calc 10 / 0: {0}" -f $div0.message)
Write-Host ("  dispatch: #{0} {1} -> {2}" -f $dispatch.dispatchId, $dispatch.squadId, $dispatch.primaryAgent.name)
$pass = -not ($checks.Values -contains $false)
Write-Host ("Headless MCP smoke: {0} responses in {1} ms (McpTools exit {2}) -> {3}" -f $byId.Count, $sw.ElapsedMilliseconds, $exitCode,
    $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
if (-not $pass) {
    Write-Host 'Raw responses:'
    foreach ($l in @($lines)) { Write-Host "  $l" }
    exit 1
}
exit 0
