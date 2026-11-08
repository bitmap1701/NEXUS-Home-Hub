#Requires -Version 5.1
<#
.SYNOPSIS
    ADR-56 Zero-BOM gate over every file tracked by Git (used by .github/workflows/nexus-ci.yml and the Verify scripts).
.DESCRIPTION
    *.ps1 must start with the UTF-8 BOM (Windows PowerShell 5.1); every other tracked file must not.
    Exit code 0 = pass, 1 = at least one offender.
#>
param([string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$result = Test-NexusBom $RepoRoot
foreach ($o in $result.Offenders) { Write-Host "BOM rule violated: $o" -ForegroundColor Red }
Write-Host ("Zero-BOM gate: {0} tracked files checked, {1} offender(s) -> {2}" -f $result.Checked, @($result.Offenders).Count,
    $(if ($result.Pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($result.Pass) { 'Green' } else { 'Red' })
if (-not $result.Pass) { exit 1 }
exit 0
