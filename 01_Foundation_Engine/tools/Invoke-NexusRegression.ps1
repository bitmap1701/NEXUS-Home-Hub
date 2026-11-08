#Requires -Version 5.1
<#
.SYNOPSIS
    ADR-55 two-tier regression entry point (TASK-14b).
.DESCRIPTION
    -FastRegression  daily development loop, target < 90 s, no Gemini call:
                     current gate (Verify-Task14.ps1) + previous gate (Verify-Task11.ps1 = Gate 11b, -SkipLive -SkipRegression)
                     + one foundation gate (Verify-Task06.ps1: config / ADR-19 state machine, -SkipRegression).
    -FullRegression  release tags (default): Verify-Task14.ps1 with the full chain Verify-Task11 -> Verify-Task03
                     (one real Gemini call in Task11 E5 unless -SkipLive).
    Both modes keep the git guard (branch + HEAD unchanged, tree clean) and write
    03_Local_Data_Vault/handoff/regression_<fast|full>.json (git-ignored). When a new gate is added, point $CurrentGate at
    its Verify script; the fast tier then runs it plus the gate before it.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Invoke-NexusRegression.ps1 -FastRegression
#>
param(
    [switch]$FastRegression,
    [switch]$FullRegression,
    [switch]$SkipBuild,
    [switch]$SkipLive
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
if ($FastRegression -and $FullRegression) { throw 'Use either -FastRegression or -FullRegression, not both.' }
$Mode = if ($FastRegression) { 'Fast' } else { 'Full' }
# Decision (0b): Verify-Task14 sets NEXUS_RELAXED_SLA=1 for the fast tier; the full tier must never inherit it.
if ($Mode -eq 'Full' -and (Test-Path Env:NEXUS_RELAXED_SLA)) { Remove-Item Env:NEXUS_RELAXED_SLA }

$EngineRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot    = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir  = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$CurrentGate = 'Verify-Task14.ps1'
$Utf8NoBom   = New-Object System.Text.UTF8Encoding($false)
$FastBudgetSeconds = 90
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("=== NEXUS regression ({0}) on {1} @ {2} ===" -f $Mode, $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan

$gateArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $CurrentGate))
$gateArgs += $(if ($Mode -eq 'Fast') { '-FastRegression' } else { '-FullRegression' })
if ($SkipBuild) { $gateArgs += '-SkipBuild' }
if ($SkipLive -and $Mode -eq 'Full') { $gateArgs += '-SkipLive' }

$watch = [System.Diagnostics.Stopwatch]::StartNew()
& powershell @gateArgs | Out-Host
$gateExit = $LASTEXITCODE
$watch.Stop()
$seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)

$gateReportPath = Join-Path $HandoffDir 'task14_verification.json'
$gateReport = if (Test-Path $gateReportPath) { Get-Content $gateReportPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$flagsOf = {
    param($names)
    $o = [ordered]@{}
    foreach ($n in $names) {
        $p = Join-Path $HandoffDir "${n}_verification.json"
        $o[$n] = (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    }
    return $o
}
$covered = if ($Mode -eq 'Fast') { @('task14', 'task11', 'task06') } else { @('task14', 'task11', 'task10', 'task09', 'task08', 'task07', 'task06', 'task05', 'task04', 'task03') }
$flags = & $flagsOf $covered
$git = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$modeMatches = ($null -ne $gateReport) -and ([string]$gateReport.regressionMode -eq $Mode)
$withinBudget = ($Mode -ne 'Fast') -or ($seconds -lt $FastBudgetSeconds)
$pass = ($gateExit -eq 0) -and $modeMatches -and -not ($flags.Values -contains $false) -and $git.Same -and $withinBudget

$summary = [ordered]@{
    mode            = $Mode
    timestamp       = (Get-Date).ToUniversalTime().ToString('o')
    machine         = $env:COMPUTERNAME
    seconds         = $seconds
    fastBudgetSeconds = $FastBudgetSeconds
    withinBudget    = $withinBudget
    currentGate     = $CurrentGate
    gateExitCode    = $gateExit
    gateReportMode  = $(if ($gateReport) { $gateReport.regressionMode } else { $null })
    coverage        = $flags
    gitStart        = $gitStart
    gitSame         = $git.Same
    gitReasons      = $git.Reasons
    pass            = $pass
}
$out = Join-Path $HandoffDir ("regression_{0}.json" -f $Mode.ToLowerInvariant())
[System.IO.File]::WriteAllText($out, ($summary | ConvertTo-Json -Depth 6), $Utf8NoBom)
Write-Host ("NEXUS regression {0}: {1} s{2} | {3} | git guard same={4} {5} -> {6}" -f $Mode, $seconds,
    $(if ($Mode -eq 'Fast') { " (budget < $FastBudgetSeconds s: $withinBudget)" } else { '' }),
    (($flags.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join ', '), $git.Same, ($git.Reasons -join '; '),
    $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
Write-Host "Summary: $out"
if (-not $pass) { exit 1 }
exit 0
