#Requires -Version 5.1
<#
.SYNOPSIS
    Gate 16c diagnosis (not a Gate, no Verify attempt): replays the steps that lead to Verify-Task16c E3 one by one, each pipe
    reply with a 10 s limit, and prints how long each step took. A step that times out is where the HostService UI thread hung.
.DESCRIPTION
    Same preparation as Verify-Task16c E2 (HostService restarted with the Gemini mock, collapsed -> wake, half, collapsed, half,
    HISTORY refresh, /calc), then the E3 steps in the same order. Stops at the first time-out and says which step it was.
    cockpit_ui.json, latest_dispatch.json and the clipboard text are put back. HostService is stopped through its pipe
    (SHUTDOWN) at the start; if it is hung at the end, the script says so and leaves it for Nhat Hoa to close.
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$UiPath     = Join-Path $RepoRoot '03_Local_Data_Vault\config\cockpit_ui.json'
$LatestPath = Join-Path $HandoffDir 'latest_dispatch.json'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$StepTimeoutMs = 10000

$uiBackup = if (Test-Path $UiPath) { [System.IO.File]::ReadAllBytes($UiPath) } else { $null }
$latestBackup = if (Test-Path $LatestPath) { [System.IO.File]::ReadAllBytes($LatestPath) } else { $null }
$clipBackup = $null; try { $clipBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }

function Send-Pipe([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect(5000)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine(($req | ConvertTo-Json -Compress))
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($StepTimeoutMs)) { throw "TIMEOUT" }
        return $task.Result
    } finally { $client.Dispose() }
}
$script:Steps = @()
function Step([string]$name, [string]$cmd, [string]$text) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Send-Pipe $cmd $text
        $ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        $short = if ($r) { $r.Substring(0, [math]::Min(90, $r.Length)) } else { '(null reply)' }
        Write-Host ('{0,-34} {1,8} ms  {2}' -f $name, $ms, $short) -ForegroundColor $(if ($r) { 'Gray' } else { 'Yellow' })
        $script:Steps += [ordered]@{ step = $name; ms = $ms; ok = [bool]$r }
        if (-not $r) { throw "NULLREPLY" }
        return $r
    } catch {
        $ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        Write-Host ('{0,-34} {1,8} ms  >>> {2} <<<' -f $name, $ms, $_.Exception.Message) -ForegroundColor Red
        $script:Steps += [ordered]@{ step = $name; ms = $ms; ok = $false; error = $_.Exception.Message }
        throw "STOP at step '$name': $($_.Exception.Message)"
    }
}
function Stop-Host {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { $null = Send-Pipe 'SHUTDOWN' $null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) { throw 'HostService did not exit after SHUTDOWN. Close it in Task Manager, then re-run.' }
}
function Start-HostMock {
    $env:NEXUS_GEMINI_MOCK_RESPONSE = 'Gate 16c probe mock'
    try { Start-Process -FilePath $HostExe | Out-Null } finally { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE -ErrorAction SilentlyContinue }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) { try { $null = Send-Pipe 'SYNCSTATE' $null; break } catch { Start-Sleep -Milliseconds 300 } }
    Start-Sleep -Seconds 3
}
function Set-ClipboardTestImage {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 96, 48
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try { $g.Clear([System.Drawing.Color]::FromArgb(255, 11, 18, 32)); $g.DrawString('ERR 16c', (New-Object System.Drawing.Font('Consolas', 10)), [System.Drawing.Brushes]::OrangeRed, 4, 14) } finally { $g.Dispose() }
        $do = New-Object System.Windows.Forms.DataObject
        $do.SetImage($bmp)
        $do.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-Object System.IO.MemoryStream(, [byte[]](0, 0, 0, 0))))
        [System.Windows.Forms.Clipboard]::SetDataObject($do, $true)
    } finally { $bmp.Dispose() }
}

$stopAt = $null
try {
    Stop-Host
    Start-HostMock
    Write-Host "HostService started (mock). Replaying E2 preparation + E3, limit $StepTimeoutMs ms per step." -ForegroundColor Cyan
    $null = Step 'UI collapsed' 'UI' 'collapsed'
    $null = Step 'PASSIVE' 'PASSIVE' $null
    Start-Sleep -Milliseconds 350
    $null = Step 'COMMANDMODE (cold wake)' 'COMMANDMODE' $null
    Start-Sleep -Milliseconds 300
    $null = Step 'PASSIVE' 'PASSIVE' $null
    $null = Step 'UI half' 'UI' 'half'
    $null = Step 'UI collapsed' 'UI' 'collapsed'
    Start-Sleep -Milliseconds 400
    $null = Step 'UI half' 'UI' 'half'
    $null = Step 'HISTORY refresh' 'HISTORY' 'refresh'
    $null = Step 'COCKPIT /calc' 'COCKPIT' '/calc 12*12+0'
    $null = Step 'UISTATE' 'UISTATE' $null
    # E3
    $null = Step 'PALETTE /' 'PALETTE' '/'
    $null = Step 'PALETTE /ca' 'PALETTE' '/ca'
    $null = Step 'PALETTE key:tab' 'PALETTE' 'key:tab'
    $null = Step 'PALETTE /zzzq' 'PALETTE' '/zzzq'
    $null = Step 'PALETTE key:esc' 'PALETTE' 'key:esc'
    $null = Step 'PALETTE (clear)' 'PALETTE' $null
    $null = Step 'PALETTE @sec' 'PALETTE' '@sec'
    $null = Step 'PALETTE key:down' 'PALETTE' 'key:down'
    $null = Step 'PALETTE key:tab' 'PALETTE' 'key:tab'
    $null = Step 'PALETTE (clear)' 'PALETTE' $null
    $null = Step 'LAZY lazy' 'LAZY' 'lazy'
    $null = Step 'UISTATE' 'UISTATE' $null
    $null = Step 'LAZY altenter' 'LAZY' 'altenter'
    $null = Step 'UISTATE' 'UISTATE' $null
    Set-ClipboardTestImage
    Write-Host 'clipboard: test image set by this PowerShell' -ForegroundColor DarkGray
    $null = Step 'PASSIVE' 'PASSIVE' $null
    $null = Step 'COMMANDMODE (image probe)' 'COMMANDMODE' $null
    Start-Sleep -Milliseconds 800
    $null = Step 'UISTATE (image card?)' 'UISTATE' $null
    $null = Step 'LAZY enter (/img)' 'LAZY' 'enter'
    Start-Sleep -Milliseconds 2500
    $null = Step 'UISTATE' 'UISTATE' $null
    $null = Step 'PASSIVE' 'PASSIVE' $null
    $null = Step 'LAZY f1 (open)' 'LAZY' 'f1'
    $null = Step 'UISTATE' 'UISTATE' $null
    $null = Step 'LAZY f1 (close)' 'LAZY' 'f1'
    $null = Step 'ALLIANCE toggle (open)' 'ALLIANCE' 'toggle'
    $null = Step 'LAZY altg' 'LAZY' 'altg'
    $null = Step 'UISTATE' 'UISTATE' $null
    $null = Step 'LAZY altk' 'LAZY' 'altk'
    $null = Step 'UISTATE' 'UISTATE' $null
    $null = Step 'ALLIANCE toggle (close)' 'ALLIANCE' 'toggle'
    Write-Host "`nAll steps answered: no hang in this replay." -ForegroundColor Green
}
catch {
    $stopAt = $_.Exception.Message
    Write-Host "`n$stopAt" -ForegroundColor Red
}
finally {
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $latestBackup) { [System.IO.File]::WriteAllBytes($LatestPath, $latestBackup) }
    if ($null -ne $clipBackup -and $clipBackup.Length -gt 0) { try { Set-Clipboard -Value $clipBackup } catch { } }
    $p = Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { Write-Host ("HostService pid {0}: Responding={1}" -f $p.Id, $p.Responding) -ForegroundColor Cyan }
}
