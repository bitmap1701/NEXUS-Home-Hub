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
      Get-NexusPrivateWsMb       (TASK-12b, Immediate Mode) Private Working Set of a process in MB, read once:
                                 GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX2) from outside the process (the source of the
                                 HUD label, no 1 s WMI lag); falls back to Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate
      Get-NexusSettledPrivateWs  (TASK-12b, Settled Median Mode) waits until the last idle trim is >= 10 s old, then 8 reads
                                 250 ms apart: median (the Gate value), min, max and every read
      Save-NexusLatestDispatch / Restore-NexusLatestDispatch (TASK-12c, B1) keep the user's latest_dispatch.json across a
                                 Verify run, so the real Cockpit shows the user's own last dispatch again afterwards
      Measure-NexusPeakPrivateWs (TASK-12b, Peak Sampler) runs an action and reads Private WS every ~100 ms for a few seconds:
                                 baseline, peak, series; envelope ok / soft-warn (> 120 MB) / over-soft-ceiling (> 200 MB)
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

# ------------------------------------------------------------------------------------------------------------------------
# TASK-12b: one Private Working Set measurement for every Verify script (ADR-09 dual envelope: settled idle + transient peak)
# ------------------------------------------------------------------------------------------------------------------------
$script:NexusPwsApi = $null
$script:NexusPwsLastSource = 'not read yet'

function Initialize-NexusPwsApi {
    if ($null -ne $script:NexusPwsApi) { return $script:NexusPwsApi }
    $script:NexusPwsApi = $false
    try {
        if (-not ('NexusVerify.PrivateWs' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace NexusVerify
{
    public static class PrivateWs
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint Cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        /// <summary>Private working set in bytes (PROCESS_MEMORY_COUNTERS_EX2), or -1 when it cannot be read.</summary>
        public static long Read(int processId)
        {
            IntPtr handle = OpenProcess(0x1000 | 0x0010, false, processId); // QUERY_LIMITED_INFORMATION | VM_READ
            if (handle == IntPtr.Zero) return -1;
            try
            {
                Counters c = new Counters();
                c.Cb = (uint)Marshal.SizeOf(typeof(Counters));
                if (!GetProcessMemoryInfo(handle, ref c, c.Cb)) return -1;
                long value = (long)c.PrivateWorkingSetSize.ToUInt64();
                return value > 0 ? value : -1;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
'@
        }
        $script:NexusPwsApi = $true
    } catch {
        $script:NexusPwsApi = $false
    }
    return $script:NexusPwsApi
}

<# Immediate Mode: one read, MB with 1 decimal, $null if the process is gone. Source of the last read: $script:NexusPwsLastSource. #>
function Get-NexusPrivateWsMb([int]$ProcessId, [switch]$Wmi) {
    if (-not $Wmi -and (Initialize-NexusPwsApi)) {
        $bytes = [NexusVerify.PrivateWs]::Read($ProcessId)
        if ($bytes -gt 0) {
            $script:NexusPwsLastSource = 'GetProcessMemoryInfo EX2'
            return [math]::Round($bytes / 1MB, 1)
        }
    }
    $perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $ProcessId" -ErrorAction SilentlyContinue
    if ($perf) {
        $script:NexusPwsLastSource = 'WMI WorkingSetPrivate'
        return [math]::Round($perf.WorkingSetPrivate / 1MB, 1)
    }
    return $null
}

<#
TASK-17b spec E5 (Architect 2026-11-12): settled idle Private WS <= 45,0 MB = PASS. Above that, PASS with [SLA-WARN] when it is
<= 50,0 MB and the GC heap (MEM pipe snapshot) is <= 10,5 MB (8,0 until Architect 17b.1): the rest is native / WPF render-surface noise, not a managed leak.
Anything else = FAIL. $MemJson = the MEM pipe answer read at the same idle point.
#>
function Test-NexusIdleRam($PrivateWsMb, $MemJson, [string]$Label) {
    $gc = $null; if ($MemJson -and $MemJson.snapshot) { $gc = $MemJson.snapshot.GcHeapSizeMb }
    $within = ($null -ne $PrivateWsMb) -and ([double]$PrivateWsMb -le 45.0)
    # Architect 17b.1 (2026-11-12 08:53): GC heap ceiling raised 8,0 -> 10,5 MB when the full HostService sits near 10 MB on the
    # PC (Verify-Task16b in the Gate 17a chain, 09:13: GC heap 10,4 MB). Verify-Task17b E3 keeps its own <= 8,0 check.
    $warn = (-not $within) -and ($null -ne $PrivateWsMb) -and ([double]$PrivateWsMb -le 50.0) -and ($null -ne $gc) -and ([double]$gc -le 10.5)
    if ($warn) { Write-Host ("[SLA-WARN (idle RAM, TASK-17b E5)] {0}: Private WS {1} MB > 45,0 (<= 50,0; GC heap {2} MB <= 10,5)" -f $Label, $PrivateWsMb, $gc) -ForegroundColor Yellow }
    return [ordered]@{ pass = ($within -or $warn); slaWarn = $warn; privateWsMb = $PrivateWsMb; gcHeapMb = $gc }
}

<#
Settled Median Mode. $LastTrimUtc returns the UTC time of the last idle trim (string or [datetime]) or $null while there is none
yet; the reads start once that trim is >= $MinTrimAgeSeconds old (at most $TimeoutSeconds of waiting). Without $LastTrimUtc the
function waits $MinTrimAgeSeconds. Returns MedianMb (the Gate value), MinMb, MaxMb, Samples, TrimAgeSeconds, Source.
#>
function Get-NexusSettledPrivateWs {
    param(
        [int]$ProcessId,
        [scriptblock]$LastTrimUtc = $null,
        [int]$MinTrimAgeSeconds = 10,
        [int]$Samples = 8,
        [int]$IntervalMs = 250,
        [int]$TimeoutSeconds = 30
    )
    $waitStart = Get-Date
    $trimAge = $null
    if ($null -ne $LastTrimUtc) {
        $deadline = $waitStart.AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $t = $null
            try { $t = & $LastTrimUtc } catch { }
            if ($null -ne $t -and [string]$t -ne '') {
                $trimAge = ((Get-Date).ToUniversalTime() - ([datetime]$t).ToUniversalTime()).TotalSeconds
                if ($trimAge -ge $MinTrimAgeSeconds) { break }
                $left = [int][math]::Ceiling(($MinTrimAgeSeconds - $trimAge) * 1000)
                Start-Sleep -Milliseconds ([math]::Max(100, [math]::Min($left, 2000)))
            } else {
                Start-Sleep -Milliseconds 500
            }
        }
    } else {
        Start-Sleep -Seconds $MinTrimAgeSeconds
    }
    if ($null -ne $trimAge -and $null -ne $LastTrimUtc) {
        try { $t = & $LastTrimUtc; if ($t) { $trimAge = ((Get-Date).ToUniversalTime() - ([datetime]$t).ToUniversalTime()).TotalSeconds } } catch { }
    }
    $values = @()
    for ($i = 0; $i -lt $Samples; $i++) {
        if ($i -gt 0) { Start-Sleep -Milliseconds $IntervalMs }
        $v = Get-NexusPrivateWsMb $ProcessId
        if ($null -ne $v) { $values += [double]$v }
    }
    $median = $null; $min = $null; $max = $null
    if ($values.Count -gt 0) {
        $sorted = @($values | Sort-Object)
        $mid = [int][math]::Floor($sorted.Count / 2)
        $median = if ($sorted.Count % 2 -eq 1) { $sorted[$mid] } else { [math]::Round(($sorted[$mid - 1] + $sorted[$mid]) / 2, 2) }
        $min = $sorted[0]
        $max = $sorted[$sorted.Count - 1]
    }
    return [pscustomobject]@{
        MedianMb       = $median
        MinMb          = $min
        MaxMb          = $max
        Samples        = $values
        TrimAgeSeconds = $(if ($null -ne $trimAge) { [math]::Round($trimAge, 1) } else { $null })
        WaitedSeconds  = [math]::Round(((Get-Date) - $waitStart).TotalSeconds, 1)
        Source         = $script:NexusPwsLastSource
    }
}

<#
Peak Sampler. Reads Private WS once (baseline), runs $Action (e.g. send /img over the pipe; the work itself runs in the
HostService background), then reads every ~$IntervalMs ms for $DurationMs. $Until (optional) ends the sampling early once it
returns $true (one more read is taken). Envelope: ok / soft-warn (> 120 MB) / over-soft-ceiling (> 200 MB) - never a gate.
#>
function Measure-NexusPeakPrivateWs {
    param(
        [int]$ProcessId,
        [scriptblock]$Action = $null,
        [int]$DurationMs = 5000,
        [int]$IntervalMs = 100,
        [scriptblock]$Until = $null,
        [double]$SoftWarnMb = 120.0,
        [double]$SoftCeilingMb = 200.0
    )
    $baseline = Get-NexusPrivateWsMb $ProcessId
    $series = New-Object System.Collections.Generic.List[double]
    $result = $null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($null -ne $Action) { $result = & $Action }
    $done = $false
    while ($sw.ElapsedMilliseconds -lt $DurationMs) {
        $v = Get-NexusPrivateWsMb $ProcessId
        if ($null -ne $v) { $series.Add([double]$v) }
        if ($done) { break }
        if ($null -ne $Until) { try { if (& $Until) { $done = $true } } catch { } }
        Start-Sleep -Milliseconds $IntervalMs
    }
    $all = @($series.ToArray())
    if ($null -ne $baseline) { $all += [double]$baseline }
    $peak = if ($all.Count -gt 0) { ($all | Measure-Object -Maximum).Maximum } else { $null }
    $envelope = if ($null -eq $peak) { 'unknown' } elseif ($peak -gt $SoftCeilingMb) { 'over-soft-ceiling' } elseif ($peak -gt $SoftWarnMb) { 'soft-warn' } else { 'ok' }
    return [pscustomobject]@{
        BaselineMb = $baseline
        PeakMb     = $peak
        Samples    = $series.Count
        Series     = @($series.ToArray())
        DurationMs = $sw.ElapsedMilliseconds
        IntervalMs = $IntervalMs
        Envelope   = $envelope
        Source     = $script:NexusPwsLastSource
        Result     = $result
    }
}

# ------------------------------------------------------------------------------------------------------------------------
# TASK-12c (B1): the user's latest_dispatch.json is put back after a Verify run (atomic replace, ADR-14)
# ------------------------------------------------------------------------------------------------------------------------
function Save-NexusLatestDispatch([string]$HandoffDir) {
    $path = Join-Path $HandoffDir 'latest_dispatch.json'
    if (Test-Path $path) { return ,([System.IO.File]::ReadAllBytes($path)) }
    return $null
}

function Restore-NexusLatestDispatch([string]$HandoffDir, $Bytes) {
    if ($null -eq $Bytes) { return $false }
    $path = Join-Path $HandoffDir 'latest_dispatch.json'
    $tmp = "$path.verify-restore.tmp"
    try {
        [System.IO.File]::WriteAllBytes($tmp, [byte[]]$Bytes)
        if (Test-Path $path) { [System.IO.File]::Replace($tmp, $path, [System.Management.Automation.Language.NullString]::Value) }
        else { [System.IO.File]::Move($tmp, $path) }
        return $true
    } catch {
        Write-Host ("  latest_dispatch.json not restored: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        return $false
    }
}
