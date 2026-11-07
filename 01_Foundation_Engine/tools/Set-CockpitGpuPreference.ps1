<#
.SYNOPSIS
    [CẤP ĐỎ - Registry] Optional GpuPreference for the Cockpit (Gate 2 decision 2). Run manually only.

.DESCRIPTION
    Writes HKCU\Software\Microsoft\DirectX\UserGpuPreferences "<HostService.exe>" = "GpuPreference=1;"
    (Windows Graphics Settings -> "Power saving", i.e. Intel UHD 730). Per-user key, no admin rights.
    Only relevant when the Cockpit runs with NEXUS_COCKPIT_RENDER=Hardware. In the default SoftwareOnly mode
    WPF renders on the CPU and this setting has no effect.
    Never run by agents automatically: GEMINI.md classifies Registry changes as Red_CriticalSystem (HITL).

.EXAMPLE
    .\Set-CockpitGpuPreference.ps1            # asks for confirmation, then sets GpuPreference=1
    .\Set-CockpitGpuPreference.ps1 -Remove    # removes the entry (back to Windows default)
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'),
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
$exe = (Resolve-Path $ExePath).Path

$action = if ($Remove) { "REMOVE GpuPreference for $exe" } else { "SET GpuPreference=1 (Intel UHD 730) for $exe" }
Write-Host "[RED TIER] Registry change: $action" -ForegroundColor Red
if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Cancelled.'; exit 1 }

if ($Remove) {
    Remove-ItemProperty -Path $key -Name $exe -ErrorAction SilentlyContinue
} else {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name $exe -Value 'GpuPreference=1;'
}
Get-ItemProperty -Path $key -Name $exe -ErrorAction SilentlyContinue | Format-List
Write-Host 'Done. Restart NEXUSHome.HostService for the change to apply.' -ForegroundColor Green
