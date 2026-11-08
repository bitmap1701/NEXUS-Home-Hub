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

<#
    TASK-13a (ADR-55 drift tracking): every [SLA-WARN (FastRegression jitter)] recorded since $SinceUtc, read from the
    selftest reports (slaWarnings) and the Verify reports (slaWarnings) in the handoff folder.
#>
function Get-NexusSlaWarnings([string]$HandoffDir, [DateTime]$SinceUtc) {
    $list = @()
    $files = @(Get-ChildItem -Path $HandoffDir -Filter 'task*_*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $SinceUtc -and ($_.Name -like '*_selftest_*' -or $_.Name -like '*_verification.json') })
    foreach ($f in $files) {
        try { $j = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
        foreach ($w in @($j.slaWarnings)) {
            if ($null -eq $w) { continue }
            $list += [pscustomobject]@{
                Source     = $f.BaseName
                Metric     = [string]$w.metric
                MeasuredMs = $w.measuredMs
                SlaMs      = $w.slaMs
                CeilingMs  = $w.ceilingMs
            }
        }
    }
    return $list
}

<# Prints the standard summary line; returns $true when [DRIFT-ALERT] (more than 2 warnings in one run) was raised. #>
function Write-NexusSlaSummary($Warnings) {
    $items = @($Warnings)
    $details = if ($items.Count -eq 0) { 'NONE' } else {
        ($items | ForEach-Object { "{0} {1}={2}ms(SLA {3})" -f $_.Source, $_.Metric, $_.MeasuredMs, $_.SlaMs }) -join '; '
    }
    Write-Host ("[SLA-WARN SUMMARY] Count={0} | Details={1}" -f $items.Count, $details) -ForegroundColor $(if ($items.Count -eq 0) { 'Green' } else { 'Yellow' })
    if ($items.Count -gt 2) {
        Write-Host '[DRIFT-ALERT] Multiple SLA warnings detected - inspect before FullRegression' -ForegroundColor Red
        return $true
    }
    return $false
}

<#
    Appends this fast run to handoff/regression_sla_history.json (git-ignored, last 30 runs) and returns the metrics that
    were warned in each of the last 3 runs coming from 3 different branches (one branch = one Gate): [DRIFT-ALERT].
#>
function Update-NexusSlaHistory([string]$HandoffDir, [string]$Branch, [string]$Head, $Warnings) {
    $path = Join-Path $HandoffDir 'regression_sla_history.json'
    $history = @()
    if (Test-Path $path) {
        # Windows PowerShell 5.1 returns a JSON array as one object: pipe it through ForEach-Object to get the elements.
        try { $parsed = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json; $history = @($parsed | ForEach-Object { $_ }) } catch { $history = @() }
    }
    $metrics = @(@($Warnings) | ForEach-Object { $_.Metric } | Sort-Object -Unique)
    $history += [pscustomobject]@{ timestampUtc = (Get-Date).ToUniversalTime().ToString('o'); branch = $Branch; head = $Head; count = @($Warnings).Count; metrics = $metrics }
    if ($history.Count -gt 30) { $history = $history[($history.Count - 30)..($history.Count - 1)] }
    [System.IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject @($history) -Depth 5), (New-Object System.Text.UTF8Encoding($false)))

    # Last run of each of the 3 most recent distinct branches.
    $perBranch = @()
    for ($i = $history.Count - 1; $i -ge 0 -and $perBranch.Count -lt 3; $i--) {
        if (@($perBranch | Where-Object { $_.branch -eq $history[$i].branch }).Count -eq 0) { $perBranch += $history[$i] }
    }
    $drift = @()
    if ($perBranch.Count -eq 3) {
        foreach ($m in $metrics) {
            if (@($perBranch | Where-Object { @($_.metrics) -contains $m }).Count -eq 3) { $drift += $m }
        }
    }
    foreach ($m in $drift) {
        Write-Host ("[DRIFT-ALERT] '{0}' warned in 3 consecutive Gates ({1}) - inspect before FullRegression" -f $m, (($perBranch | ForEach-Object { $_.branch }) -join ', ')) -ForegroundColor Red
    }
    return $drift
}
