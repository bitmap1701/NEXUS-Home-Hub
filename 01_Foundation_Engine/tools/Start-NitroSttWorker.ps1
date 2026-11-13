<#
.SYNOPSIS
    TASK-18b (ADR-73): start the TCP 5055 STT worker on the Nitro 5 (or any PC on 192.168.1.x) in this window (Ctrl+C stops it).
.DESCRIPTION
    Runs "NEXUSHome.McpTools.exe --stt-worker serve" with a local whisper.cpp "whisper-cli". The PC (bitmappp) then uses it with
    nexus_config.json  "stt": { "provider": "auto", "lanEndpoint": "192.168.1.22:5055", "language": "vi-VN" }  + /reload.
    Contract (TASK-18a): one TCP connection = one 16 kHz / 16-bit / mono WAV (<= 2 MB) -> UTF-8 text, or "ERR: ..." (the PC then
    falls back to SAPI en-US). Only loopback and 192.168.1.x peers are served; one recognition at a time; temp WAVs are deleted.
    Nothing is installed and no firewall rule is changed: the inbound TCP 5055 rule is only checked (read-only).
.PARAMETER WhisperExe
    Path of whisper.cpp "whisper-cli.exe" (https://github.com/ggml-org/whisper.cpp releases).
.PARAMETER Model
    A multilingual ggml model. Default (Architect 18b.1): ggml-small-q5_1.bin next to whisper-cli.exe or in its "models" folder
    (not *.en.bin: those are English only).
.PARAMETER Bind
    <IPv4>:<port>. Default (TASK-20a): this machine's 192.168.1.x address on an Ethernet adapter that is Up, else on any adapter
    that is Up (Wi-Fi), port 5055 (0.0.0.0:5055 if none is found).
.PARAMETER Threads
    whisper-cli threads. Default (TASK-20a): the physical core count (Win32_Processor.NumberOfCores; 8 on the i7-11800H, where
    8 threads took 2,4 s and 4 threads 3,6 s for a 3 s phrase).
.PARAMETER TimeoutMs
    How long one whisper-cli run may take before the worker answers "ERR:" (1000-60000, default 12000).
.PARAMETER DryRun
    Print the chosen bind / threads / timeout as one JSON line and exit without starting the worker (Verify-Task20a E3).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Start-NitroSttWorker.ps1 -WhisperExe C:\whisper\whisper-cli.exe   (model: C:\whisper\ggml-small-q5_1.bin or C:\whisper\models\ggml-small-q5_1.bin)
#>
param(
    [Parameter(Mandatory = $true)][string]$WhisperExe,
    [string]$Model,
    [string]$Bind,
    [string]$Language = 'vi',
    [int]$Threads = 0,
    [int]$TimeoutMs = 12000,
    [string]$McpTools,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # review 2026-11-14: no CLIXML progress on stderr when Get-NetAdapter loads its module (-DryRun is read as JSON)
$TimeoutMs = [math]::Max(1000, [math]::Min(60000, $TimeoutMs))   # the worker clamps the same way
$DefaultModel = 'ggml-small-q5_1.bin'   # Architect 18b.1: small multilingual, 5-bit quantized (~180 MB)
if (-not $Model) {
    $whisperDir = Split-Path -Parent $WhisperExe
    $Model = @((Join-Path $whisperDir $DefaultModel), (Join-Path $whisperDir "models\$DefaultModel")) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Model) { $Model = Join-Path $whisperDir "models\$DefaultModel" }
}
$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $McpTools) {
    $McpTools = @('Release', 'Debug') | ForEach-Object { Join-Path $EngineRoot "NEXUSHome.McpTools\bin\$_\net8.0\NEXUSHome.McpTools.exe" } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($Threads -le 0) {
    $cores = (Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Measure-Object -Property NumberOfCores -Sum).Sum
    $Threads = if ($cores -gt 0) { [int]$cores } else { 4 }
}
$bindSource = 'given'
if (-not $Bind) {
    # TASK-20a: Ethernet (802.3) that is Up first, then any adapter that is Up (Wi-Fi), then any 192.168.1.x address
    $up = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' })
    $lanIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '192.168.1.*' })
    $wired = @($up | Where-Object { $_.PhysicalMediaType -eq '802.3' -or $_.Name -like 'Ethernet*' } | ForEach-Object { $_.ifIndex })
    $upIdx = @($up | ForEach-Object { $_.ifIndex })
    $pick = $lanIps | Where-Object { $wired -contains $_.InterfaceIndex } | Select-Object -First 1
    $bindSource = 'ethernet'
    if (-not $pick) { $pick = $lanIps | Where-Object { $upIdx -contains $_.InterfaceIndex } | Select-Object -First 1; $bindSource = 'up' }
    if (-not $pick) { $pick = $lanIps | Select-Object -First 1; $bindSource = 'any' }
    if ($pick) { $Bind = "$($pick.IPAddress):5055" } else { $Bind = '0.0.0.0:5055'; $bindSource = 'none' }
}
if ($DryRun) {
    [ordered]@{ bind = $Bind; bindSource = $bindSource; threads = $Threads; timeoutMs = $TimeoutMs } | ConvertTo-Json -Compress
    exit 0
}
if (-not $McpTools -or -not (Test-Path $McpTools)) {
    throw "NEXUSHome.McpTools.exe not found. Build it once: dotnet build `"$EngineRoot\NEXUSHome.McpTools`" -c Release"
}
if (-not (Test-Path $WhisperExe)) { Write-Host "whisper-cli not found at ${WhisperExe}: the worker will answer 'ERR:' (the PC falls back to SAPI)." -ForegroundColor Yellow }
if (-not (Test-Path $Model)) { Write-Host "Model not found at ${Model}: whisper-cli will fail (answer 'ERR:')." -ForegroundColor Yellow }
if ($Model -match '\.en\.bin$') { Write-Host "$Model is English-only: use a multilingual model for Vietnamese." -ForegroundColor Yellow }
$port = ($Bind -split ':')[-1]
$rule = Get-NetFirewallPortFilter -Protocol TCP -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq $port } | Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } | Select-Object -First 1
if ($rule) { Write-Host "Firewall: inbound TCP $port allowed by '$($rule.DisplayName)'." -ForegroundColor Green }
else { Write-Host "Firewall: no enabled inbound allow rule for TCP $port found (a normal, non-Administrator window may not be able to read the rules). Without one the PC cannot reach this worker (create it yourself as Administrator; this script changes nothing)." -ForegroundColor Yellow }

$template = "-m `"$Model`" -t $Threads -f {wav} -l {lang} -nt -np"
Write-Host "Starting ($bindSource, $Threads threads): $McpTools --stt-worker serve --bind $Bind --cli $WhisperExe --args '$template' --lang $Language --timeout-ms $TimeoutMs" -ForegroundColor Cyan
Write-Host 'Ctrl+C stops the worker.' -ForegroundColor Cyan
& $McpTools --stt-worker serve --bind $Bind --cli $WhisperExe --args $template --lang $Language --timeout-ms $TimeoutMs
exit $LASTEXITCODE
