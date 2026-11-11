#Requires -Version 5.1
<#
.SYNOPSIS
    Gate 16c run 2 diagnosis: where the Cockpit window's memory goes on this PC (not a Gate; does not use a Verify attempt).
.DESCRIPTION
    Runs the selftest "ui-footprint" twice per variant (adr68 = as built, thread0 = no thread rows, classic = Segoe UI + Consolas;
    with -Baseline also old = the v0.16.0 window), alternating. Originally: 4 times, alternating the ADR-68 fonts (Segoe UI Variable + Cascadia Mono) and the classic
    fonts of v0.16.0 (Segoe UI + Consolas, NEXUS_UI_FONTS=classic). Each run is a separate HostService process that creates the real
    window off screen, shows it (Half, Full), renders the real Q&A history of this PC, a mock /ask! (is_test row, 0 token), two
    /calc lines, opens and closes the F1 and Alt+A boards, then hides it. Every phase is read after an aggressive GC.
    HostService is stopped through its own pipe (SHUTDOWN, as the Verify scripts do) and started again at the end if it was running.
    -Baseline: A/B against the old window instead. Builds origin/claude/task-16c-footprint-baseline (v0.16.0 + this selftest only)
    in a separate git worktree ($env:TEMP\NEXUS_footprint_baseline, outside the repository; never deleted by this script), runs its
    HostService with NEXUS_VAULT_PATH = this vault (same database, history, agents), alternating old / new x2.
    cockpit_ui.json and latest_dispatch.json are put back afterwards.
    Writes 03_Local_Data_Vault/handoff/task16c_footprint_summary.json (or task16c_footprint_baseline.json with -Baseline).
#>
param([switch]$SkipBuild, [switch]$Baseline)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$VaultRoot  = Join-Path $RepoRoot '03_Local_Data_Vault'
$UiPath     = Join-Path $VaultRoot 'config\cockpit_ui.json'
$LatestPath = Join-Path $HandoffDir 'latest_dispatch.json'
$uiBackup     = if (Test-Path $UiPath) { [System.IO.File]::ReadAllBytes($UiPath) } else { $null }
$latestBackup = if (Test-Path $LatestPath) { [System.IO.File]::ReadAllBytes($LatestPath) } else { $null }
$BaseDir    = Join-Path $env:TEMP 'NEXUS_footprint_baseline'
$BaseExe    = Join-Path $BaseDir '01_Foundation_Engine\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'

function Send-PipeLine([string]$line, [int]$timeoutMs = 3000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}

$wasRunning = [bool](Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)
if ($wasRunning) {
    Write-Host 'Stopping HostService through its pipe (SHUTDOWN)...' -ForegroundColor Cyan
    try { $null = Send-PipeLine '{"cmd":"SHUTDOWN"}' } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) { throw 'HostService did not exit after SHUTDOWN. Close it yourself, then re-run.' }
}

try {
    if (-not $SkipBuild) {
        $buildText = (& dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) { Write-Host $buildText; throw "Build failed (exit $LASTEXITCODE)" }
        Write-Host 'Build OK (Release, -warnaserror).' -ForegroundColor Green
    }
    if ($Baseline) {
        & git -C $RepoRoot fetch -q origin claude/task-16c-footprint-baseline
        if (-not (Test-Path (Join-Path $BaseDir '.git'))) {
            & git -C $RepoRoot worktree add --detach $BaseDir origin/claude/task-16c-footprint-baseline | Out-Host
        } else {
            & git -C $BaseDir checkout -q --detach origin/claude/task-16c-footprint-baseline
        }
        $bt = (& dotnet build (Join-Path $BaseDir '01_Foundation_Engine') -c Release -warnaserror -v minimal 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) { Write-Host $bt; throw "Baseline build failed (exit $LASTEXITCODE)" }
        Write-Host ("Baseline build OK: v0.16.0 window @ {0} in {1}" -f (& git -C $BaseDir rev-parse --short HEAD), $BaseDir) -ForegroundColor Green
    }
    $runs = @()
    # 'old' = v0.16.0 window (-Baseline); 'adr68' = this branch as is; 'classic' = Segoe UI + Consolas; 'thread0' = no thread rows
    $set = if ($Baseline) { @('old', 'adr68', 'thread0', 'classic') } else { @('adr68', 'thread0', 'classic') }
    $variants = $set + $set
    foreach ($variant in $variants) {
        $argsList = @('--selftest', 'ui-footprint'); if ($variant -ne 'old' -and $variant -ne 'adr68') { $argsList += $variant }
        $file = if ($variant -eq 'old' -or $variant -eq 'adr68') { 'task16c_selftest_ui-footprint.json' } else { "task16c_selftest_ui-footprint-$variant.json" }
        $path = Join-Path $HandoffDir $file
        if (Test-Path $path) { Remove-Item $path -Force }
        if ($variant -eq 'old') {
            $env:NEXUS_VAULT_PATH = $VaultRoot   # the old build reads and writes this vault, not the worktree's empty one
            try { $p = Start-Process -FilePath $BaseExe -ArgumentList $argsList -PassThru -Wait }
            finally { Remove-Item Env:NEXUS_VAULT_PATH -ErrorAction SilentlyContinue }
        } else {
            $p = Start-Process -FilePath $HostExe -ArgumentList $argsList -PassThru -Wait
        }
        if (-not (Test-Path $path)) { throw "ui-footprint $variant wrote no report (exit $($p.ExitCode))" }
        $j = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $runs += [ordered]@{ variant = $variant; exitCode = $p.ExitCode; shown = $j.shown; showError = $j.showError; trimInfo = $j.trimInfo; phases = $j.phases }
        Write-Host ("  {0,-8} hidden: Private WS {1} MB, GC heap {2} MB" -f $variant, ($j.phases | Select-Object -Last 1).privateWsMb, ($j.phases | Select-Object -Last 1).gcHeapMb)
    }
    $names = @($runs[0].phases | ForEach-Object { $_.phase })
    Write-Host ''
    Write-Host ('Private WS in MB, each cell = run 1 / run 2 (GC heap on the grey line)') -ForegroundColor Cyan
    $header = '{0,-14}' -f 'phase'
    foreach ($v in $set) { $header += ('{0,17}' -f $v) }
    Write-Host $header -ForegroundColor Cyan
    foreach ($n in $names) {
        $line = '{0,-14}' -f $n; $gline = '{0,-14}' -f '  (GC heap)'
        foreach ($v in $set) {
            $ph = @($runs | Where-Object { $_.variant -eq $v } | ForEach-Object { $_.phases | Where-Object { $_.phase -eq $n } })
            $line += ('{0,17}' -f (($ph | ForEach-Object { '{0:N1}' -f $_.privateWsMb }) -join ' / '))
            $gline += ('{0,17}' -f (($ph | ForEach-Object { '{0:N1}' -f $_.gcHeapMb }) -join ' / '))
        }
        Write-Host $line
        Write-Host $gline -ForegroundColor DarkGray
    }
    $summary = [ordered]@{ tool = 'Measure-CockpitFootprint'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
        branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD); head = (& git -C $RepoRoot rev-parse --short HEAD); runs = $runs }
    $out = Join-Path $HandoffDir $(if ($Baseline) { 'task16c_footprint_baseline.json' } else { 'task16c_footprint_summary.json' })
    [System.IO.File]::WriteAllText($out, ($summary | ConvertTo-Json -Depth 8), $Utf8NoBom)
    Write-Host "`nSummary written to $out" -ForegroundColor Green
}
finally {
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $latestBackup) { [System.IO.File]::WriteAllBytes($LatestPath, $latestBackup) }
    if ($wasRunning -and -not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $HostExe | Out-Null
        Write-Host 'HostService started again.' -ForegroundColor Cyan
    }
}
