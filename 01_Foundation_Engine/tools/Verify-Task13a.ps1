#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 13a automated verification (TASK-13a: Cockpit memory profiling & slimming, HUD Private WS
    label, [SLA-WARN SUMMARY] drift tracking).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0) + repository-wide Zero-BOM gate (Test-NexusBom).
    E2  Memory A/B on the real HostService (WPF Cockpit, mock Gemini, no tokens): the same load (/ask x6, /g x10,
        /calc x30, /note list + find, /status, /mem), then HIDE and 8 s idle, once with NEXUS_MEMORY_SLIMMING=0
        ("before") and once with slimming on ("after"). PASS: after Private WS <= 52.0 MB (target <= 50.0), the idle
        trim ran, and selftest mem-profile passes. The breakdown (managed heap, GC committed, SQLite, agents, vault)
        is printed for both runs.
    E3  HUD label: the "Private WS: xx.x MB / 60 MB" text on the HUD badge (pipe MEM) is within 2.0 MB of this
        script's Private WS measurement, /status shows "Private WS:", and the label is not PrivateMemorySize64.
    E4  Invoke-NexusRegression -FastRegression -SkipBuild: < 90 s, every gate PASS, and the run prints
        "[SLA-WARN SUMMARY] Count=...".
    E5  Cockpit idle CPU after the settle window < 0.5 % (from that fast run), git branch + HEAD unchanged and tree
        clean, and the latest GitHub Actions run for this HEAD when the API is reachable.
    Writes 03_Local_Data_Vault/handoff/task13a_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$ReportPath = Join-Path $HandoffDir 'task13a_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$TargetMb   = 52.0
$GoalMb     = 50.0
$Gate14bMb  = 57.7
$IdleWaitMs = 8000   # idle trim fires 5 s after the last activity; +3 s settle
$MockAnswer = 'Gate 13a mock answer: Private Working Set theo ADR-09.'
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

foreach ($v in 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES', 'NEXUS_MEMORY_SLIMMING', 'NEXUS_RELAXED_SLA') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Result([string]$id, [bool]$pass, [string]$detail) {
    Write-Host ("{0}: {1} -> {2}" -f $id, $detail, $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}

function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}
function Send-CockpitCommand([string]$text) {
    $outer = Send-PipeLine ([ordered]@{ cmd = 'COCKPIT'; text = $text } | ConvertTo-Json -Compress) | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}
function Get-PipeJson([string]$cmd) {
    $outer = Send-PipeLine ('{"cmd":"' + $cmd + '"}') | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task13a.ps1.'
    }
}
function Get-PrivateWsMb([int]$processId) {
    $perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $processId" -ErrorAction SilentlyContinue
    if ($perf) { return [math]::Round($perf.WorkingSetPrivate / 1MB, 1) }
    return $null
}

<# One A/B leg: fresh HostService with mock Gemini and the given NEXUS_MEMORY_SLIMMING, same load, idle, measure. #>
function Measure-HostMemory([string]$slimming) {
    Stop-HostService
    $saved = @{}
    $overrides = @{ NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer; NEXUS_MEMORY_SLIMMING = $slimming }
    foreach ($k in $overrides.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process'); [Environment]::SetEnvironmentVariable($k, $overrides[$k], 'Process') }
    try { Start-Process -FilePath $HostExe | Out-Null }
    finally { foreach ($k in $overrides.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') } }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) { try { $null = Get-PipeJson 'SYNCSTATE'; break } catch { Start-Sleep -Milliseconds 300 } }
    Start-Sleep -Seconds 3
    $proc = Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1
    $startMb = Get-PrivateWsMb $proc.Id

    $failed = @()
    $commands = @()
    for ($i = 0; $i -lt 6; $i++) { $commands += "/ask Gate 13a memory load $i - ADR-09 Private Working Set" }
    for ($i = 0; $i -lt 10; $i++) { $commands += '/g ADR-09 Private Working Set Gate 13a' }
    for ($i = 0; $i -lt 30; $i++) { $commands += ("/calc sqrt({0}) + 2^8" -f (144 + $i)) }
    $commands += '/note list', '/note find gate'
    foreach ($c in $commands) {
        $r = Send-CockpitCommand $c
        if (-not [bool]$r.ok) { $failed += ("{0}: {1}" -f $c, $r.message) }
    }
    Start-Sleep -Milliseconds 1500   # mock AI tasks of the /ask load finish in the background
    $status = Send-CockpitCommand '/status'
    $memCmd = Send-CockpitCommand '/mem'
    $loadedMb = Get-PrivateWsMb $proc.Id
    $null = Send-PipeLine '{"cmd":"HIDE"}'
    Start-Sleep -Milliseconds $IdleWaitMs
    # Gate 13a run 1: after the trim, Private WS keeps falling for ~3-4 s (GC decommit), and the WMI counter lags ~1 s.
    # Read it every 1 s until two reads agree within 0.5 MB (max 8 s more), so the HUD label is compared to a settled value.
    $idleMb = Get-PrivateWsMb $proc.Id
    $settleReads = 1
    for ($k = 0; $k -lt 8; $k++) {
        Start-Sleep -Milliseconds 1000
        $next = Get-PrivateWsMb $proc.Id
        $settleReads++
        $settled = ($null -ne $idleMb) -and ($null -ne $next) -and ([math]::Abs($next - $idleMb) -le 0.5)
        $idleMb = $next
        if ($settled) { break }
    }
    $mem = Get-PipeJson 'MEM'
    return [pscustomobject]@{
        Slimming      = ($slimming -ne '0')
        ProcessId     = $proc.Id
        StartMb       = $startMb
        LoadedMb      = $loadedMb
        IdleMb        = $idleMb
        SettleReads   = $settleReads
        Mem           = $mem
        StatusMessage = [string]$status.message
        MemMessage    = [string]$memCmd.message
        LoadFailures  = $failed
        Commands      = $commands.Count
    }
}

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan

# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror) + repository-wide Zero-BOM gate'
$buildCode = 0; $buildWarnings = 0; $buildErrors = 0
Stop-HostService
if (-not $SkipBuild) {
    $buildText = (& dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1 | Out-String)
    $buildCode = $LASTEXITCODE
    Write-Host $buildText.Trim()
    if ($buildText -match '(\d+)\s+Warning\(s\)') { $buildWarnings = [int]$Matches[1] }
    if ($buildText -match '(\d+)\s+Error\(s\)')   { $buildErrors   = [int]$Matches[1] }
}
$bom = Test-NexusBom $RepoRoot
foreach ($o in $bom.Offenders) { Write-Host "  BOM rule violated: $o" -ForegroundColor Red }
$e1Pass = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0) -and $bom.Pass
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} (skipped={3}) | Zero-BOM: {4} files, {5} offender(s)" -f `
    $buildCode, $buildWarnings, $buildErrors, [bool]$SkipBuild, $bom.Checked, @($bom.Offenders).Count)

# --------------------------------------------- E2 ---------------------------------------------
Write-Step "E2: memory A/B on the real HostService (target <= $TargetMb MB, goal <= $GoalMb MB; Gate 14b: $Gate14bMb MB)"
$selfPath = Join-Path $HandoffDir 'task13a_selftest_mem-profile.json'
if (Test-Path $selfPath) { Remove-Item $selfPath -Force }
$env:NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
try { $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', 'mem-profile') -PassThru -Wait; $selfExit = $p.ExitCode }
finally { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE -ErrorAction SilentlyContinue }
$self = if (Test-Path $selfPath) { Get-Content $selfPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$selfPass = ($selfExit -eq 0) -and ($null -ne $self) -and [bool]$self.pass

$before = Measure-HostMemory '0'
$after  = Measure-HostMemory '1'
Stop-HostService   # the mock HostService must not stay running

function Format-Breakdown($leg) {
    $s = $leg.Mem.snapshot
    return ("Private WS start {0} -> loaded {1} -> idle {2} MB | managed {3} MB, GC heap {4} MB (committed {5}), SQLite {6} MB, agents {7} (~{8} KB), threads {9} | trim: {10}" -f `
        $leg.StartMb, $leg.LoadedMb, $leg.IdleMb, $s.ManagedHeapAllocatedMb, $s.GcHeapSizeMb, $s.GcCommittedMb, $s.SqliteNativeMb,
        $s.AgentCount, [math]::Round([double]$s.AgentRegistryCacheMb * 1024, 0), $s.ThreadCount,
        $(if ($leg.Mem.lastTrim) { "{0} -> {1} MB in {2} ms" -f $leg.Mem.lastTrim.PrivateWorkingSetBeforeMb, $leg.Mem.lastTrim.PrivateWorkingSetAfterMb, $leg.Mem.lastTrim.DurationMs } else { 'none' }))
}
Write-Host ("  BEFORE (slimming off): " + (Format-Breakdown $before))
Write-Host ("  AFTER  (slimming on) : " + (Format-Breakdown $after))
$reduction = if ($null -ne $before.IdleMb -and $null -ne $after.IdleMb) { [math]::Round($before.IdleMb - $after.IdleMb, 1) } else { $null }
$trimRan = ($null -ne $after.Mem.lastTrim) -and ([int]$after.Mem.trimCount -ge 1) -and (-not $before.Mem.slimmingEnabled) -and [bool]$after.Mem.slimmingEnabled
$loadOk = (@($before.LoadFailures).Count -eq 0) -and (@($after.LoadFailures).Count -eq 0)
foreach ($f in @($before.LoadFailures) + @($after.LoadFailures)) { Write-Host "  load failure: $f" -ForegroundColor Red }
$e2Pass = $selfPass -and $trimRan -and $loadOk -and ($null -ne $after.IdleMb) -and ($after.IdleMb -le $TargetMb)
Write-Result 'E2' $e2Pass ("idle Private WS before {0} MB -> after {1} MB (reduction {2} MB; <= {3}: {4}; goal <= {5}: {6}) | selftest mem-profile={7} | trim ran={8} | load ok={9}" -f `
    $before.IdleMb, $after.IdleMb, $reduction, $TargetMb, ($after.IdleMb -le $TargetMb), $GoalMb, ($after.IdleMb -le $GoalMb), $selfPass, $trimRan, $loadOk)

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: HUD memory label = Private Working Set (within 2.0 MB)'
$label = [string]$after.Mem.hudLabel
$badge = [string]$after.Mem.hudBadge
$labelMb = $null
if ($label -match 'Private WS: ([0-9]+(\.[0-9]+)?) MB / 60 MB') { $labelMb = [double]$Matches[1] }
$commitMb = [double]$after.Mem.snapshot.CommitMb
$labelDiff = if ($null -ne $labelMb -and $null -ne $after.IdleMb) { [math]::Round([math]::Abs($labelMb - $after.IdleMb), 2) } else { $null }
$e3Checks = [ordered]@{
    labelFormat    = ($null -ne $labelMb)
    withinTwoMb    = ($null -ne $labelDiff) -and ($labelDiff -lt 2.0)
    badgeShowsIt   = $badge.Contains('Private WS: ')
    statusShowsIt  = $after.StatusMessage.Contains('Private WS: ')
    notCommitCharge = ($null -ne $labelMb) -and ([math]::Abs($commitMb - $labelMb) -gt 2.0)
    source         = -not ([string]$after.Mem.snapshot.PrivateWorkingSetSource).StartsWith('unavailable')
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("HUD '{0}' vs measured {1} MB (diff {2} MB) | source {3} | commit (PrivateMemorySize64) {4} MB | badge: {5} | failed=[{6}]" -f `
    $label, $after.IdleMb, $labelDiff, $after.Mem.snapshot.PrivateWorkingSetSource, $commitMb, $badge,
    (($e3Checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: Invoke-NexusRegression -FastRegression (< 90 s, [SLA-WARN SUMMARY])'
$fastWatch = [System.Diagnostics.Stopwatch]::StartNew()
$fastLines = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-NexusRegression.ps1') -FastRegression -SkipBuild |
    ForEach-Object { Write-Host $_; [string]$_ })
$fastExit = $LASTEXITCODE
$fastWatch.Stop()
$fastSeconds = [math]::Round($fastWatch.Elapsed.TotalSeconds, 1)
$summaryLines = @($fastLines | Where-Object { $_ -match '^\[SLA-WARN SUMMARY\] Count=\d+ \| Details=' })
$fastSummaryPath = Join-Path $HandoffDir 'regression_fast.json'
$fastSummary = if (Test-Path $fastSummaryPath) { Get-Content $fastSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$e4Pass = ($fastExit -eq 0) -and ($fastSeconds -lt 90) -and ($summaryLines.Count -ge 1) -and ($null -ne $fastSummary) -and [bool]$fastSummary.pass
Write-Result 'E4' $e4Pass ("fast tier {0} s (< 90: {1}) exit {2} | {3} | drift alert={4}" -f `
    $fastSeconds, ($fastSeconds -lt 90), $fastExit, $(if ($summaryLines.Count) { $summaryLines[-1] } else { 'no [SLA-WARN SUMMARY] line' }), $fastSummary.driftAlert)

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: idle CPU < 0.5 %, git guard, GitHub Actions'
$t14Path = Join-Path $HandoffDir 'task14_verification.json'
$t14 = if (Test-Path $t14Path) { Get-Content $t14Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$cpu = if ($t14) { [double]$t14.E5_McpSyncResourcesRegression.idleCpuLast5AvgPct } else { 99 }
$fastRam = if ($t14) { [double]$t14.E5_McpSyncResourcesRegression.privateWorkingSetMb } else { $null }
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $headers = @{ 'User-Agent' = 'nexus-verify-task13a'; 'Accept' = 'application/vnd.github+json' }
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) -Headers $headers -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable or repository private: $($_.Exception.Message)" } }
$e5Pass = ($cpu -lt 0.5) -and $gitEnd.Same
Write-Result 'E5' $e5Pass ("Cockpit idle CPU {0} % (< 0.5) | fast-run Private WS after idle {1} MB | git same={2} {3} | CI: {4}" -f `
    $cpu, $fastRam, $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1} {2}" -f $ci.status, $ci.conclusion, $ci.url } else { $ci.note }))
if (-not ($ci.checked -and $ci.conclusion -eq 'success')) { Write-Host '  Confirm the green CI run in the GitHub Actions tab.' -ForegroundColor Yellow }

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-13a'; gate = 'Gate 13a'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart
    E1_BuildAndZeroBom = [ordered]@{ pass = $e1Pass; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders }
    E2_MemoryAB = [ordered]@{
        pass = $e2Pass; targetMb = $TargetMb; goalMb = $GoalMb; gate14bMb = $Gate14bMb; reductionMb = $reduction
        before = $before; after = $after; selftest = $self; trimRan = $trimRan; loadOk = $loadOk
    }
    E3_HudLabel = [ordered]@{ pass = $e3Pass; checks = $e3Checks; label = $label; labelMb = $labelMb; measuredMb = $after.IdleMb; diffMb = $labelDiff; badge = $badge; commitMb = $commitMb }
    E4_FastRegression = [ordered]@{ pass = $e4Pass; seconds = $fastSeconds; exitCode = $fastExit; summaryLine = $(if ($summaryLines.Count) { $summaryLines[-1] } else { $null }); summary = $fastSummary }
    E5_CpuGitCi = [ordered]@{ pass = $e5Pass; idleCpuPct = $cpu; fastRunPrivateWsMb = $fastRam; gitSame = $gitEnd.Same; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
Write-Host "`nGate 13a verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
