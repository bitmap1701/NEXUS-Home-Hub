<#
.SYNOPSIS
    TASK-06 / Gate 6 verification (E1-E5) for NEXUS Home Hub - nexus_config.json, ADR-19 state machine, shared core,
    dual-profile dispatch SLA and the merge of the task branches into main.

.DESCRIPTION
    E1  dotnet build -c Release -warnaserror (0/0) + no UTF-8 BOM in GEMINI.md, current_task.md, nexus_config.json,
        latest_dispatch.json
    E2  nexus_config.json: auto-create / reload / broken JSON / empty fields (--selftest config, isolated temp folder),
        then LIVE on the running HostService through the Cockpit pipe: edit memoryTrimFloorMb + defaultNexusMode,
        "/reload" applies them, "/config" shows them; broken JSON + "/reload" -> no crash, last valid config kept,
        standard file rewritten. The real config file is restored byte for byte afterwards.
    E3  ADR-19 (--selftest adr19): /fail x3 -> ESCALATED r3 + 1 escalation line; 4th /fail -> r4, still ESCALATED,
        still 1 line; "/pass Đã khắc phục xong" -> PASSED r4; reopen after PASSED. qa_report.md is restored afterwards
        (-KeepQaReportEntry keeps the line).
    E4  Shared core: both projects reference NEXUSHome.Core and build through NexusRuntime, no core type is defined twice;
        HostService (--selftest sla), the running Cockpit (/config) and McpTools (get_nexus_config) report the same config
        fingerprint and the same SLA profile (Desktop 10 ms / LiveCombat 25 ms), matching what this script detects.
    E5  Git: task-02..05 are in origin/main and this branch is main or a fast-forward of it; git status was clean at
        start. Full regression: Verify-Task05 -SkipBuild (runs Verify-Task04, which runs Verify-Task03) all PASS,
        private working set < 60 MB, idle CPU < 1 %.
    Results: 03_Local_Data_Vault/handoff/task06_verification.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Verify-Task06.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [switch]$SkipBuild,
    [switch]$SkipRegression,
    [switch]$KeepQaReportEntry
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = $utf8
[Console]::OutputEncoding = $utf8

$engine     = Join-Path $Root '01_Foundation_Engine'
$sln        = Join-Path $engine 'NEXUSHomeHub.slnx'
$mcpExe     = Join-Path $engine 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$hostExe    = Join-Path $engine 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$vault      = Join-Path $Root '03_Local_Data_Vault'
$handoff    = Join-Path $vault 'handoff'
$configPath = Join-Path $vault 'config\nexus_config.json'
$qaReport   = Join-Path $handoff 'qa_report.md'
$pipeName   = 'NEXUS_Home_Cockpit_Pipe'

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

function Invoke-Pipe([string]$cmd) {
    $resp = Send-PipeLine (([ordered]@{ cmd = $cmd }) | ConvertTo-Json -Compress) | ConvertFrom-Json
    return [string]$resp.result
}

# Cockpit command through the running HostService -> {kind, ok, message, ...}
function Invoke-Cockpit([string]$text) {
    $resp = Send-PipeLine (([ordered]@{ cmd = 'COCKPIT'; text = $text }) | ConvertTo-Json -Compress) | ConvertFrom-Json
    return ([string]$resp.result | ConvertFrom-Json)
}

function Invoke-SelfTest([string]$scenario, [string]$prefix = 'task06') {
    $out = Join-Path $handoff "${prefix}_selftest_$scenario.json"
    if (Test-Path $out) { Remove-Item $out }
    $p = Start-Process -FilePath $hostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $out)) { return [pscustomobject]@{ pass = $false; error = "no report (exit code $($p.ExitCode))" } }
    return (Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Invoke-Mcp([string[]]$calls) {
    $requests = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}') + $calls
    $lines = (($requests -join "`n") + "`n") | & $mcpExe
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
        try { $null = Invoke-Pipe 'SYNCSTATE'; break } catch { Start-Sleep -Milliseconds 300 }
    }
    return [pscustomobject]@{ Process = (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1); StartedHere = $started }
}

function Test-NoBom([string]$path) {
    if (-not (Test-Path $path)) { return 'missing' }
    $b = [System.IO.File]::ReadAllBytes($path)
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return 'BOM' }
    return 'ok'
}

Write-Host "NEXUS Home Hub - TASK-06 verification (root: $Root)" -ForegroundColor Cyan

# git status before anything writes (E5)
$gitStatusAtStart = @(& git -C $Root status --porcelain)
$configExistedBefore = Test-Path $configPath

# ---------------- E1 (part 1): build ----------------
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
$slaFirst = Invoke-SelfTest 'sla'   # first process start: creates config\nexus_config.json if it is missing

# ---------------- E2: config ----------------
$cfg = Invoke-SelfTest 'config'
$hostInfo = Start-HostIfNeeded
$live = [ordered]@{ ran = $false }
if ($hostInfo.Process -and (Test-Path $configPath)) {
    $original = [System.IO.File]::ReadAllBytes($configPath)
    try {
        $o = [System.Text.Encoding]::UTF8.GetString($original).TrimStart([char]0xFEFF) | ConvertFrom-Json
        $newFloor = if ($o.memoryTrimFloorMb -eq 64) { 72 } else { 64 }
        $newMode = if ($o.defaultNexusMode -eq 'Sprint') { 'Full' } else { 'Sprint' }
        $o.memoryTrimFloorMb = $newFloor
        $o.defaultNexusMode = $newMode
        [System.IO.File]::WriteAllText($configPath, ($o | ConvertTo-Json -Depth 5), $utf8)

        $r1 = Invoke-Cockpit '/reload'
        $c1 = Invoke-Cockpit '/config'
        $applied = $r1.ok -and ($r1.message -match "memoryTrimFloorMb=$newFloor") -and ($r1.message -match "defaultNexusMode=$newMode") -and
                   ($c1.message -match "memoryTrimFloorMb=$newFloor") -and ($c1.message -match "defaultNexusMode=$newMode")

        $beforeBackups = @(Get-ChildItem -Path (Split-Path $configPath) -Filter 'nexus_config.json.invalid-*' -ErrorAction SilentlyContinue).Count
        [System.IO.File]::WriteAllText($configPath, '{ "memoryTrimFloorMb": 99, "defaultNexusMode": ', $utf8)
        $r2 = Invoke-Cockpit '/reload'
        $alive = $false; try { $null = Invoke-Pipe 'SYNCSTATE'; $alive = $true } catch { }
        $c2 = Invoke-Cockpit '/config'
        $restoredFile = $null; try { $restoredFile = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
        $afterBackups = @(Get-ChildItem -Path (Split-Path $configPath) -Filter 'nexus_config.json.invalid-*' -ErrorAction SilentlyContinue).Count
        $brokenHandled = (-not $r2.ok) -and $alive -and ($c2.message -match "memoryTrimFloorMb=$newFloor") -and
                         ($null -ne $restoredFile) -and ($restoredFile.memoryTrimFloorMb -eq $newFloor) -and ($afterBackups -gt $beforeBackups)

        $live = [ordered]@{
            ran = $true; newFloor = $newFloor; newMode = $newMode
            applied = $applied; reloadMessage = $r1.message
            brokenHandled = $brokenHandled; brokenReloadOk = [bool]$r2.ok; brokenReloadMessage = $r2.message; hostAlive = $alive
            restoredFileFloor = $(if ($restoredFile) { $restoredFile.memoryTrimFloorMb } else { $null }); brokenCopies = $afterBackups - $beforeBackups
        }
    } finally {
        [System.IO.File]::WriteAllBytes($configPath, $original)
        try { $r3 = Invoke-Cockpit '/reload'; $live['restoredReloadOk'] = [bool]$r3.ok } catch { $live['restoredReloadOk'] = $false }
    }
}
$e2Pass = [bool]$cfg.pass -and $live.ran -and $live.applied -and $live.brokenHandled -and $live.restoredReloadOk
Set-Result 'E2' $e2Pass ("isolated: create={0} (noBom={1}) reload 50->64/Sprint={2} brokenJson={3} (kept last valid={4}, backup={5}, file rewritten floor={6}) emptyFields={7} bomFile={8} hotkeys={9} | LIVE Cockpit: /reload applied floor={10} mode={11}: {12}; broken JSON -> ok={13} host alive={14} kept={15} file restored floor={16} copies+{17}; original config restored + reload={18} | config existed before run={19}" -f `
    $cfg.create.ok, $cfg.create.noBom, $cfg.reload.ok, $cfg.brokenJson.ok, $cfg.brokenJson.keptLastValid, $cfg.brokenJson.backupOk, $cfg.brokenJson.restoredFileMemoryTrimFloorMb,
    $cfg.emptyFields.ok, $cfg.bomFile.ok, (@($cfg.hotkeys.PSObject.Properties | Where-Object { $_.Value }).Count.ToString() + '/5'),
    $live.newFloor, $live.newMode, $live.applied, $live.brokenReloadOk, $live.hostAlive, $live.brokenHandled, $live.restoredFileFloor, $live.brokenCopies,
    $live.restoredReloadOk, $configExistedBefore)

# ---------------- E3: ADR-19 ----------------
$qaBackup = if (Test-Path $qaReport) { [System.IO.File]::ReadAllBytes($qaReport) } else { $null }
$e3 = Invoke-SelfTest 'adr19'
if (-not $KeepQaReportEntry -and $null -ne $qaBackup) {
    [System.IO.File]::WriteAllBytes($qaReport, $qaBackup)
    $qaNote = 'qa_report.md restored'
} else { $qaNote = 'qa_report.md entry kept' }
$stepText = (@($e3.steps) | ForEach-Object { "{0}:{1}/r{2}/lines={3}" -f $_.step, $_.status, $_.retryCount, $_.escalationLines }) -join ' | '
Set-Result 'E3' ([bool]$e3.pass) ("dispatch #{0}: {1} | checks {2} | second dispatch #{3} PASSED->/fail -> {4} r{5} | {6}" -f `
    $e3.dispatchId, $stepText, ($e3.checks | ConvertTo-Json -Compress), $e3.secondDispatch.dispatchId, $e3.secondDispatch.status, $e3.secondDispatch.retryCount, $qaNote)

# ---------------- E1 (part 2): BOM ----------------
$bom = [ordered]@{
    'GEMINI.md'            = Test-NoBom (Join-Path $Root 'GEMINI.md')
    'current_task.md'      = Test-NoBom (Join-Path $handoff 'current_task.md')
    'nexus_config.json'    = Test-NoBom $configPath
    'latest_dispatch.json' = Test-NoBom (Join-Path $handoff 'latest_dispatch.json')
}
$bomOk = -not ($bom.Values | Where-Object { $_ -ne 'ok' })
Set-Result 'E1' ($buildOk -and $bomOk) ("{0} | UTF-8 BOM check: {1}" -f $buildDetail, (($bom.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

# ---------------- E4: shared core + ADR-17 ----------------
$coreTypes = 'SqliteMemoryStore|SquadDispatcher|GameBoost|AgentRegistry|NexusPaths|NexusConfigService|NexusConfig|NexusRuntime|DispatchVerdictService|LatestDispatchFile|CockpitCommandProcessor'
$dupes = @()
foreach ($proj in 'NEXUSHome.HostService', 'NEXUSHome.McpTools', 'NEXUSHome.CockpitUI') {
    Get-ChildItem (Join-Path $engine $proj) -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
        $m = Select-String -Path $_.FullName -Pattern "\b(class|record|struct)\s+($coreTypes)\b"
        if ($m) { $dupes += "$($proj)/$($_.Name): $($m.Matches[0].Value)" }
    }
}
$refsOk = $true; $bootOk = $true
foreach ($proj in 'NEXUSHome.HostService', 'NEXUSHome.McpTools') {
    $csproj = Get-Content (Join-Path $engine "$proj\$proj.csproj") -Raw
    if ($csproj -notmatch 'NEXUSHome\.Core\.csproj') { $refsOk = $false }
    $prog = Get-Content (Join-Path $engine "$proj\Program.cs") -Raw
    if ($prog -notmatch 'NexusRuntime\.Create\(' -or $prog -match 'new SqliteMemoryStore\(|new SquadDispatcher\(|new DispatchVerdictService\(') { $bootOk = $false }
}
$slaHost = Invoke-SelfTest 'sla'
$cockpitConfig = $null; try { $cockpitConfig = Invoke-Cockpit '/config' } catch { }
$cockpitFp = if ($cockpitConfig -and $cockpitConfig.message -match 'fingerprint ([0-9a-f]{12})') { $Matches[1] } else { $null }
$mcpCall = ([ordered]@{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = [ordered]@{ name = 'get_nexus_config'; arguments = @{} } } | ConvertTo-Json -Compress -Depth 5)
$mcp = $null; try { $m = Invoke-Mcp @($mcpCall); $mcp = [string]$m[2].result.content[0].text | ConvertFrom-Json } catch { }
# What this script sees (ADR-17 rule, from the same config file)
$cfgNow = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$combatPrefixes = if ($cfgNow.combatProcessPrefixes) { @($cfgNow.combatProcessPrefixes) } else { @('DeltaForce', 'TslGame', 'SGuard') }
$combatRunning = @(Get-Process | Where-Object { $n = $_.ProcessName; @($combatPrefixes | Where-Object { $n.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 } | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
$scriptProfile = if ($combatRunning.Count) { 'LiveCombat' } else { 'Desktop' }
$scriptBudget = if ($combatRunning.Count) { $cfgNow.dispatchSlaCombatMs } else { $cfgNow.dispatchSlaDesktopMs }
$sameFp = $slaHost.fingerprint -and ($slaHost.fingerprint -eq $mcp.fingerprint) -and ($slaHost.fingerprint -eq $cockpitFp)
$sameSla = ($slaHost.sla.profile -eq $mcp.sla.profile) -and ($slaHost.sla.profile -eq $scriptProfile) -and
           ([double]$slaHost.sla.budgetMs -eq [double]$mcp.sla.budgetMs) -and ([double]$slaHost.sla.budgetMs -eq [double]$scriptBudget)
$e4Pass = [bool]$slaHost.pass -and $refsOk -and $bootOk -and $dupes.Count -eq 0 -and $sameFp -and $sameSla
Set-Result 'E4' $e4Pass ("Core refs ok={0}, both Program.cs boot via NexusRuntime={1}, core types defined outside Core=[{2}] | config fingerprint HostService={3} Cockpit(live)={4} McpTools={5} | SLA HostService={6}/{7}ms McpTools={8}/{9}ms script={10}/{11}ms (combat processes: [{12}])" -f `
    $refsOk, $bootOk, ($dupes -join '; '), $slaHost.fingerprint, $cockpitFp, $mcp.fingerprint,
    $slaHost.sla.profile, $slaHost.sla.budgetMs, $mcp.sla.profile, $mcp.sla.budgetMs, $scriptProfile, $scriptBudget, ($combatRunning -join ','))

# ---------------- E5: git + regression ----------------
& git -C $Root fetch origin --quiet
$inMain = [ordered]@{}
foreach ($b in 'claude/task-02-monitor-routing', 'claude/task-03-agent-squad-dispatch', 'claude/task-04-cockpit-dispatch', 'claude/task-05-dev-qa-loop') {
    & git -C $Root merge-base --is-ancestor "origin/$b" origin/main
    $inMain[$b] = ($LASTEXITCODE -eq 0)
}
& git -C $Root merge-base --is-ancestor origin/main HEAD; $mainInHead = ($LASTEXITCODE -eq 0)
& git -C $Root merge-base --is-ancestor HEAD origin/main; $headInMain = ($LASTEXITCODE -eq 0)
$headName = (& git -C $Root rev-parse --abbrev-ref HEAD)
$gitOk = -not ($inMain.Values | Where-Object { -not $_ }) -and ($headInMain -or $mainInHead) -and $gitStatusAtStart.Count -eq 0
$mergeNote = if ($headInMain) { 'HEAD already in origin/main' } elseif ($mainInHead) { 'HEAD = origin/main + new commits (fast-forward ready)' } else { 'HEAD diverged from origin/main' }

$regOk = $true; $regDetail = 'regression skipped (-SkipRegression)'
if (-not $SkipRegression) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task05.ps1') -Root $Root -SkipBuild | Out-Host
    $t5 = Join-Path $handoff 'task05_verification.json'
    $t4 = Join-Path $handoff 'task04_verification.json'
    $t3 = Join-Path $handoff 'task03_verification.json'
    $flag = { param($p) (Test-Path $p) -and [bool]((Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).allPassed) }
    $t5Pass = & $flag $t5; $t4Pass = & $flag $t4; $t3Pass = & $flag $t3
    $regOk = $t5Pass -and $t4Pass -and $t3Pass
    $e7 = if (Test-Path $t5) { (Get-Content $t5 -Raw -Encoding UTF8 | ConvertFrom-Json).results.E7.detail } else { '' }
    $regDetail = "Verify-Task05 allPassed=$t5Pass, Verify-Task04 allPassed=$t4Pass, Verify-Task03 allPassed=$t3Pass | Task05 E7: $e7"
}
Set-Result 'E5' ($gitOk -and $regOk) ("git: {0} in origin/main = [{1}] | branch {2}: {3} | git status at start: {4} | {5}" -f `
    ($inMain.Keys.Count), (($inMain.GetEnumerator() | ForEach-Object { "$($_.Key.Replace('claude/', ''))=$($_.Value)" }) -join ', '),
    $headName, $mergeNote, $(if ($gitStatusAtStart.Count) { 'NOT clean: ' + ($gitStatusAtStart -join '; ') } else { 'clean' }), $regDetail)

# ---------------- Report ----------------
$report = [ordered]@{
    task = 'TASK-06'
    timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    machine = $env:COMPUTERNAME
    root = $Root
    results = $results
    e2Live = $live
    e4 = [ordered]@{ duplicates = $dupes; hostFingerprint = $slaHost.fingerprint; cockpitFingerprint = $cockpitFp; mcpFingerprint = $mcp.fingerprint; scriptProfile = $scriptProfile; combatProcesses = $combatRunning }
    e5 = [ordered]@{ branchesInMain = $inMain; head = $headName; headInMain = $headInMain; mainInHead = $mainInHead; gitStatusAtStart = $gitStatusAtStart }
    qaReportEntryKept = [bool]$KeepQaReportEntry
    hostServiceStartedHere = $hostInfo.StartedHere
    allPassed = -not ($results.Values | Where-Object { -not $_.pass })
}
$out = Join-Path $handoff 'task06_verification.json'
[System.IO.File]::WriteAllText($out, ($report | ConvertTo-Json -Depth 6), $utf8)
Write-Host "`nReport: $out" -ForegroundColor Cyan
if ($report.allPassed) { Write-Host 'ALL CHECKS PASS (E1-E5)' -ForegroundColor Green } else { Write-Host 'SOME CHECKS FAILED' -ForegroundColor Red; exit 1 }
