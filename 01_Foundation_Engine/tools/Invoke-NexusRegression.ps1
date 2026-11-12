#Requires -Version 5.1
<#
.SYNOPSIS
    ADR-55 two-tier regression entry point (TASK-14b).
.DESCRIPTION
    -FastRegression  daily development loop, target < 90 s, no Gemini call:
                     current gate (Verify-Task14.ps1) + previous gate (Verify-Task11.ps1 = Gate 11b, -SkipLive -SkipRegression)
                     + one foundation gate (Verify-Task06.ps1: config / ADR-19 state machine, -SkipRegression).
    -FullRegression  release tags (default): Verify-Task14.ps1 with the full chain Verify-Task11 -> Verify-Task03
                     (real Gemini calls in Task07/09/10/11 E5 when a key is stored, unless -SkipLive; -SkipLive reaches the whole chain since Gate 15).
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

$runStartUtc = [DateTime]::UtcNow
$covered = if ($Mode -eq 'Fast') { @('task14', 'task11', 'task06') } else { @('task14', 'task11', 'task10', 'task09', 'task08', 'task07', 'task06', 'task05', 'task04', 'task03') }
$gateReportPath = Join-Path $HandoffDir 'task14_verification.json'
function Invoke-GateOnce {
    $startUtc = [DateTime]::UtcNow
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    & powershell @gateArgs | Out-Host
    $exit = $LASTEXITCODE
    $watch.Stop()
    $report = if (Test-Path $gateReportPath) { Get-Content $gateReportPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $f = [ordered]@{}
    foreach ($n in $covered) {
        $p = Join-Path $HandoffDir "${n}_verification.json"
        $f[$n] = (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed)
    }
    # selftest reports of this attempt that failed (a retry overwrites them, so their names are kept here)
    $failedSelftests = @(Get-ChildItem -Path $HandoffDir -Filter 'task*_selftest_*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $startUtc } | Where-Object {
            try { -not [bool]((Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).pass) } catch { $true } } | ForEach-Object { $_.BaseName })
    $sec = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    $modeOk = ($null -ne $report) -and ([string]$report.regressionMode -eq $Mode)
    $budgetOk = ($Mode -ne 'Fast') -or ($sec -lt $FastBudgetSeconds)
    return [ordered]@{ exit = $exit; seconds = $sec; report = $report; flags = $f; modeMatches = $modeOk; withinBudget = $budgetOk
        failedSelftests = $failedSelftests; ok = ($exit -eq 0) -and $modeOk -and $budgetOk -and -not ($f.Values -contains $false) }
}

# Architect 17a.6 (2026-11-12): one retry against micro-latency noise in the 100+ selftest chain. A run that passes only on
# the retry passes with [SLA-WARN (retry)]; the first attempt's failed flags and selftests are recorded. NEXUS_REGRESSION_NO_RETRY=1
# turns the retry off.
$attempts = @(Invoke-GateOnce)
if (-not $attempts[0].ok -and $env:NEXUS_REGRESSION_NO_RETRY -ne '1') {
    Write-Host ("[RETRY 1/1] regression {0} failed first ({1}; failed selftests: {2}) - running it once more" -f $Mode,
        (($attempts[0].flags.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','),
        ($(if ($attempts[0].failedSelftests.Count) { $attempts[0].failedSelftests -join ',' } else { 'none' }))) -ForegroundColor Yellow
    $attempts += ,(Invoke-GateOnce)
}
$last = $attempts[-1]
$gateExit = $last.exit
$seconds = [math]::Round((@($attempts | ForEach-Object { $_.seconds }) | Measure-Object -Sum).Sum, 1)
$gateReport = $last.report
$flags = $last.flags
$git = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$modeMatches = $last.modeMatches
$withinBudget = $last.withinBudget   # the Fast budget is judged per attempt
$pass = $last.ok -and $git.Same
$retryWarning = $null
if ($attempts.Count -gt 1 -and $last.ok) {
    $retryWarning = [pscustomobject]@{ Source = "regression_$($Mode.ToLowerInvariant())"; Metric = 'retry'; MeasuredMs = $null; SlaMs = $null; CeilingMs = $null
        FirstFailedFlags = @($attempts[0].flags.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }); FirstFailedSelftests = @($attempts[0].failedSelftests) }
    Write-Host ("[SLA-WARN (retry, Architect 17a.6)] regression {0} passed on the retry; first attempt failed: {1} (selftests: {2})" -f $Mode,
        ($retryWarning.FirstFailedFlags -join ','), ($(if ($retryWarning.FirstFailedSelftests.Count) { $retryWarning.FirstFailedSelftests -join ',' } else { 'none' }))) -ForegroundColor Yellow
}

# TASK-13a (ADR-55 drift tracking): [SLA-WARN SUMMARY], [DRIFT-ALERT] for > 2 warnings in this run or the same metric
# warned in 3 consecutive Gates. Warnings never fail the run; they are recorded for qa_report.md and the handoff.
$slaWarnings = @()
$driftMetrics = @()
$driftCount = $false
if ($Mode -eq 'Fast') {
    $slaWarnings = @(Get-NexusSlaWarnings $HandoffDir $runStartUtc)
    $driftCount = Write-NexusSlaSummary $slaWarnings
    $driftMetrics = @(Update-NexusSlaHistory $HandoffDir $gitStart.Branch $gitStart.Head $slaWarnings)
}
if ($retryWarning) { $slaWarnings += $retryWarning }

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
    slaWarningCount = @($slaWarnings).Count
    slaWarnings     = @($slaWarnings)
    driftAlert      = ($driftCount -or (@($driftMetrics).Count -gt 0))
    driftMetrics    = @($driftMetrics)
    attempts        = @($attempts | ForEach-Object { [ordered]@{ exit = $_.exit; seconds = $_.seconds; ok = $_.ok; flags = $_.flags; failedSelftests = $_.failedSelftests } })
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
