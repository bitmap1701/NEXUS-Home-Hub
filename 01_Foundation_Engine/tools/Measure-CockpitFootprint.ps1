#Requires -Version 5.1
<#
.SYNOPSIS
    Gate 16c run 2 diagnosis: where the Cockpit window's memory goes on this PC (not a Gate; does not use a Verify attempt).
.DESCRIPTION
    Runs the selftest "ui-footprint" 4 times, alternating the ADR-68 fonts (Segoe UI Variable + Cascadia Mono) and the classic
    fonts of v0.16.0 (Segoe UI + Consolas, NEXUS_UI_FONTS=classic). Each run is a separate HostService process that creates the real
    window off screen, shows it (Half, Full), renders the real Q&A history of this PC, a mock /ask! (is_test row, 0 token), two
    /calc lines, opens and closes the F1 and Alt+A boards, then hides it. Every phase is read after an aggressive GC.
    HostService is stopped through its own pipe (SHUTDOWN, as the Verify scripts do) and started again at the end if it was running.
    Writes 03_Local_Data_Vault/handoff/task16c_footprint_summary.json (UTF-8 without BOM).
#>
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)

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
    $runs = @()
    foreach ($variant in @('adr68', 'classic', 'adr68', 'classic')) {
        $argsList = @('--selftest', 'ui-footprint'); if ($variant -eq 'classic') { $argsList += 'classic' }
        $path = Join-Path $HandoffDir $(if ($variant -eq 'classic') { 'task16c_selftest_ui-footprint-classic.json' } else { 'task16c_selftest_ui-footprint.json' })
        if (Test-Path $path) { Remove-Item $path -Force }
        $p = Start-Process -FilePath $HostExe -ArgumentList $argsList -PassThru -Wait
        if (-not (Test-Path $path)) { throw "ui-footprint $variant wrote no report (exit $($p.ExitCode))" }
        $j = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $runs += [ordered]@{ variant = $variant; exitCode = $p.ExitCode; shown = $j.shown; showError = $j.showError; phases = $j.phases }
        Write-Host ("  {0,-8} hidden: Private WS {1} MB, GC heap {2} MB" -f $variant, ($j.phases | Select-Object -Last 1).privateWsMb, ($j.phases | Select-Object -Last 1).gcHeapMb)
    }
    $names = @($runs[0].phases | ForEach-Object { $_.phase })
    Write-Host ''
    Write-Host ('{0,-14} {1,22} {2,22}' -f 'phase', 'ADR-68 fonts (MB)', 'classic fonts (MB)') -ForegroundColor Cyan
    foreach ($n in $names) {
        $a = @($runs | Where-Object { $_.variant -eq 'adr68' } | ForEach-Object { ($_.phases | Where-Object { $_.phase -eq $n }).privateWsMb })
        $c = @($runs | Where-Object { $_.variant -eq 'classic' } | ForEach-Object { ($_.phases | Where-Object { $_.phase -eq $n }).privateWsMb })
        Write-Host ('{0,-14} {1,22} {2,22}' -f $n, ($a -join ' / '), ($c -join ' / '))
    }
    $summary = [ordered]@{ tool = 'Measure-CockpitFootprint'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
        branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD); head = (& git -C $RepoRoot rev-parse --short HEAD); runs = $runs }
    $out = Join-Path $HandoffDir 'task16c_footprint_summary.json'
    [System.IO.File]::WriteAllText($out, ($summary | ConvertTo-Json -Depth 8), $Utf8NoBom)
    Write-Host "`nSummary written to $out" -ForegroundColor Green
}
finally {
    if ($wasRunning -and -not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $HostExe | Out-Null
        Write-Host 'HostService started again.' -ForegroundColor Cyan
    }
}
