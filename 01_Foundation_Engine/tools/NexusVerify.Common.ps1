#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - helpers shared by the Verify scripts, Invoke-NexusRegression.ps1 and the CI workflow (TASK-14b).
.DESCRIPTION
    Dot-source this file:  . (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')
      Get-NexusGitState     branch, HEAD and `git status --porcelain` lines of the repository
      Compare-NexusGitState (TASK-14b hardening a) did the branch or HEAD move, is the tree still clean?
      Test-NexusBom         (ADR-56) repository-wide BOM rule over `git ls-files`:
                            *.ps1 MUST start with the UTF-8 BOM (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI and
                            breaks Vietnamese text); every other tracked file must NOT start with EF BB BF.
    ASCII only, so the file reads the same with or without a BOM.
#>

function Get-NexusGitState([string]$RepoRoot) {
    $branch = ''
    $head = ''
    $dirty = @()
    $error1 = $null
    try {
        $branch = ([string](& git -C $RepoRoot rev-parse --abbrev-ref HEAD)).Trim()
        $head = ([string](& git -C $RepoRoot rev-parse HEAD)).Trim()
        $dirty = @(& git -C $RepoRoot status --porcelain | ForEach-Object { [string]$_ } | Where-Object { $_.Trim().Length -gt 0 })
        if ($LASTEXITCODE -ne 0) { $error1 = "git exit code $LASTEXITCODE" }
    } catch {
        $error1 = $_.Exception.Message
    }
    return [pscustomobject]@{
        Branch        = $branch
        Head          = $head
        Dirty         = $dirty
        Error         = $error1
        CapturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
}

<#
    Pure comparison (no git call), so the guard itself can be checked with made-up states (Verify-Task14b E2).
    RequireClean: the current tree must have no `git status --porcelain` line.
#>
function Compare-NexusGitState($Initial, $Current, [switch]$RequireClean) {
    $branchSame = ($null -ne $Initial) -and ($null -ne $Current) -and ([string]$Initial.Branch -ne '') -and ([string]$Initial.Branch -eq [string]$Current.Branch)
    $headSame = ($null -ne $Initial) -and ($null -ne $Current) -and ([string]$Initial.Head -ne '') -and ([string]$Initial.Head -eq [string]$Current.Head)
    $clean = ($null -ne $Current) -and (@($Current.Dirty).Count -eq 0)
    $noError = ($null -ne $Current) -and ($null -eq $Current.Error) -and ($null -ne $Initial) -and ($null -eq $Initial.Error)
    $reasons = @()
    if (-not $noError) { $reasons += 'git could not be read' }
    if (-not $branchSame) { $reasons += ("branch moved: {0} -> {1}" -f $Initial.Branch, $Current.Branch) }
    if (-not $headSame) { $reasons += ("HEAD moved: {0} -> {1}" -f $Initial.Head, $Current.Head) }
    if ($RequireClean -and -not $clean) { $reasons += ("working tree not clean: {0}" -f (@($Current.Dirty) -join '; ')) }
    return [pscustomobject]@{
        Same       = ($reasons.Count -eq 0)
        BranchSame = $branchSame
        HeadSame   = $headSame
        Clean      = $clean
        Reasons    = $reasons
    }
}

function Test-NexusBom([string]$RepoRoot) {
    $files = @(& git -C $RepoRoot ls-files | ForEach-Object { [string]$_ } | Where-Object { $_.Trim().Length -gt 0 })
    $offenders = @()
    $checked = 0
    foreach ($rel in $files) {
        $path = Join-Path $RepoRoot $rel
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $checked++
        $hasBom = $false
        $stream = [System.IO.File]::OpenRead($path)
        try {
            $buffer = New-Object byte[] 3
            $read = $stream.Read($buffer, 0, 3)
            $hasBom = ($read -eq 3 -and $buffer[0] -eq 0xEF -and $buffer[1] -eq 0xBB -and $buffer[2] -eq 0xBF)
        } finally {
            $stream.Dispose()
        }
        $isPs1 = $rel.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)
        if ($isPs1 -and -not $hasBom) { $offenders += "$rel (PowerShell script without UTF-8 BOM)" }
        if (-not $isPs1 -and $hasBom) { $offenders += "$rel (UTF-8 BOM)" }
    }
    return [pscustomobject]@{
        Pass      = ($offenders.Count -eq 0) -and ($checked -gt 0)
        Checked   = $checked
        Offenders = $offenders
    }
}
