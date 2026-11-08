#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 14b automated verification (TASK-14b: ADR-55 two-tier regression, ADR-56 CI, hardening a/b/c).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0) + repository-wide Zero-BOM gate over `git ls-files` (Test-NexusBom: *.ps1 keep
        their BOM for Windows PowerShell 5.1, every other tracked file has none; includes nexus-ci.yml, handoff docs,
        docs/ADR_REGISTRY.md, current_task.md).
    E2  Git HEAD / branch immutability guard: the comparison flags a moved HEAD, a switched branch and a dirty tree on
        made-up states, and the real branch + HEAD are unchanged from the start to the end of this script.
    E3  ADR-55 fast tier: Invoke-NexusRegression.ps1 -FastRegression -SkipBuild finishes in < 90 s and covers Gate 14
        + Gate 11b (Verify-Task11) + Verify-Task06, all allPassed, no Gemini call.
    E4  Hardening b + RAM: from that run's task14_verification.json, Cockpit idle CPU after the settle window < 0.5 %,
        headless timer selftest idle CPU < 1 %, Private WS < 60 MB (soft warning at 54 MB reported, not failed).
    E5  ADR-56: .github/workflows/nexus-ci.yml has the required triggers, runner, .NET 8 setup and the BOM / build /
        smoke steps; if the GitHub API is reachable, the latest run for this branch and HEAD is reported.
    Writes 03_Local_Data_Vault/handoff/task14b_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$ReportPath = Join-Path $HandoffDir 'task14b_verification.json'
$Workflow   = Join-Path $RepoRoot '.github\workflows\nexus-ci.yml'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$FastBudgetSeconds = 90
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Result([string]$id, [bool]$pass, [string]$detail) {
    Write-Host ("{0}: {1} -> {2}" -f $id, $detail, $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan

# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror) + repository-wide Zero-BOM gate'
$buildCode = 0; $buildWarnings = 0; $buildErrors = 0
if (-not $SkipBuild) {
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        try {
            $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'NEXUS_Home_Cockpit_Pipe', [System.IO.Pipes.PipeDirection]::InOut)
            $client.Connect(3000)
            $w = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $w.AutoFlush = $true
            $w.WriteLine('{"cmd":"SHUTDOWN"}')
            $client.Dispose()
        } catch { }
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
        if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
            throw 'HostService did not exit after SHUTDOWN over the pipe (the build would fail on locked files). Close it yourself, then re-run.'
        }
    }
    $buildText = (& dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1 | Out-String)
    $buildCode = $LASTEXITCODE
    Write-Host $buildText.Trim()
    if ($buildText -match '(\d+)\s+Warning\(s\)') { $buildWarnings = [int]$Matches[1] }
    if ($buildText -match '(\d+)\s+Error\(s\)')   { $buildErrors   = [int]$Matches[1] }
}
$bom = Test-NexusBom $RepoRoot
foreach ($o in $bom.Offenders) { Write-Host "  BOM rule violated: $o" -ForegroundColor Red }
$required = @('.github/workflows/nexus-ci.yml', '03_Local_Data_Vault/handoff/gate14_handoff.md', 'docs/ADR_REGISTRY.md', '03_Local_Data_Vault/handoff/current_task.md')
$tracked = @(& git -C $RepoRoot ls-files -- $required)
$requiredTracked = (@($required | Where-Object { $tracked -contains $_ }).Count -eq $required.Count)
$e1Pass = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0) -and $bom.Pass -and $requiredTracked
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} (skipped={3}) | Zero-BOM: {4} tracked files, {5} offender(s) | required files tracked={6}" -f `
    $buildCode, $buildWarnings, $buildErrors, [bool]$SkipBuild, $bom.Checked, @($bom.Offenders).Count, $requiredTracked)

# --------------------------------------------- E2 (part 1: the guard itself) ---------------------------------------------
Write-Step 'E2: Git HEAD / branch immutability guard'
$base   = [pscustomobject]@{ Branch = 'claude/x'; Head = ('a' * 40); Dirty = @(); Error = $null }
$moved  = [pscustomobject]@{ Branch = 'claude/x'; Head = ('b' * 40); Dirty = @(); Error = $null }
$switch = [pscustomobject]@{ Branch = 'main'; Head = ('a' * 40); Dirty = @(); Error = $null }
$dirty  = [pscustomobject]@{ Branch = 'claude/x'; Head = ('a' * 40); Dirty = @(' M README.md'); Error = $null }
$guardCases = [ordered]@{
    identicalAccepted  = (Compare-NexusGitState $base $base -RequireClean).Same
    movedHeadRejected  = -not (Compare-NexusGitState $base $moved).Same
    switchedBranchRejected = -not (Compare-NexusGitState $base $switch).Same
    dirtyTreeRejected  = -not (Compare-NexusGitState $base $dirty -RequireClean).Same
}
$guardLogicOk = -not ($guardCases.Values -contains $false)

# --------------------------------------------- E3 ---------------------------------------------
Write-Step "E3: ADR-55 fast tier (Invoke-NexusRegression -FastRegression, budget < $FastBudgetSeconds s)"
$fastStartUtc = [DateTime]::UtcNow
$fastWatch = [System.Diagnostics.Stopwatch]::StartNew()
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-NexusRegression.ps1') -FastRegression -SkipBuild | Out-Host
$fastExit = $LASTEXITCODE
$fastWatch.Stop()
$fastSeconds = [math]::Round($fastWatch.Elapsed.TotalSeconds, 1)
$fastSummaryPath = Join-Path $HandoffDir 'regression_fast.json'
$fastSummary = if (Test-Path $fastSummaryPath) { Get-Content $fastSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$coverage = if ($fastSummary) { $fastSummary.coverage } else { $null }
$coverageOk = ($null -ne $coverage) -and [bool]$coverage.task14 -and [bool]$coverage.task11 -and [bool]$coverage.task06
$t14 = $null
$t14Path = Join-Path $HandoffDir 'task14_verification.json'
if (Test-Path $t14Path) { $t14 = Get-Content $t14Path -Raw -Encoding UTF8 | ConvertFrom-Json }
$liveSkipped = ($null -ne $t14) -and ([string]$t14.E5_McpSyncResourcesRegression.regression.mode -eq 'Fast') -and
               (@($t14.E5_McpSyncResourcesRegression.regression.scripts) -join ' ') -like '*-SkipLive*'
# Decision (0b): count the micro-latency SLAs that passed only with the FastRegression jitter tolerance.
$slaWarnings = @()
foreach ($f in @(Get-ChildItem -Path $HandoffDir -Filter 'task1*_selftest_*.json' -File -ErrorAction SilentlyContinue)) {
    if ($f.LastWriteTimeUtc -lt $fastStartUtc) { continue }   # only reports written by this fast run
    $j = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($w in @($j.slaWarnings)) { if ($null -ne $w) { $slaWarnings += ("{0}: {1} = {2} ms (SLA {3})" -f $f.BaseName, $w.metric, $w.measuredMs, $w.slaMs) } }
}
$relaxedUsed = ($null -ne $t14) -and [bool]$t14.relaxedSla
$e3Pass = ($fastExit -eq 0) -and ($fastSeconds -lt $FastBudgetSeconds) -and $coverageOk -and $liveSkipped -and $relaxedUsed
Write-Result 'E3' $e3Pass ("fast tier {0} s (< {1}: {2}) exit {3} | coverage task14={4} task11={5} task06={6} | no Gemini call={7} | relaxed SLA={8}, warnings={9}" -f `
    $fastSeconds, $FastBudgetSeconds, ($fastSeconds -lt $FastBudgetSeconds), $fastExit, $coverage.task14, $coverage.task11, $coverage.task06, $liveSkipped, $relaxedUsed, $slaWarnings.Count)
foreach ($w in $slaWarnings) { Write-Host "  [SLA-WARN (FastRegression jitter)] $w" -ForegroundColor Yellow }

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: CPU settle window (< 0.5 %) and Private WS (< 60 MB, soft warning 54 MB)'
$e5 = if ($t14) { $t14.E5_McpSyncResourcesRegression } else { $null }
$cockpitCpu  = if ($e5) { [double]$e5.idleCpuLast5AvgPct } else { 99 }
$headlessCpu = if ($t14) { [double]$t14.E3_LocalTimer.headless.idle.idleCpuPct } else { 99 }
$privateWs   = if ($e5) { [double]$e5.privateWorkingSetMb } else { 999 }
$e4Pass = ($null -ne $e5) -and ($cockpitCpu -lt 0.5) -and ($headlessCpu -lt 1.0) -and ($privateWs -lt 60.0) -and [bool]$e5.timersIdleBeforeCpu -and ([int]$e5.cpuSettleMs -ge 1500)
Write-Result 'E4' $e4Pass ("Cockpit idle CPU {0} % after {1} ms settle (timers idle={2}) | headless timer selftest {3} % (settle {4} ms) | Private WS {5} MB (soft 54: {6}, hard 60)" -f `
    $cockpitCpu, $e5.cpuSettleMs, $e5.timersIdleBeforeCpu, $headlessCpu, $t14.E3_LocalTimer.headless.idle.settleMs, $privateWs,
    $(if ($privateWs -ge 54.0) { 'WARNING' } else { 'ok' }))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: ADR-56 GitHub Actions workflow'
$wf = if (Test-Path $Workflow) { [System.IO.File]::ReadAllText($Workflow, $Utf8NoBom) } else { '' }
$wfChecks = [ordered]@{
    exists          = $wf.Length -gt 0
    pushBranches    = ($wf -match "branches:\s*\[\s*main,\s*'claude/\*\*',\s*'docs/\*\*'\s*\]")
    pullRequestMain = ($wf -match "pull_request:\s*\r?\n\s*branches:\s*\[\s*main\s*\]")
    windowsLatest   = ($wf -match 'runs-on:\s*windows-latest')
    checkoutV4      = ($wf -match 'uses:\s*actions/checkout@v4')
    setupDotnet8    = ($wf -match 'uses:\s*actions/setup-dotnet@v4') -and ($wf -match "dotnet-version:\s*'8\.0\.x'")
    bomStep         = ($wf -match 'Test-NexusBom\.ps1')
    strictBuild     = ([regex]::Matches($wf, 'dotnet build .*-warnaserror')).Count -ge 2
    smokeStep       = ($wf -match 'Invoke-CiSmoke\.ps1')
    noTabs          = -not $wf.Contains("`t")
}
$wfOk = -not ($wfChecks.Values -contains $false)

$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $headers = @{ 'User-Agent' = 'nexus-verify-task14b'; 'Accept' = 'application/vnd.github+json' }
    $uri = "https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch=$([uri]::EscapeDataString($gitStart.Branch))&per_page=5"
    $runs = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    if ($run.Count -gt 0) {
        $ci = [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url; headSha = $run[0].head_sha }
    } else {
        $ci = [ordered]@{ checked = $true; status = 'no run for this HEAD yet'; conclusion = $null }
    }
} catch {
    $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable or repository private: $($_.Exception.Message)" }
}
$e5Pass = $wfOk
Write-Result 'E5' $e5Pass ("workflow checks failed=[{0}] | latest CI run for this HEAD: {1}" -f `
    (($wfChecks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','),
    $(if ($ci.checked) { "{0} / {1} {2}" -f $ci.status, $ci.conclusion, $ci.url } else { $ci.note }))
if (-not ($ci.checked -and $ci.conclusion -eq 'success')) {
    Write-Host '  Check the run in the GitHub Actions tab; E5 here judges the workflow file, the green run is confirmed on GitHub.' -ForegroundColor Yellow
}

# --------------------------------------------- E2 (part 2: real guard over this whole script) ---------------------------------------------
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$e2Pass = $guardLogicOk -and $gitEnd.Same
Write-Result 'E2' $e2Pass ("guard cases: {0} | this run: branch/HEAD unchanged and tree clean = {1} {2}" -f `
    (($guardCases.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join ', '), $gitEnd.Same, ($gitEnd.Reasons -join '; '))

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-14b'; gate = 'Gate 14b'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall
    gitStart = $gitStart
    E1_BuildAndRepoZeroBom = [ordered]@{ pass = $e1Pass; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; buildSkipped = [bool]$SkipBuild; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; requiredTracked = $requiredTracked }
    E2_GitImmutabilityGuard = [ordered]@{ pass = $e2Pass; guardCases = $guardCases; thisRunSame = $gitEnd.Same; reasons = $gitEnd.Reasons }
    E3_FastRegression = [ordered]@{ pass = $e3Pass; seconds = $fastSeconds; budgetSeconds = $FastBudgetSeconds; exitCode = $fastExit; summary = $fastSummary; noGeminiCall = $liveSkipped; relaxedSla = $relaxedUsed; slaWarnings = $slaWarnings }
    E4_CpuSettleAndMemory = [ordered]@{ pass = $e4Pass; cockpitIdleCpuPct = $cockpitCpu; headlessTimerIdleCpuPct = $headlessCpu; privateWorkingSetMb = $privateWs; softWarnMb = 54.0; hardLimitMb = 60.0; softWarning = ($privateWs -ge 54.0); cpuSettleMs = $e5.cpuSettleMs; cpuSamplesPct = $e5.cpuSamplesPct }
    E5_CiWorkflow = [ordered]@{ pass = $e5Pass; checks = $wfChecks; latestRun = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
Write-Host "`nGate 14b verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
