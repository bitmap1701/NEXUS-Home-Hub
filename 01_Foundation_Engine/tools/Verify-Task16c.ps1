#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 16c automated verification (TASK-16c, ADR-68: Cockpit UI v2 visual layer - 2 columns, 44 px chips bar,
    40 px single line, Card Pool <= 20, running card + Hủy (Esc), "/" and "@" palettes, VIỆC TIẾP THEO card, clipboard image
    card, F1 quick switches, Alt+A alliance board with Alt+G / Alt+K), with the Gate 16b WAKE budget and the Gate 15 footprint kept.
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate, ADR-68 in docs/ADR_REGISTRY.md, no embedded font file (Segoe UI
        Variable + Cascadia Mono from Windows, fallback Segoe UI / Consolas), selftest ui-v2-visual (Core + the real window
        measured headless), and "@<agentId> task" dispatching to that agent of the registry on this PC.
    E2  Live HostService (Gemini mocked): Hủy first - /ask! with a 3 s mock delay, the running card shows the elapsed time and
        Hủy, CANCEL (the same CancelRunning as Esc) cancels the CancellationTokenSource, the card says [ĐÃ HỦY] and stays so
        after the 3 s (late responses ignored), 0 token. Then a fresh HostService: 44 px top bar, 2 columns (right 300 px),
        the 40 px single line in collapsed level with the body clipped, the Card Pool <= 20, a /calc line marked
        "cục bộ · 0 token".
    E3  "/" palette (VỪA DÙNG + TẤT CẢ, the 17 commands of the spec, filter "/ca" -> /calc, Tab fills "/calc "), "@sec" from the
        agent registry of this PC (Tab fills "@<id> "), VIỆC TIẾP THEO card (Alt+Enter copies and hides it), clipboard image card
        on wake (Enter = /img "Lỗi gì đây, sửa sao?"), F1 board with the 6 quick switches, Alt+A board + Alt+G / Alt+K.
    E4  WAKE 40 -> 330 px: ColdFirstWake (first wake after the restart, report only), Warmup <= 60 ms, N = 10 scored wakes
        P50 <= 10,0 / P95 <= 16,0 ms with LevelParts; each wake waits for 40 px + 50 ms outside the timer.
    E5  Settled idle Private WS <= 45,0 MB, idle CPU 0,0 %, regression chain Verify-Task15 (-> Task16b -> Task16 -> Task13 ->
        Task12 -> Task13a -> Task14 ... Task03, -SkipLive) with 0 live Gemini calls (Task07/09/10/11 audited), git guard, CI.
    The user's latest_dispatch.json, cockpit_ui.json and clipboard text are restored; at the end the script reports its own
    result through MCP notify_shift_complete. Writes 03_Local_Data_Vault/handoff/task16c_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5   # development loop: Verify-Task15 -FastE5 (-> Verify-Task16b -FastE5) instead of the full chain
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$VaultRoot  = Join-Path $RepoRoot '03_Local_Data_Vault'
$HandoffDir = Join-Path $VaultRoot 'handoff'
$ConfigDir  = Join-Path $VaultRoot 'config'
$UiPath     = Join-Path $ConfigDir 'cockpit_ui.json'
$LatestPath = Join-Path $HandoffDir 'latest_dispatch.json'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe     = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath = Join-Path $HandoffDir 'task16c_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$RunId      = Get-Date -Format 'HHmmss'
$Marker     = "huymark$RunId" + (Get-Random -Minimum 1000 -Maximum 9999)
$MockAnswer = 'Gate 16c visual mock'
$CancelDelayMs = 3000
$LocalBadge = 'cục bộ · 0 token'
$SpecCommands = '/img', '/calc', '/ask', '/g', '/snap', '/pin', '/ctx', '/timer', '/note', '/copy', '/unpin', '/qa', '/retry', '/mem', '/status', '/boost', '/ui'
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$envNames = 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_MOCK_DELAY_MS', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES',
            'NEXUS_RELAXED_SLA', 'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE', 'NEXUS_UI_AUTOCOLLAPSE_MS'
$savedUserEnv = @{}
foreach ($v in $envNames) {
    if (Test-Path "Env:$v") { $savedUserEnv[$v] = (Get-Item "Env:$v").Value; Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
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
function Send-PipeRaw([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    return [string](Send-PipeLine ($req | ConvertTo-Json -Compress) | ConvertFrom-Json).result
}
function Send-PipeJson([string]$cmd, [string]$text) { return ((Send-PipeRaw $cmd $text) | ConvertFrom-Json) }
function Get-UiState { return Send-PipeJson 'UISTATE' $null }
function Get-Visual { return (Get-UiState).visual }
function Set-Ui([string]$action) { return Send-PipeJson 'UI' $action }
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task16c.ps1.'
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
# The Gemini mock (and the Hủy test's mock delay) live in the HostService environment only.
function Restart-Host([bool]$mock, [int]$delayMs = 0) {
    Stop-HostService
    if ($mock) { $env:NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
    if ($delayMs -gt 0) { $env:NEXUS_GEMINI_MOCK_DELAY_MS = [string]$delayMs }
    try { $p = Start-HostIfNeeded }
    finally {
        if (Test-Path Env:NEXUS_GEMINI_MOCK_RESPONSE) { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE }
        if (Test-Path Env:NEXUS_GEMINI_MOCK_DELAY_MS) { Remove-Item Env:NEXUS_GEMINI_MOCK_DELAY_MS }
    }
    return $p
}
function Invoke-SelfTest([string]$scenario, [string]$prefix) {
    $path = Join-Path $HandoffDir "${prefix}_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $($p.ExitCode))" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    return [ordered]@{ exitCode = $p.ExitCode; noBom = -not (Test-HasUtf8Bom $path); data = $data }
}
function Invoke-Mcp([object[]]$calls) {
    $OutputEncoding = $Utf8NoBom
    $req = @('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}')
    $id = 2
    foreach ($c in $calls) {
        $req += ([ordered]@{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = [ordered]@{ name = $c.name; arguments = $c.arguments } } | ConvertTo-Json -Compress -Depth 6)
        $id++
    }
    $lines = (($req -join "`n") + "`n") | & $McpExe
    $byId = @{}
    foreach ($ln in @($lines)) {
        if ([string]::IsNullOrWhiteSpace($ln)) { continue }
        try { $o = $ln | ConvertFrom-Json; if ($null -ne $o.id -and $null -ne $o.result) { $byId[[int]$o.id] = ([string]$o.result.content[0].text | ConvertFrom-Json) } } catch { }
    }
    return $byId
}
function Get-FailedNames($checks) { return (($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ',') }
function Get-Percentile([double[]]$values, [double]$p) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $rank = ($p / 100.0) * ($sorted.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    return [math]::Round($sorted[$lo] + ($sorted[$hi] - $sorted[$lo]) * ($rank - $lo), 3)
}
function Wait-Ui([scriptblock]$condition, [int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $s = Get-UiState
        if (& $condition $s) { return $s }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $s
}
function Test-Near([object]$value, [double]$target, [double]$tolerance = 0.5) {
    return ($null -ne $value) -and ([math]::Abs([double]$value - $target) -le $tolerance)
}
function Get-SelectableRows($palette) { return @($palette.rows | Where-Object { [bool]$_.selectable }) }

$script:ClipApi = $false
try { Add-Type -AssemblyName System.Windows.Forms, System.Drawing; $script:ClipApi = ([System.Threading.Thread]::CurrentThread.ApartmentState -eq 'STA') } catch { }
function Set-ClipboardTestImage {
    $bmp = New-Object System.Drawing.Bitmap 96, 48
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::FromArgb(255, 11, 18, 32))
            $g.DrawString('ERR 16c', (New-Object System.Drawing.Font('Consolas', 10)), [System.Drawing.Brushes]::OrangeRed, 4, 14)
        } finally { $g.Dispose() }
        $do = New-Object System.Windows.Forms.DataObject
        $do.SetImage($bmp)
        $do.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-Object System.IO.MemoryStream(, [byte[]](0, 0, 0, 0))))
        [System.Windows.Forms.Clipboard]::SetDataObject($do, $true)
    } finally { $bmp.Dispose() }
}

# Gate 15 run 2/3 convention (unchanged): every WAKE sample carries LevelParts; each wake waits until the window is back at 40 px
# plus a 50 ms static pause outside the timer.
function Get-WakeSample {
    $null = Set-Ui 'collapsed'
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    Start-Sleep -Milliseconds 300
    $pre = Wait-Ui { param($u) ([string]$u.level -eq 'collapsed') -and ([double]$u.height -le 40.5) } 1000
    $settled = ($null -ne $pre) -and ([string]$pre.level -eq 'collapsed') -and ([double]$pre.height -le 40.5)
    Start-Sleep -Milliseconds 50
    $null = Send-PipeRaw 'COMMANDMODE' $null
    Start-Sleep -Milliseconds 250
    $w = (Get-UiState).lastWake
    $parts = if ($w) { $w.LevelParts } else { $null }
    return [ordered]@{
        ms = $(if ($w) { [double]$w.Ms } else { $null }); from = $w.From; to = $w.To; focused = $w.InputFocused
        settledAt40 = $settled; preHeight = $(if ($pre) { $pre.height } else { $null })
        LevelParts = [ordered]@{
            HwndCaptureMs  = $(if ($w) { $w.BeforeLevelMs } else { $null })
            WindowHeightMs = $(if ($parts) { $parts.WindowHeightMs } else { $null })
            ActivateMs     = $(if ($w) { $w.ActivateMs } else { $null })
            FocusMs        = $(if ($w) { $w.FocusMs } else { $null })
            LevelAndShowMs = $(if ($w) { $w.LevelAndShowMs } else { $null })
            ChromeMs       = $(if ($parts) { $parts.ChromeMs } else { $null })
            ClampMs        = $(if ($parts) { $parts.ClampMs } else { $null })
            RestMs         = $(if ($parts) { $parts.RestMs } else { $null })
            LayoutMs       = $(if ($w) { $w.LayoutMs } else { $null })
        }
    }
}
function Format-WakeParts($s) {
    $p = $s.LevelParts
    '{0:N2} ms [hwnd {1} | height {2} | activate {3} | focus {4}]' -f $s.ms, $p.HwndCaptureMs, $p.WindowHeightMs, $p.ActivateMs, $p.FocusMs
}

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan
$latestBackup = Save-NexusLatestDispatch $HandoffDir
$uiBackup = if (Test-Path $UiPath) { [System.IO.File]::ReadAllBytes($UiPath) } else { $null }
$clipboardBackup = $null
try { $clipboardBackup = Get-Clipboard -Raw -ErrorAction Stop } catch { }
$script:Restored = $false
function Restore-UserState {
    if ($script:Restored) { return }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        try { $null = Send-PipeRaw 'PALETTE' $null } catch { }
    }
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    $script:Restored = $true
}

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, ADR-68 registered, no embedded font, selftest ui-v2-visual'
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
$adrText = Get-Content (Join-Path $RepoRoot 'docs\ADR_REGISTRY.md') -Raw -Encoding UTF8
$adr68 = $adrText -match '(?m)^\|\s*ADR-68\s*\|'
$fontFiles = @(Get-ChildItem $EngineRoot -Recurse -File -Include '*.ttf', '*.otf', '*.woff', '*.woff2' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName.Substring($RepoRoot.Length + 1) })
$xaml = Get-Content (Join-Path $EngineRoot 'NEXUSHome.CockpitUI\CockpitShellWindow.xaml') -Raw -Encoding UTF8
$fontRefs = ($xaml -match 'Segoe UI Variable Text, Segoe UI') -and ($xaml -match 'Cascadia Mono, Consolas')
# no looping animation in the Cockpit (CPU idle 0 %): no Storyboard / *Animation object / BeginAnimation / RepeatBehavior in code or XAML
$animationHits = @(Get-ChildItem (Join-Path $EngineRoot 'NEXUSHome.CockpitUI') -Recurse -File -Include '*.xaml', '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern '<Storyboard|new\s+\w*Animation\b|BeginAnimation\(|RepeatBehavior' |
    ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber })
$vSelf = Invoke-SelfTest 'ui-v2-visual' 'task16c'
$vData = $vSelf.data
$e1Checks = [ordered]@{
    build        = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom      = [bool]$bom.Pass
    adr68        = [bool]$adr68
    noEmbeddedFont = ($fontFiles.Count -eq 0) -and $fontRefs
    noAnimation  = ($animationHits.Count -eq 0)
    selftest     = ($vSelf.exitCode -eq 0) -and $vSelf.noBom -and [bool]$vData.pass
    # the registry of this PC has real agents, so the selftest also dispatched "@<agentId> task" to that agent
    atAgentDispatch = ($null -ne $vData.core.atDispatch) -and [bool]$vData.checks.'core.atAgentDispatch'
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | ADR-68 {5} | font files [{6}] refs {7} | animations [{15}] | selftest ui-v2-visual pass={8} failed=[{9}] window {10} ms, cancel {11} ms after Hủy | @agent {12} -> {13} | failed=[{14}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $adr68, ($fontFiles -join ','), $fontRefs, $vData.pass, (@($vData.failed) -join ','),
    $vData.windowMs, $vData.cancel.afterCancelMs, $vData.core.atDispatch.agent, $vData.core.atDispatch.primary, (Get-FailedNames $e1Checks), ($animationHits -join ','))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: Hủy (Esc) cancels the request, then 44 px bar, 2 columns (300 px), 40 px line, Card Pool <= 20, local line'
# (a) Hủy: a HostService whose mock Gemini waits 3 s, so the request is still running when it is cancelled.
$hostProc = Restart-Host $true $CancelDelayMs
$null = Set-Ui 'half'
$askRes = Send-PipeJson 'COCKPIT' "/ask! Gate 16c kiểm thử Hủy (Gate 16c E2) $Marker"
Start-Sleep -Milliseconds 400
$runningUi = (Get-Visual)
$swCancel = [System.Diagnostics.Stopwatch]::StartNew()
$cancelRes = Send-PipeJson 'CANCEL' $null
$cancelPipeMs = [math]::Round($swCancel.Elapsed.TotalMilliseconds, 1)
Start-Sleep -Milliseconds ($CancelDelayMs + 700)   # past the mock delay: a late answer must not replace [ĐÃ HỦY]
$afterUi = (Get-Visual)
$cancelChecks = [ordered]@{
    pendingAtStart = [bool]$askRes.aiPending
    runningCard    = ($null -ne $runningUi.running) -and [bool]$runningUi.running.cancellable -and [bool]$runningUi.card.cancelVisible -and
                     ($null -ne $runningUi.card.elapsed) -and [bool]$runningUi.runningTimerEnabled -and [bool]$runningUi.card.progressVisible
    cancelled      = [bool]$cancelRes.cancelled -and [bool]$cancelRes.lastCancel.tokenCancelled -and ([string]$cancelRes.card -eq '[ĐÃ HỦY]')
    staysCancelled = ($null -eq $afterUi.running) -and (-not [bool]$afterUi.runningTimerEnabled) -and ([string]$afterUi.card.qa -eq '[ĐÃ HỦY]') -and
                     (-not [bool]$afterUi.card.cancelVisible) -and ([string]$afterUi.lastRunning.reason -eq 'cancelled')
}

# (b) layout on a fresh HostService (mock without delay); its first wake is the ColdFirstWake of E4 (report only)
$hostProc = Restart-Host $true
$coldLevel0 = [string](Get-UiState).level
$coldFirstWake = Get-WakeSample
Write-Host ('  ColdFirstWake (report only) ' + (Format-WakeParts $coldFirstWake)) -ForegroundColor DarkGray
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
Start-Sleep -Milliseconds 300
$half = Get-UiState
$hv = $half.visual
$null = Set-Ui 'collapsed'
$col = Wait-Ui { param($u) ([string]$u.level -eq 'collapsed') -and ([double]$u.height -le 40.5) } 1500
$null = Set-Ui 'half'
Start-Sleep -Milliseconds 300
$back = Get-Visual
$null = Send-PipeJson 'HISTORY' 'refresh'
$calc = Send-PipeJson 'COCKPIT' '/calc 12*12+0'
Start-Sleep -Milliseconds 300
$lv = Get-Visual
$localItems = @($lv.thread.items | Where-Object { [string]$_.kind -eq 'local' })
$layoutChecks = [ordered]@{
    topBar44        = (Test-Near $hv.topBarHeight 44) -and (Test-Near $hv.topRowHeight 44)
    twoColumns      = ([int]$hv.columns -eq 2) -and (Test-Near $hv.rightColumnWidth 300) -and ([double]$hv.rightColumnDefinedWidth -eq 300) -and ([double]$hv.conversationWidth -gt 300)
    fonts           = ([string]$hv.fonts.ui -eq 'Segoe UI Variable Text, Segoe UI') -and ([string]$hv.fonts.mono -eq 'Cascadia Mono, Consolas')
    collapsedLine   = ($null -ne $col) -and ([double]$col.height -le 40.5) -and [bool]$col.visual.collapsedLineVisible -and ([string]$col.visual.collapsedLine -like '*NEXUS*') -and
                      [bool]$col.visual.levelButtons.collapsed -and (-not [bool]$back.collapsedLineVisible) -and [bool]$back.levelButtons.half
    cardPool20      = ([int]$lv.thread.pool -le 20) -and ([int]$lv.thread.max -eq 20) -and ([int]$lv.thread.shown -le 20) -and (@($lv.thread.items).Count -le 20)
    localLine       = ($localItems.Count -ge 1) -and ([string]$localItems[-1].badge -eq $LocalBadge) -and ([string]$localItems[-1].result -like '*144*')
}
$e2Checks = [ordered]@{}
foreach ($k in $cancelChecks.Keys) { $e2Checks["cancel.$k"] = $cancelChecks[$k] }
foreach ($k in $layoutChecks.Keys) { $e2Checks[$k] = $layoutChecks[$k] }
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("Hủy: pending={0}, running card {1} s '{2}', CANCEL pipe {3} ms (in-app {4} ms), card '{5}' -> after {6} s '{7}' | bar {8}/{9} px, columns {10} (right {11} px, body {12} px), fonts '{13}' / '{14}' | collapsed {15} px line '{16}' | pool {17} shown {18} | local '{19}' [{20}] | failed=[{21}]" -f `
    $askRes.aiPending, $runningUi.running.seconds, $runningUi.card.elapsed, $cancelPipeMs, $cancelRes.lastCancel.ms, $cancelRes.card, (($CancelDelayMs + 700) / 1000), $afterUi.card.qa,
    $hv.topBarHeight, $hv.topRowHeight, $hv.columns, $hv.rightColumnWidth, $hv.conversationWidth, $hv.fonts.ui, $hv.fonts.mono,
    $(if ($col) { $col.height } else { 'n/a' }), $(if ($col) { $col.visual.collapsedLine } else { 'n/a' }), $lv.thread.pool, $lv.thread.shown,
    $(if ($localItems.Count) { $localItems[-1].title + ' = ' + $localItems[-1].result } else { 'none' }), $(if ($localItems.Count) { $localItems[-1].badge } else { '' }), (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: "/" palette, "@sec" agents, VIỆC TIẾP THEO (Alt+Enter), clipboard image card, F1 switches, Alt+A board + Alt+G / Alt+K'
$pAll = Send-PipeJson 'PALETTE' '/'
$allNames = @(Get-SelectableRows $pAll.palette | ForEach-Object { [string]$_.a })
$headers = @($pAll.palette.rows | Where-Object { -not [bool]$_.selectable } | ForEach-Object { [string]$_.a })
$missingCmds = @($SpecCommands | Where-Object { $allNames -notcontains $_ })
$pCa = Send-PipeJson 'PALETTE' '/ca'
$caRows = @(Get-SelectableRows $pCa.palette)
$pCaTab = Send-PipeJson 'PALETTE' 'key:tab'
$pNone = Send-PipeJson 'PALETTE' '/zzzq'
$pEsc = Send-PipeJson 'PALETTE' 'key:esc'
$null = Send-PipeRaw 'PALETTE' $null
$pSec = Send-PipeJson 'PALETTE' '@sec'
$secRows = @(Get-SelectableRows $pSec.palette)
$secMatch = @($secRows | Where-Object { ([string]$_.a + ' ' + [string]$_.b + ' ' + [string]$_.c) -match '(?i)sec' })
$registryCount = if ([string]$pSec.palette.header -match '(\d+)\s+AGENT') { [int]$Matches[1] } else { 0 }
$null = Send-PipeJson 'PALETTE' 'key:down'
$pSecTab = Send-PipeJson 'PALETTE' 'key:tab'
$secPicked = if ($secRows.Count -ge 2) { [string]$secRows[1].c } elseif ($secRows.Count -eq 1) { [string]$secRows[0].c } else { '' }
$null = Send-PipeRaw 'PALETTE' $null

$lazyShow = Send-PipeJson 'LAZY' 'lazy'
$lazyUi = (Get-Visual).lazy
$lazyCopy = Send-PipeJson 'LAZY' 'altenter'
$lazyAfter = (Get-Visual).lazy

$imageVisible = $false; $imageAsk = $null; $imageAfter = $null; $imageNote = 'clipboard API not available (PowerShell not STA)'
if ($script:ClipApi) {
    Set-ClipboardTestImage
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    $null = Send-PipeRaw 'COMMANDMODE' $null      # a wake probes the clipboard once (Background priority)
    $iv = Wait-Ui { param($u) [bool]$u.visual.imageCardVisible } 1500
    $imageVisible = [bool]$iv.visual.imageCardVisible
    $imageAsk = Send-PipeJson 'LAZY' 'enter'
    Start-Sleep -Milliseconds 300
    $imageAfter = Wait-Ui { param($u) $null -eq $u.visual.running } 8000
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    $imageNote = 'ok'
}

$f1On = Send-PipeJson 'LAZY' 'f1'
$f1Ui = (Get-Visual).f1
$f1Off = Send-PipeJson 'LAZY' 'f1'
$boardOn = Send-PipeJson 'ALLIANCE' 'toggle'
$gem = Send-PipeJson 'LAZY' 'altg'
$gemBoard = (Get-Visual).lastBoardCopy
$prm = Send-PipeJson 'LAZY' 'altk'
$prmBoard = (Get-Visual).lastBoardCopy
$boardOff = Send-PipeJson 'ALLIANCE' 'toggle'
$e3Checks = [ordered]@{
    slashPalette   = [bool]$pAll.palette.visible -and ([string]$pAll.palette.mode -eq 'slash') -and ($missingCmds.Count -eq 0) -and ($headers -contains 'TẤT CẢ')
    recentGroup    = ($headers -contains 'VỪA DÙNG') -and (@($pAll.palette.recent) -contains '/calc')
    slashFilter    = ($caRows.Count -ge 1) -and ([string]$caRows[0].a -eq '/calc') -and ([string]$pCa.palette.selectedFill -eq '/calc') -and
                     ([string]$pCaTab.box -eq '/calc ') -and (-not [bool]$pCaTab.palette.visible)
    slashNoMatch   = ([string]$pNone.palette.rows[0].a -eq '(không khớp)') -and (-not [bool]$pEsc.palette.visible)
    agentPalette   = [bool]$pSec.palette.visible -and ([string]$pSec.palette.mode -eq 'agent') -and ($secRows.Count -ge 1) -and ($secRows.Count -le 8) -and
                     ($secMatch.Count -eq $secRows.Count) -and ($registryCount -ge 100)
    agentTab       = ($secPicked.Length -gt 0) -and ([string]$pSecTab.box -eq "@$secPicked ") -and (-not [bool]$pSecTab.palette.visible)
    lazyNextStep   = [bool]$lazyUi.visible -and ([string]$lazyUi.title).Length -gt 0 -and (@($lazyUi.steps).Count -eq 4) -and
                     (-not [bool]$lazyAfter.visible) -and ([string]$lazyCopy.lastCopied).Length -gt 0
    imageCard      = $imageVisible -and ($null -ne $imageAsk) -and ([string]$imageAfter.visual.lastImageAsk.prompt -eq 'Lỗi gì đây, sửa sao?') -and
                     [bool]$imageAfter.visual.lastImageAsk.ok -and (-not [bool]$imageAfter.visual.imageCardVisible)
    f1Switches     = [bool]$f1On.cheatSheetVisible -and [bool]$f1Ui.switchesInF1 -and (-not [bool]$f1Off.cheatSheetVisible)
    allianceBoard  = [bool]$boardOn.panelVisible -and ([string]$boardOn.board.gateTitle -like 'Gate *') -and (@($boardOn.board.checks).Count -eq 5) -and (-not [bool]$boardOff.panelVisible)
    altGAltK       = ([int]$gem.result -gt 0) -and ([int]$prm.result -gt 0) -and ($null -ne $gemBoard) -and ($null -ne $prmBoard) -and ([string]$prm.lastCopied -match 'Verify')
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("'/' {0} commands, groups [{1}], missing [{2}] | '/ca' -> {3} -> Tab '{4}' | '@sec' {5} of {6} agents [{7}] -> Tab '{8}' | lazy '{9}' -> Alt+Enter hidden={10} | image card {11} ({12}) -> /img ok={13} | F1 switches {14} | board '{15}' badge {16}, Alt+G {17} chars, Alt+K {18} chars | failed=[{19}]" -f `
    $allNames.Count, ($headers -join ' + '), ($missingCmds -join ','), $(if ($caRows.Count) { $caRows[0].a } else { 'none' }), $pCaTab.box,
    $secRows.Count, $registryCount, (($secRows | ForEach-Object { $_.a }) -join ', '), $pSecTab.box, $lazyUi.title, (-not [bool]$lazyAfter.visible),
    $imageVisible, $imageNote, $(if ($imageAfter) { $imageAfter.visual.lastImageAsk.ok } else { 'n/a' }), $f1Ui.switchesInF1,
    $boardOn.board.gateTitle, $boardOn.board.badge, $gem.result, $prm.result, (Get-FailedNames $e3Checks))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: WAKE 40 -> 330 px still P50 <= 10,0 / P95 <= 16,0 ms; no test row in the Q&A history'
$warmup = Get-WakeSample
Write-Host ('  Warmup  ' + (Format-WakeParts $warmup)) -ForegroundColor DarkGray
$wakes = @()
for ($i = 0; $i -lt 10; $i++) {
    $s = Get-WakeSample
    $wakes += $s
    Write-Host ('  WAKE {0,2} {1}' -f ($i + 1), (Format-WakeParts $s)) -ForegroundColor DarkGray
}
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui $(if (@('collapsed', 'half', 'full') -contains $coldLevel0) { $coldLevel0 } else { 'half' })
$wakeMs = @($wakes | Where-Object { $null -ne $_.ms } | ForEach-Object { [double]$_.ms })
$wakeP50 = Get-Percentile $wakeMs 50
$wakeP95 = Get-Percentile $wakeMs 95
$hist = Send-PipeJson 'HISTORY' 'refresh'
$testRows = @($hist.rows | Where-Object { ([string]$_.aiModel -match '(?i)mock') -or ([string]$_.task -match $Marker) -or ([string]$_.task -match '\(Gate [^)]*E[^)]*\)') })
$e4Checks = [ordered]@{
    warmupUnder60 = ($null -ne $warmup.ms) -and ([double]$warmup.ms -le 60.0) -and ([string]$warmup.from -eq 'collapsed') -and ([string]$warmup.to -eq 'half')
    wakeSettled40 = [bool]$warmup.settledAt40 -and (@($wakes | Where-Object { -not [bool]$_.settledAt40 }).Count -eq 0)
    wakeParts     = (@($wakes | Where-Object { $null -eq $_.LevelParts.HwndCaptureMs -or $null -eq $_.LevelParts.WindowHeightMs -or $null -eq $_.LevelParts.ActivateMs -or $null -eq $_.LevelParts.FocusMs }).Count -eq 0)
    wakeShape     = (@($wakes | Where-Object { $_.from -ne 'collapsed' -or $_.to -ne 'half' -or -not [bool]$_.focused }).Count -eq 0) -and ($wakeMs.Count -eq 10)
    wakeP50       = ($null -ne $wakeP50) -and ($wakeP50 -le 10.0)
    wakeP95       = ($null -ne $wakeP95) -and ($wakeP95 -le 16.0)
    historyClean  = ($testRows.Count -eq 0)
}
$e4Pass = -not ($e4Checks.Values -contains $false)
$slowest = $wakes | Where-Object { $null -ne $_.ms } | Sort-Object { [double]$_.ms } -Descending | Select-Object -First 1
Write-Host ('  ColdFirstWake (report only, first wake after the E2 restart) ' + (Format-WakeParts $coldFirstWake)) -ForegroundColor DarkGray
Write-Result 'E4' $e4Pass ("Warmup {0} (<= 60) | WAKE N=10 P50 {1} / P95 {2} ms ({3}) | slowest {4} | history {5} rows, test rows in it: {6} | failed=[{7}]" -f `
    (Format-WakeParts $warmup), $wakeP50, $wakeP95, (($wakeMs | ForEach-Object { '{0:N2}' -f $_ }) -join ' / '),
    $(if ($slowest) { Format-WakeParts $slowest } else { 'n/a' }), @($hist.rows).Count, $testRows.Count, (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45,0 MB, idle CPU 0,0 %, regression Verify-Task15 (-> Task16b ... Task03, -SkipLive), git, CI'
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 8000
$idleRead = Get-NexusSettledPrivateWs -ProcessId $hostProc.Id -LastTrimUtc {
    $mm = $null; try { $mm = Send-PipeJson 'MEM' $null } catch { }
    if ($mm -and $mm.lastTrim) { $mm.lastTrim.AtUtc } else { $null }
}
$idleMb = $idleRead.MedianMb
$cpuSeries = @()
for ($i = 0; $i -lt 10; $i++) {
    $hostProc.Refresh(); $c0 = $hostProc.TotalProcessorTime; $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 1
    $hostProc.Refresh()
    $cpuSeries += [math]::Round(($hostProc.TotalProcessorTime - $c0).TotalMilliseconds / ($sw.Elapsed.TotalMilliseconds * [Environment]::ProcessorCount) * 100, 2)
}
$idleCpu = [math]::Round(($cpuSeries | Select-Object -Last 5 | Measure-Object -Average).Average, 2)
$memJson = $null; try { $memJson = Send-PipeJson 'MEM' $null } catch { }
$idleVisual = $null; try { $idleVisual = Get-Visual } catch { }
Restore-UserState
if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored before the regression chain.' }
$null = Restart-Host $false
$chainArgs = @('-SkipBuild'); if ($FastE5) { $chainArgs += '-FastE5' }
$chainStartUtc = [DateTime]::UtcNow
$swChain = [System.Diagnostics.Stopwatch]::StartNew()
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task15.ps1') @chainArgs | Out-Host
$chainExit = $LASTEXITCODE
$chainSeconds = [math]::Round($swChain.Elapsed.TotalSeconds, 1)
# Same audit as Gate 15: the reports of the four scripts that can call Gemini, written during this chain, say skipLive=true and
# liveCallAttempted=false. The full chain runs all four; the -FastE5 chain runs only Task11.
$liveAudit = @()
foreach ($n in @('task07', 'task09', 'task10', 'task11')) {
    $p = Join-Path $HandoffDir "${n}_verification.json"
    $fresh = (Test-Path $p) -and ((Get-Item $p).LastWriteTimeUtc -ge $chainStartUtc)
    $j = if ($fresh) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $hasFields = ($null -ne $j) -and (@($j.PSObject.Properties.Name) -contains 'skipLive') -and (@($j.PSObject.Properties.Name) -contains 'liveCallAttempted')
    $liveAudit += [ordered]@{ report = $n; ranInChain = $fresh; hasFields = $hasFields
        skipLive = $(if ($hasFields) { [bool]$j.skipLive } else { $null }); liveCallAttempted = $(if ($hasFields) { [bool]$j.liveCallAttempted } else { $null }) }
}
$liveRequired = if ($FastE5) { @('task11') } else { @('task07', 'task09', 'task10', 'task11') }
$liveCalls = @($liveAudit | Where-Object { $_.ranInChain -and (-not $_.hasFields -or $_.liveCallAttempted -or -not $_.skipLive) })
$liveMissing = @($liveAudit | Where-Object { ($liveRequired -contains $_.report) -and -not $_.ranInChain })
$chainReports = [ordered]@{}
foreach ($n in @('task15', 'task16b')) {
    $p = Join-Path $HandoffDir "${n}_verification.json"
    $j = if ((Test-Path $p) -and ((Get-Item $p).LastWriteTimeUtc -ge $chainStartUtc)) { Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $chainReports[$n] = $(if ($j) { [bool]$j.allPassed } else { $null })
}
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task16c'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45 = ($null -ne $idleMb) -and ($idleMb -le 45.0)
    idleCpuZero      = ([math]::Round($idleCpu, 1) -eq 0.0)
    noIdleTimer      = ($null -ne $idleVisual) -and (-not [bool]$idleVisual.runningTimerEnabled) -and ($null -eq $idleVisual.running)
    regressionChain  = ($chainExit -eq 0) -and ($chainReports['task15'] -eq $true) -and ($chainReports['task16b'] -eq $true)
    zeroLiveCalls    = ($liveCalls.Count -eq 0) -and ($liveMissing.Count -eq 0)
    gitUnchanged     = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45,0; HUD {1}) | idle CPU {2} % ({3}) | Verify-Task15 chain exit {4} in {5} s (task15 allPassed={6}, task16b allPassed={7}) | live calls in chain: {8} (audited {9}; missing [{10}]) | git same={11} {12} | CI: {13} | failed=[{14}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $chainExit, $chainSeconds, $chainReports['task15'], $chainReports['task16b'],
    $liveCalls.Count, (@($liveAudit | Where-Object { $_.ranInChain } | ForEach-Object { $_.report }) -join ','), (@($liveMissing | ForEach-Object { $_.report }) -join ','),
    $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-16c'; gate = 'Gate 16c'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; adr68 = $adr68; fontFiles = $fontFiles; animationHits = $animationHits; selftest = $vData }
    E2_LayoutCancel = [ordered]@{ pass = $e2Pass; checks = $e2Checks; ask = $askRes; running = $runningUi; cancel = $cancelRes; cancelPipeMs = $cancelPipeMs; afterLateWindow = $afterUi; half = $hv; collapsed = $(if ($col) { [ordered]@{ height = $col.height; visual = $col.visual } } else { $null }); calc = $calc; local = $lv.thread }
    E3_PaletteLazyImageBoard = [ordered]@{ pass = $e3Checks.Values -notcontains $false; checks = $e3Checks; slash = $pAll.palette; ca = $pCa.palette; caTab = $pCaTab.box; agents = $pSec.palette; agentTab = $pSecTab.box; registryCount = $registryCount
        lazy = $lazyUi; lazyAfter = $lazyAfter; lazyCopied = $lazyCopy.lastCopied; imageCardVisible = $imageVisible; imageAsk = $imageAsk; imageAfter = $(if ($imageAfter) { $imageAfter.visual.lastImageAsk } else { $null }); imageNote = $imageNote
        f1 = $f1Ui; board = $boardOn.board; altG = $gem.result; altK = $prm.result }
    E4_Wake = [ordered]@{ pass = $e4Pass; checks = $e4Checks; coldFirstWake = $coldFirstWake; coldFirstWakeGraded = $false; settlePauseMs = 50; warmup = $warmup; warmupLimitMs = 60.0; n = 10; samples = $wakes; p50 = $wakeP50; p95 = $wakeP95; historyRows = @($hist.rows).Count; testRows = $testRows.Count }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; chainExit = $chainExit; chainSeconds = $chainSeconds; chainReports = $chainReports; liveAudit = $liveAudit; liveCallsInChain = $liveCalls.Count; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
$null = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 16c'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task16c: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; Hủy {5} ms, ColdFirstWake {6} ms, Warmup {7} ms, WAKE P50 {8} / P95 {9} ms, RAM {10} MB, live calls {11}" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $cancelPipeMs, $coldFirstWake.ms, $warmup.ms, $wakeP50, $wakeP95, $idleMb, $liveCalls.Count) } })
Write-Host "`nGate 16c verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
