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
    A multilingual ggml model, e.g. ggml-base.bin or ggml-small.bin (not *.en.bin: those are English only).
.PARAMETER Bind
    <IPv4>:<port>. Default: this machine's 192.168.1.x address, port 5055 (0.0.0.0:5055 if none is found).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Start-NitroSttWorker.ps1 -WhisperExe C:\whisper\whisper-cli.exe -Model C:\whisper\ggml-small.bin
#>
param(
    [Parameter(Mandatory = $true)][string]$WhisperExe,
    [Parameter(Mandatory = $true)][string]$Model,
    [string]$Bind,
    [string]$Language = 'vi',
    [int]$Threads = 4,
    [string]$McpTools
)
$ErrorActionPreference = 'Stop'
$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $McpTools) {
    $McpTools = @('Release', 'Debug') | ForEach-Object { Join-Path $EngineRoot "NEXUSHome.McpTools\bin\$_\net8.0\NEXUSHome.McpTools.exe" } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $McpTools -or -not (Test-Path $McpTools)) {
    throw "NEXUSHome.McpTools.exe not found. Build it once: dotnet build `"$EngineRoot\NEXUSHome.McpTools`" -c Release"
}
if (-not (Test-Path $WhisperExe)) { Write-Host "whisper-cli not found at ${WhisperExe}: the worker will answer 'ERR:' (the PC falls back to SAPI)." -ForegroundColor Yellow }
if (-not (Test-Path $Model)) { Write-Host "Model not found at ${Model}: whisper-cli will fail (answer 'ERR:')." -ForegroundColor Yellow }
if ($Model -match '\.en\.bin$') { Write-Host "$Model is English-only: use a multilingual model for Vietnamese." -ForegroundColor Yellow }
if (-not $Bind) {
    $lan = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like '192.168.1.*' } | Select-Object -First 1
    $Bind = if ($lan) { "$($lan.IPAddress):5055" } else { '0.0.0.0:5055' }
}
$port = ($Bind -split ':')[-1]
$rule = Get-NetFirewallPortFilter -Protocol TCP -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq $port } | Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' } | Select-Object -First 1
if ($rule) { Write-Host "Firewall: inbound TCP $port allowed by '$($rule.DisplayName)'." -ForegroundColor Green }
else { Write-Host "Firewall: no enabled inbound allow rule for TCP $port found. The PC cannot reach this worker until one exists (create it yourself as Administrator; this script changes nothing)." -ForegroundColor Yellow }

$template = "-m `"$Model`" -t $Threads -f {wav} -l {lang} -nt -np"
Write-Host "Starting: $McpTools --stt-worker serve --bind $Bind --cli $WhisperExe --args '$template' --lang $Language" -ForegroundColor Cyan
Write-Host 'Ctrl+C stops the worker.' -ForegroundColor Cyan
& $McpTools --stt-worker serve --bind $Bind --cli $WhisperExe --args $template --lang $Language
exit $LASTEXITCODE
