#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 13 automated verification (TASK-13: HUD micro-widgets, Alt+C / Alt+P, /copy, /pin, Right MFD,
    MCP manage_hud_widgets).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), repository-wide Zero-BOM gate, 0 compiled-Regex option in the C# sources.
    E2  Selftest widgets (2 code blocks + 1 checklist, fence-free content, 4 KB extraction <= 0.5 ms, 30 x /copy) and,
        on the live HostService, pipe COPYWIDGET 1 / 2: the Windows clipboard holds exactly each block, 30 calls
        P50 <= 1.5 ms / P95 <= 2.5 ms (whole /copy incl. the Win32 clipboard write).
    E3  Selftest mfd-pins (lifecycle, FIFO cap 5, Secret Guard) and live: /unpin all -> /pin #1 -> /pin "Cổng TCP Socket
        UTH: 8888" -> /pins = 2, mfd_pins.json without BOM, the Right MFD shows both.
    E4  MCP manage_hud_widgets pin -> the running Cockpit shows it in < 300 ms, foreground window unchanged.
    E5  5 pins + copy load, idle trim, Private WS (two settled reads) <= 52.0 MB, idle CPU < 0.5 %,
        Invoke-NexusRegression -FastRegression (< 90 s, [SLA-WARN SUMMARY]), git guard, CI.
    The user's mfd_pins.json and clipboard text are restored at the end. No Gemini token is used.
    Writes 03_Local_Data_Vault/handoff/task13_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot  = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir = Join-Path $VaultRoot 'handoff'
$PinsPath   = Join-Path $VaultRoot 'config\mfd_pins.json'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe     = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath = Join-Path $HandoffDir 'task13_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$CodeCSharp = "var listener = new TcpListener(IPAddress.Any, 8888);`nlistener.Start();`nConsole.WriteLine(`"listening on 8888`");"
$CodePs     = 'Test-NetConnection -ComputerName 127.0.0.1 -Port 8888'
$NotePin    = 'Cổng TCP Socket UTH: 8888'
$McpPin     = 'Gate 13 MCP pin ' + (Get-Date -Format 'HHmmss')
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

foreach ($v in 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES', 'NEXUS_MEMORY_SLIMMING', 'NEXUS_RELAXED_SLA') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Result([string]$id, [bool]$pass, [string]$detail) {
    Write-Host ("{0}: {1} -> {2}" -f $id, $detail, $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
}
function Test-HasUtf8Bom([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    $b = [System.IO.File]::ReadAllBytes($path)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}
function Send-PipeJson([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    $outer = Send-PipeLine ($req | ConvertTo-Json -Compress) | ConvertFrom-Json
    return ([string]$outer.result | ConvertFrom-Json)
}
function Send-CockpitCommand([string]$text) { return Send-PipeJson 'COCKPIT' $text }
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task13.ps1.'
    }
}
function Start-HostIfNeeded {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $HostExe | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline) { try { $null = Send-PipeJson 'SYNCSTATE' $null; break } catch { Start-Sleep -Milliseconds 300 } }
        Start-Sleep -Seconds 3
    }
    return (Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1)
}
function Invoke-SelfTest([string]$scenario) {
    $path = Join-Path $HandoffDir "task13_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $($p.ExitCode))" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($w in @($data.slaWarnings)) {
        if ($null -ne $w) { Write-Host ("[SLA-WARN (FastRegression jitter)] {0}: {1} = {2} ms (SLA {3} ms)" -f $scenario, $w.metric, $w.measuredMs, $w.slaMs) -ForegroundColor Yellow }
    }
    return [ordered]@{ exitCode = $p.ExitCode; noBom = -not (Test-HasUtf8Bom $path); data = $data }
}
function Get-Percentile([double[]]$values, [double]$p) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $rank = ($p / 100.0) * ($sorted.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    return [math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($rank - $lo), 3)
}

$script:FgApi = $false
try {
    if (-not ('NexusGate13.User32' -as [type])) {
        Add-Type -Namespace NexusGate13 -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'
    }
    $script:FgApi = $true
} catch { }
function Get-Foreground { if ($script:FgApi) { return ('0x{0:X}' -f [NexusGate13.User32]::GetForegroundWindow().ToInt64()) } else { return $null } }

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan
$pinsBackup = if (Test-Path $PinsPath) { [System.IO.File]::ReadAllBytes($PinsPath) } else { $null }
$latestBackup = Save-NexusLatestDispatch $HandoffDir   # TASK-12c (B1)
$clipboardBackup = $null
try { $clipboardBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }

function Restore-UserState {
    if ($null -ne $pinsBackup) { [System.IO.File]::WriteAllBytes($PinsPath, $pinsBackup) }
    elseif (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) { try { $null = Send-CockpitCommand '/unpin all' } catch { } }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
}

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, no compiled Regex'
$buildCode = 0; $buildWarnings = 0; $buildErrors = 0
Stop-HostService
if (-not $SkipBuild) {
    $buildText = (& dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1 | Out-String)
    $buildCode = $LASTEXITCODE
    Write-Host $buildText.Trim()
    if ($buildText -match '(\d+)\s+Warning\(s\)') { $buildWarnings = [int]$Matches[1] }
    if ($buildText -match '(\d+)\s+Error\(s\)')   { $buildErrors   = [int]$Matches[1] }
}
$bom = Test-NexusBom $RepoRoot
foreach ($o in $bom.Offenders) { Write-Host "  BOM rule violated: $o" -ForegroundColor Red }
# TASK-12c B1: selftests now put latest_dispatch.json back when they end; keep the widgets test answer for the live E2 check
# (this script restores the user's file in its final step).
$env:NEXUS_SELFTEST_KEEP_LATEST = '1'
try { $e2Self = Invoke-SelfTest 'widgets' } finally { Remove-Item Env:NEXUS_SELFTEST_KEEP_LATEST -ErrorAction SilentlyContinue }
$compiledFiles = @($e2Self.data.compiledRegexFiles)
$e1Pass = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0) -and $bom.Pass -and ($compiledFiles.Count -eq 0)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | compiled Regex in sources: {5}" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $compiledFiles.Count)

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: card extraction, /copy and pipe COPYWIDGET (P50 <= 1.5 ms, P95 <= 2.5 ms)'
$e2SelfPass = ($e2Self.exitCode -eq 0) -and $e2Self.noBom -and [bool]$e2Self.data.pass
Write-Host ("  selftest: {0} | 4 KB extraction P50 {1} ms | headless /copy P50 {2} / P95 {3} ms | checks failed=[{4}]" -f `
    $e2Self.data.indicator, $e2Self.data.extract4Kb.latency.p50, $e2Self.data.copy.latency.p50, $e2Self.data.copy.latency.p95,
    (($e2Self.data.checks.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object { $_.Name }) -join ','))
# The widgets selftest left its test answer in latest_dispatch.json: it is the live HostService's current response too.
$hostProc = Start-HostIfNeeded
$live1 = Send-PipeJson 'COPYWIDGET' '1'
$live2 = Send-PipeJson 'COPYWIDGET' '2'
$times = @()
$allOk = $true
for ($i = 0; $i -lt 30; $i++) {
    $r = Send-PipeJson 'COPYWIDGET' $(if ($i % 2 -eq 0) { '1' } else { '2' })
    $allOk = $allOk -and [bool]$r.ok
    $times += [double]$r.elapsedMs
}
$p50 = Get-Percentile $times 50
$p95 = Get-Percentile $times 95
$liveContentOk = [bool]$live1.ok -and ($live1.copiedText -eq $CodeCSharp) -and [bool]$live1.clipboardMatches -and
                 [bool]$live2.ok -and ($live2.copiedText -eq $CodePs) -and [bool]$live2.clipboardMatches
$liveLatencyOk = $allOk -and ($p50 -le 1.5) -and ($p95 -le 2.5)
$e2Pass = $e2SelfPass -and $liveContentOk -and $liveLatencyOk
Write-Result 'E2' $e2Pass ("live /copy 1 clipboard match={0}, /copy 2 match={1} (system clipboard={2} {3}) | 30 x COPYWIDGET P50 {4} / P95 {5} ms [{6}]" -f `
    $live1.clipboardMatches, $live2.clipboardMatches, $live1.systemClipboard, $live1.systemClipboardError, $p50, $p95, ($times -join ', '))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: Right MFD pin lifecycle, FIFO cap, Secret Guard, zero-BOM persistence'
$e3Self = Invoke-SelfTest 'mfd-pins'
$e3SelfPass = ($e3Self.exitCode -eq 0) -and $e3Self.noBom -and [bool]$e3Self.data.pass
$hostProc = Start-HostIfNeeded
$u = Send-CockpitCommand '/unpin all'
$a = Send-CockpitCommand '/pin #1'
$b = Send-CockpitCommand "/pin $NotePin"
$l = Send-CockpitCommand '/pins'
$state = Send-PipeJson 'PINSTATE' $null
$pinsNoBom = (Test-Path $PinsPath) -and -not (Test-HasUtf8Bom $PinsPath)
$e3LiveOk = [bool]$u.ok -and [bool]$a.ok -and [bool]$b.ok -and [bool]$l.ok -and ([string]$l.message -like '`[PINS`] 2/*') -and $pinsNoBom -and
            (@($state.pins).Count -eq 2) -and ([string]$state.slots[1] -like "*$NotePin*") -and ([string]$state.header -like '*(2/*')
$e3Pass = $e3SelfPass -and $e3LiveOk
Write-Result 'E3' $e3Pass ("selftest: FIFO {0} | {1} | live: {2} | header '{3}' | mfd_pins.json no BOM={4}" -f `
    (@($e3Self.data.cappedTitles) -join ' / '), $e3Self.data.secret, $l.message, $state.header, $pinsNoBom)

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: MCP manage_hud_widgets pin -> live Right MFD < 300 ms, focus kept'
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 300
$fgBefore = Get-Foreground
$OutputEncoding = $Utf8NoBom
$req = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}',
    ([ordered]@{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = [ordered]@{ name = 'manage_hud_widgets'; arguments = [ordered]@{ action = 'extract' } } } | ConvertTo-Json -Compress -Depth 6),
    ([ordered]@{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = [ordered]@{ name = 'manage_hud_widgets'; arguments = [ordered]@{ action = 'pin'; targetOrText = $McpPin } } } | ConvertTo-Json -Compress -Depth 6),
    ([ordered]@{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = [ordered]@{ name = 'manage_hud_widgets'; arguments = [ordered]@{ action = 'list_pins' } } } | ConvertTo-Json -Compress -Depth 6)
)
$lines = (($req -join "`n") + "`n") | & $McpExe
$byId = @{}
foreach ($ln in @($lines)) { if ([string]::IsNullOrWhiteSpace($ln)) { continue }; try { $o = $ln | ConvertFrom-Json; if ($null -ne $o.id) { $byId[[int]$o.id] = $o } } catch { } }
$mcpExtract = if ($byId.ContainsKey(2)) { [string]$byId[2].result.content[0].text | ConvertFrom-Json } else { $null }
$mcpPinRes  = if ($byId.ContainsKey(3)) { [string]$byId[3].result.content[0].text | ConvertFrom-Json } else { $null }
$sync = $null
$deadline = (Get-Date).AddSeconds(2)
do {
    Start-Sleep -Milliseconds 100
    $sync = Send-PipeJson 'PINSTATE' $null
} while ((Get-Date) -lt $deadline -and -not ([string]$sync.lastSync.newestTitle -eq $McpPin -and $sync.lastSync.newestSource -eq 'McpTools'))
$fgAfter = Get-Foreground
$e4Checks = [ordered]@{
    mcpExtract      = ($null -ne $mcpExtract) -and (@($mcpExtract.result.cards).Count -ge 1)
    mcpPinned       = ($null -ne $mcpPinRes) -and [bool]$mcpPinRes.result.ok
    cockpitShowsPin = ([string]$sync.lastSync.newestTitle -eq $McpPin) -and ($sync.lastSync.newestSource -eq 'McpTools') -and ((@($sync.slots) -join ' ') -like "*$McpPin*")
    under300Ms      = ($null -ne $sync.lastSync.latencyMs) -and ([double]$sync.lastSync.latencyMs -lt 300)
    focusKept       = [bool]$sync.lastSync.foregroundUnchanged -and ((-not $script:FgApi) -or ($fgBefore -eq $fgAfter))
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("MCP pin '{0}' -> Cockpit latency {1} ms | slots: {2} | foreground {3} -> {4} | failed=[{5}]" -f `
    $McpPin, $sync.lastSync.latencyMs, (@($sync.slots) -join ' / '), $fgBefore, $fgAfter,
    (($e4Checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: 5 pins + copy load, idle Private WS <= 52.0 MB, idle CPU < 0.5 %, fast regression, git, CI'
for ($i = 1; $i -le 5; $i++) { $null = Send-CockpitCommand "/pin Gate 13 RAM load $i" }
for ($i = 0; $i -lt 10; $i++) { $null = Send-PipeJson 'COPYWIDGET' '1' }
$full = Send-PipeJson 'PINSTATE' $null
$null = Send-PipeLine '{"cmd":"HIDE"}'
$hostProc = Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1
# TASK-12b: Settled Median Mode of NexusVerify.Common.ps1 (idle trim >= 10 s old, median of 8 reads 250 ms apart).
$idleRead = Get-NexusSettledPrivateWs -ProcessId $hostProc.Id -LastTrimUtc {
    $m = $null; try { $m = Send-PipeJson 'MEM' $null } catch { }
    if ($m -and $m.lastTrim) { $m.lastTrim.AtUtc } else { $null }
}
$idleMb = $idleRead.MedianMb
Write-Host ("  idle Private WS: median {0} MB (min {1}, max {2}; trim age {3} s; {4}): {5}" -f `
    $idleRead.MedianMb, $idleRead.MinMb, $idleRead.MaxMb, $idleRead.TrimAgeSeconds, $idleRead.Source, (@($idleRead.Samples) -join ', '))
$cpuSeries = @()
for ($i = 0; $i -lt 10; $i++) {
    $hostProc.Refresh(); $c0 = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 1
    $hostProc.Refresh()
    $cpuSeries += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
}
$idleCpu = [math]::Round(($cpuSeries | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
Restore-UserState
$fastLines = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-NexusRegression.ps1') -FastRegression -SkipBuild |
    ForEach-Object { Write-Host $_; [string]$_ })
$fastExit = $LASTEXITCODE
$summaryLine = @($fastLines | Where-Object { $_ -match '^\[SLA-WARN SUMMARY\] Count=\d+ \| Details=' }) | Select-Object -Last 1
$fastSummaryPath = Join-Path $HandoffDir 'regression_fast.json'
$fastSummary = if (Test-Path $fastSummaryPath) { Get-Content $fastSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task13'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable or repository private: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    fivePins        = (@($full.pins).Count -eq 5)
    privateWsUnder52 = ($null -ne $idleMb) -and ($idleMb -le 52.0)
    idleCpuUnderHalf = ($idleCpu -lt 0.5)
    fastRegression  = ($fastExit -eq 0) -and ($null -ne $fastSummary) -and [bool]$fastSummary.pass -and ([double]$fastSummary.seconds -lt 90) -and ($null -ne $summaryLine)
    gitUnchanged    = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("pins {0}/5 | idle Private WS {1} MB (<= 52) | idle CPU {2} % | fast {3} s, {4} | git same={5} {6} | CI: {7} | failed=[{8}]" -f `
    @($full.pins).Count, $idleMb, $idleCpu, $fastSummary.seconds, $summaryLine, $gitEnd.Same, ($gitEnd.Reasons -join '; '),
    $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }),
    (($e5Checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ','))
if (-not ($ci.checked -and $ci.conclusion -eq 'success')) { Write-Host '  Confirm the green CI run in the GitHub Actions tab.' -ForegroundColor Yellow }

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-13'; gate = 'Gate 13'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart
    E1_BuildBomRegex = [ordered]@{ pass = $e1Pass; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; compiledRegexFiles = $compiledFiles }
    E2_ExtractCopy = [ordered]@{ pass = $e2Pass; selftest = $e2Self.data; live1 = $live1; live2 = $live2; copyTimesMs = $times; p50 = $p50; p95 = $p95 }
    E3_Pins = [ordered]@{ pass = $e3Pass; selftest = $e3Self.data; live = [ordered]@{ unpin = $u.message; pinCard = $a.message; pinNote = $b.message; list = $l.message; state = $state; noBom = $pinsNoBom } }
    E4_McpSync = [ordered]@{ pass = $e4Pass; checks = $e4Checks; mcpPin = $mcpPinRes; sync = $sync.lastSync; foregroundBefore = $fgBefore; foregroundAfter = $fgAfter }
    E5_MemoryCpuRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; pins = @($full.pins).Count; idlePrivateWsMb = $idleMb; idleRead = $idleRead; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; fast = $fastSummary; summaryLine = $summaryLine; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
Write-Host "`nGate 13 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored (B1).' }
}
