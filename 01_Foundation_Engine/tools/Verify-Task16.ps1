#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 16 automated verification (TASK-16, ADR-65: Cockpit UI v2 Stage 1 B1-B6 + U1, G272 YouTube /
    Samsung K450 Equalizer APO auto-switch U2, HUAWEI WATCH GT 6 webhook U3).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), repository-wide Zero-BOM gate (.ps1 with BOM, everything else without),
        selftest ui-v2 (headless, loopback only), CI checked in E5.
    E2  B1-B6 on a REAL HostService (no Gemini mock, so the test-record filter is on): an isTest / mock snapshot is ignored by
        ACTIVE SQUAD, RECENT DISPATCHES shows no test row; one Private WS text for the badge and the LEFT MFD, no "Working Set";
        AI answer area + 1-line status bar (no scrolling log list); F1 cheat sheet only through the focused window (no
        RegisterHotKey); "Câu trả lời hiện tại: X thẻ"; the window stays inside the G272 work area.
    E3  U1: 40 / 330 / 640 px through the pipe (same code path as /ui, the grip snap and double-click), toggle, cockpit_ui.json
        saved + restored after a HostService restart (level and position; an off-screen position falls back inside the G272),
        auto-collapse after the idle delay in PASSIVE (NEXUS_UI_AUTOCOLLAPSE_MS = 3000 for the test) and re-open on a new answer,
        click-through (opacity 0.6 + WS_EX_TRANSPARENT | WS_EX_LAYERED, clicks reach the window behind), Ctrl+Shift+Space path
        (COMMANDMODE) = clickable, 100 %, Half shown, input focused.
    E4  U2 with a simulated browser title in a TEMP Equalizer APO folder (never Program Files): one config.txt switch per state
        change, BOM/CRLF kept, HUD collapses in PASSIVE and comes back; 5 WinEvent hooks, no timer. U3: /watch test and a
        /timer expiry against a 127.0.0.1 listener that never answers: the pipe call returns at once, the POST ends at the 3 s
        timeout, the HUD keeps answering while it waits; /watch never shows the URL path.
    E5  Settled idle Private WS (median of 8 reads, trim >= 10 s old) <= 45.0 MB (Phase 3 gate 52.0), idle CPU < 0.5 %
        (target 0 %), Invoke-NexusRegression -FastRegression, git guard, CI.
    No Gemini call, no token. The user's latest_dispatch.json and cockpit_ui.json are restored, the HostService is restarted
    without the test variables at the end. Writes 03_Local_Data_Vault/handoff/task16_verification.json (UTF-8 without BOM).
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
$UiPath     = Join-Path $VaultRoot 'config\cockpit_ui.json'
$LatestPath = Join-Path $HandoffDir 'latest_dispatch.json'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$ReportPath = Join-Path $HandoffDir 'task16_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$EqDir      = Join-Path $env:TEMP 'nexus_verify16_eq'   # fixed path, overwritten each run (never Program Files)
$AutoCollapseMs = 3000
$RunId      = Get-Date -Format 'HHmmss'
$YouTubeTitle = "Lofi Girl - YouTube — Google Chrome ($RunId)"
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$testVars = 'NEXUS_EQ_CONFIG_DIR', 'NEXUS_UI_AUTOCOLLAPSE_MS', 'NEXUS_WATCH_WEBHOOK_URL'
$envNames = @('NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES', 'NEXUS_RELAXED_SLA',
              'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE') + $testVars
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
function Send-CockpitCommand([string]$text) { return Send-PipeJson 'COCKPIT' $text }
function Get-UiState { return Send-PipeJson 'UISTATE' $null }
function Set-Ui([string]$action) { return Send-PipeJson 'UI' $action }
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task16.ps1.'
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
<# The TASK-16 test variables live only in the HostService environment: set them, start it, remove them again here. #>
function Restart-Host([bool]$testEnv) {
    Stop-HostService
    if ($testEnv) {
        $env:NEXUS_EQ_CONFIG_DIR = $EqDir
        $env:NEXUS_UI_AUTOCOLLAPSE_MS = [string]$AutoCollapseMs
        $env:NEXUS_WATCH_WEBHOOK_URL = "http://127.0.0.1:$script:WebhookPort/nexus-verify16"
    }
    try { $p = Start-HostIfNeeded }
    finally { foreach ($v in $testVars) { if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue } } }
    # A real YouTube tab on the G272 must not fold the HUD in the middle of E2 / E3: simulate "no YouTube" until E4.
    if ($testEnv) { try { $null = Send-PipeJson 'YOUTUBE' 'off' } catch { } }
    return $p
}
function Invoke-SelfTest([string]$scenario) {
    $path = Join-Path $HandoffDir "task16_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $($p.ExitCode))" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    return [ordered]@{ exitCode = $p.ExitCode; noBom = -not (Test-HasUtf8Bom $path); data = $data }
}
function Get-FailedNames($checks) { return (($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key }) -join ',') }
function Wait-Ui([scriptblock]$condition, [int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        $s = Get-UiState
        if (& $condition $s) { return $s }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $s
}
function Test-LooksLikeTest([string]$aiModel, [string]$task) {
    return ($aiModel -match '(?i)mock') -or ($task -match '\(Gate [^)]*E[^)]*\)')
}
# dispatchId must be > 0: LatestDispatchFile.TryRead drops a non-local snapshot with dispatchId <= 0 (attempt 1 wrote 0, so E2 B1 / E3 reopen never saw it).
$script:FakeDispatchId = 900000000L
function Write-Latest([string]$model, [string]$task, [string]$answer, [bool]$isTest) {
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $script:FakeDispatchId++
    $o = [ordered]@{ event = 'ai_response'; source = 'Verify-Task16'; dispatchId = $script:FakeDispatchId; status = 'PASSED'; retryCount = 0; evidenceNotes = ''
        timestampUtc = $now; updatedAtUtc = $now; taskSummary = $task; nexusMode = 'Micro'; squadId = 'Squad1'; squadName = 'Verify16 squad'
        primaryAgent = [ordered]@{ id = 'verify'; name = 'Verify16 Agent' }; qaAgent = [ordered]@{ id = '-'; name = '-' }; realityChecker = 'testing-reality-checker'
        aiResponse = $answer; aiModel = $model; turnCount = 1 }
    if ($isTest) { $o['isTest'] = $true }
    [System.IO.File]::WriteAllText($LatestPath, ($o | ConvertTo-Json -Depth 5), $Utf8NoBom)
}
function Get-FileMs([string]$path) { return (Get-Item $path).LastWriteTimeUtc.Ticks }

# Window under a screen point (physical pixels: the script asks for per-monitor DPI awareness first).
$script:HitApi = $false
try {
    Add-Type -Namespace NexusGate16 -Name User32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
public static uint ProcessAt(int x, int y) { POINT p; p.X = x; p.Y = y; uint pid; GetWindowThreadProcessId(WindowFromPoint(p), out pid); return pid; }
'@
    $script:HitApi = [NexusGate16.User32]::SetProcessDpiAwarenessContext([IntPtr](-4))
} catch { $script:HitApi = $false }
function Get-ProcessAtHudCentre($ui) {
    if (-not $script:HitApi -or $null -eq $ui.rectPx) { return $null }
    $x = [int]$ui.rectPx.x + [int]([int]$ui.rectPx.w / 2); $y = [int]$ui.rectPx.y + [int]([int]$ui.rectPx.h / 2)
    return [int][NexusGate16.User32]::ProcessAt($x, $y)
}

# Fake Equalizer APO folder: config.txt (UTF-8 BOM, CRLF) + the two K450 profiles.
function Reset-EqFolder {
    if (-not (Test-Path $EqDir)) { New-Item -ItemType Directory -Path $EqDir | Out-Null }
    $bomUtf8 = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText((Join-Path $EqDir 'config.txt'), "Device: Samsung K450 (Verify16)`r`nPreamp: -3 dB`r`nInclude: nexus_k450_music.txt`r`nGraphicEQ: 25 0; 40 1`r`n", $bomUtf8)
    [System.IO.File]::WriteAllText((Join-Path $EqDir 'nexus_k450_youtube.txt'), "Preamp: -2 dB`r`n", $Utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $EqDir 'nexus_k450_music.txt'), "Preamp: -4 dB`r`n", $Utf8NoBom)
    $backup = Join-Path $EqDir 'config.txt.nexus-backup'
    if (Test-Path $backup) { [System.IO.File]::WriteAllText($backup, '', $Utf8NoBom) }
}
function Get-EqInclude {
    $line = @(Get-Content (Join-Path $EqDir 'config.txt') -Encoding UTF8 | Where-Object { $_ -match '^\s*Include:\s*nexus_k450_' })
    if ($line.Count -gt 0) { return $line[0].Trim() } else { return $null }
}

$gitStart = Get-NexusGitState $RepoRoot
Write-Host ("Git guard: branch {0} @ {1}" -f $gitStart.Branch, $gitStart.Head) -ForegroundColor Cyan
$latestBackup = Save-NexusLatestDispatch $HandoffDir
$uiBackup = if (Test-Path $UiPath) { [System.IO.File]::ReadAllBytes($UiPath) } else { $null }
# A loopback listener that accepts the TCP connection (OS backlog) and never answers: the webhook POST must give up at 3 s.
$script:Webhook = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$script:Webhook.Start()
$script:WebhookPort = ([System.Net.IPEndPoint]$script:Webhook.LocalEndpoint).Port
$script:Restored = $false
function Restore-UserState {
    if ($script:Restored) { return }
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    else { [System.IO.File]::WriteAllText($UiPath, '{ "heightLevel": "half", "leftPx": null, "topPx": null, "autoCollapse": true, "clickThrough": false }', $Utf8NoBom) }
    $script:Restored = $true
}

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, selftest ui-v2'
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
$uSelf = Invoke-SelfTest 'ui-v2'
$uData = $uSelf.data
$e1Checks = [ordered]@{
    build    = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom  = [bool]$bom.Pass
    selftest = ($uSelf.exitCode -eq 0) -and $uSelf.noBom -and [bool]$uData.pass
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | selftest ui-v2 pass={5} failed=[{6}] | failed=[{7}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $uData.pass, (@($uData.failed) -join ','), (Get-FailedNames $e1Checks))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: B1-B6 on a real HostService (test records hidden, one Private WS, answer + status line, F1, card label, G272 anchor)'
Reset-EqFolder
[System.IO.File]::WriteAllText($UiPath, '{ "heightLevel": "half", "leftPx": null, "topPx": null, "autoCollapse": true, "clickThrough": false }', $Utf8NoBom)
$hostProc = Restart-Host $true
$null = Send-PipeLine '{"cmd":"HIDE"}'
$null = Send-PipeLine '{"cmd":"TOGGLE"}'          # Alt+Space path: Passive HUD
Start-Sleep -Milliseconds 400
$ui0 = Get-UiState
# B1: a test snapshot (isTest + mock model + Gate tag) is ignored by the real Cockpit
$titleBefore = [string]$ui0.dispatchCardTitle
Write-Latest 'gemini-mock' "Verify16 (Gate 16 E2) test row $RunId" "Không được hiện $RunId" $true
$uiTest = Wait-Ui { param($s) [long]$s.hiddenTestSnapshots -gt [long]$ui0.hiddenTestSnapshots } 3000
# B5 + B3: a normal answer with a code block and a checklist
$answer = "Verify16 answer $RunId`n``````powershell`nGet-Process | Select-Object -First 1`n```````n- [ ] mục một`n- [ ] mục hai"
Write-Latest 'verify16' "Verify16 thẻ trả lời $RunId" $answer $false
$uiAns = Wait-Ui { param($s) ([string]$s.dispatchCardTitle) -ne $titleBefore -and [bool]$s.aiVisible } 3000
$null = Send-PipeLine '{"cmd":"COMMANDMODE"}'
$f1On = Set-Ui 'keys'
$f1Off = Set-Ui 'keys'
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$ui2 = Get-UiState
$srcCs = Get-Content (Join-Path $EngineRoot 'NEXUSHome.CockpitUI\CockpitShellWindow.xaml.cs') -Raw -Encoding UTF8
$srcXaml = Get-Content (Join-Path $EngineRoot 'NEXUSHome.CockpitUI\CockpitShellWindow.xaml') -Raw -Encoding UTF8
$srcHotkeys = Get-Content (Join-Path $EngineRoot 'NEXUSHome.HostService\AulaHero68HotkeyListener.cs') -Raw -Encoding UTF8
$recentTest = @(@($ui2.recentShown) | Where-Object { Test-LooksLikeTest ([string]$_.aiModel) ([string]$_.task) })
$e2Checks = [ordered]@{
    b1SnapshotHidden   = ([long]$uiTest.hiddenTestSnapshots -gt [long]$ui0.hiddenTestSnapshots) -and ([string]$uiTest.dispatchCardTitle -eq $titleBefore) -and [bool]$uiTest.hideTestRecords
    b1RecentClean      = ($recentTest.Count -eq 0)
    b2OneFigure        = [bool]$ui2.badgeMatchesMfd -and ([string]$ui2.leftMfdMemory).StartsWith('Private WS:') -and -not [bool]$ui2.leftMfdHasWorkingSet
    b2NoWorkingSetCode = -not ($srcCs -match 'WorkingSet64|Working Set :')
    b3AnswerStatusLine = [bool]$uiAns.aiVisible -and [bool]$ui2.statusLineVisible -and ([string]$ui2.statusLine).Length -gt 0 -and -not ($srcXaml -match 'VirtualizedOutputList')
    b4F1               = [bool]$f1On.cheatSheetVisible -and -not [bool]$f1Off.cheatSheetVisible -and -not ($srcHotkeys -match 'VK_F1|0x70\b') -and ($srcCs -match 'e\.Key == Key\.F1')
    b4Tooltips         = ($srcXaml -match 'TextTrimming" Value="CharacterEllipsis"') -and ($srcXaml -match 'RelativeSource Self')
    b5CardLabel        = ([string]$uiAns.cardLabel -match '^Câu trả lời hiện tại: \d+ thẻ$') -and ([int]$uiAns.cardCount -ge 1)
    b6InsideTarget     = [bool]$ui2.insideTarget
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("B1 hidden snapshots {0} -> {1}, card '{2}', recent shown {3} (test rows filtered {4}) | B2 badge/MFD '{5}' | B3 status '{6}' | B4 F1 {7}/{8} | B5 '{9}' | B6 monitor {10} ({11}) rect {12},{13} {14}x{15} inside={16} | failed=[{17}]" -f `
    $ui0.hiddenTestSnapshots, $uiTest.hiddenTestSnapshots, $uiTest.dispatchCardTitle, @($ui2.recentShown).Count, $ui2.recentFilteredOut,
    $ui2.leftMfdMemory, $ui2.statusLine, $f1On.cheatSheetVisible, $f1Off.cheatSheetVisible, $uiAns.cardLabel,
    $ui2.monitor, $ui2.monitorMatchedBy, $ui2.rectPx.x, $ui2.rectPx.y, $ui2.rectPx.w, $ui2.rectPx.h, $ui2.insideTarget, (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: U1 snap heights, persistence, auto-collapse, click-through, Ctrl+Shift+Space path'
$null = Set-Ui 'tuthu off'
$lv = [ordered]@{}
foreach ($l in 'collapsed', 'full', 'half') { $lv[$l] = Set-Ui $l }
$tog1 = Set-Ui 'toggle'; $tog2 = Set-Ui 'toggle'
function Test-Level($s, [string]$level) {
    return ([string]$s.level -eq $level) -and ([math]::Abs([double]$s.height - [double]$s.expectedHeight) -lt 1.5) -and
           ([string]$s.saved.HeightLevel -eq $level) -and ([bool]$s.mainPanesVisible -eq ($level -ne 'collapsed')) -and [bool]$s.insideTarget
}
$heightsOk = (Test-Level $lv['collapsed'] 'collapsed') -and ([double]$lv['collapsed'].height -eq 40) -and
             (Test-Level $lv['full'] 'full') -and ([double]$lv['full'].expectedHeight -eq 640) -and
             (Test-Level $lv['half'] 'half') -and ([double]$lv['half'].height -eq 330)
$toggleOk = ([string]$tog1.level -eq 'collapsed') -and ([string]$tog2.level -eq 'half')
# Persistence: a saved position inside the work area is restored after a restart; an off-screen one falls back inside.
$wa = $lv['half'].workAreaPx
$wantX = [int]$wa.x + 40; $wantY = [int]$wa.y + 60
$null = Set-Ui 'full'
[System.IO.File]::WriteAllText($UiPath, ('{{ "heightLevel": "full", "leftPx": {0}, "topPx": {1}, "autoCollapse": false, "clickThrough": false }}' -f $wantX, $wantY), $Utf8NoBom)
$hostProc = Restart-Host $true
$null = Send-PipeLine '{"cmd":"TOGGLE"}'
Start-Sleep -Milliseconds 400
$afterRestart = Get-UiState
[System.IO.File]::WriteAllText($UiPath, '{ "heightLevel": "half", "leftPx": -30000, "topPx": -30000, "autoCollapse": true, "clickThrough": false }', $Utf8NoBom)
$hostProc = Restart-Host $true
$null = Send-PipeLine '{"cmd":"TOGGLE"}'
Start-Sleep -Milliseconds 400
$offScreen = Get-UiState
$persistOk = ([string]$afterRestart.level -eq 'full') -and ([int]$afterRestart.rectPx.x -eq $wantX) -and ([int]$afterRestart.rectPx.y -eq $wantY) -and
             -not [bool]$afterRestart.autoCollapse -and ([string]$offScreen.level -eq 'half') -and [bool]$offScreen.insideTarget
# Auto-collapse (PASSIVE, idle 3 s in this test host), then a new answer re-opens it; off = stays
$t0 = Get-Date
$auto = Wait-Ui { param($s) [string]$s.level -eq 'collapsed' } ($AutoCollapseMs + 4000)
$autoSec = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
Write-Latest 'verify16' "Verify16 câu trả lời mới $RunId" "Câu trả lời mới $RunId" $false
$reopen = Wait-Ui { param($s) [string]$s.level -eq 'half' } 3000
$null = Set-Ui 'tuthu off'
Start-Sleep -Milliseconds ($AutoCollapseMs + 1500)
$stayed = Get-UiState
$null = Set-Ui 'tuthu on'
$autoOk = ([string]$auto.level -eq 'collapsed') -and ([string]$auto.tempReason -eq 'auto') -and ([string]$auto.savedLevel -eq 'half') -and ($autoSec -le ($AutoCollapseMs / 1000 + 3)) -and
          ([string]$reopen.level -eq 'half') -and ([string]$stayed.level -eq 'half') -and -not [bool]$stayed.autoCollapseArmed
# Click-through + Ctrl+Shift+Space (COMMANDMODE = the same QuickAgentPtt handler)
$null = Set-Ui 'tuthu off'
$ctOn = Set-Ui 'xuyen on'
$hitOn = Get-ProcessAtHudCentre $ctOn
$null = Send-PipeLine '{"cmd":"COMMANDMODE"}'
Start-Sleep -Milliseconds 300
$cmd = Get-UiState
$hitCmd = Get-ProcessAtHudCentre $cmd
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
Start-Sleep -Milliseconds 200
$back = Get-UiState
$ctOff = Set-Ui 'xuyen off'
$hitOff = Get-ProcessAtHudCentre $ctOff
$clickOk = [bool]$ctOn.transparent -and [bool]$ctOn.layered -and ([math]::Abs([double]$ctOn.opacity - 0.6) -lt 0.01) -and
           -not [bool]$cmd.transparent -and ([double]$cmd.opacity -eq 1) -and [bool]$cmd.commandMode -and
           [bool]$back.transparent -and ([math]::Abs([double]$back.opacity - 0.6) -lt 0.01) -and
           -not [bool]$ctOff.transparent -and ([double]$ctOff.opacity -eq 1) -and -not [bool]$ctOff.saved.ClickThrough
$hitOk = (-not $script:HitApi) -or (($hitOn -ne $hostProc.Id) -and ($hitCmd -eq $hostProc.Id) -and ($hitOff -eq $hostProc.Id))
# Collapsed + Ctrl+Shift+Space: Half is shown with the input focused; back in PASSIVE it folds again after the idle delay
$null = Set-Ui 'collapsed'
$null = Send-PipeLine '{"cmd":"COMMANDMODE"}'
Start-Sleep -Milliseconds 300
$fromCollapsed = Get-UiState
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$refold = Wait-Ui { param($s) [string]$s.level -eq 'collapsed' } ($AutoCollapseMs + 4000)
$commandOk = ([string]$fromCollapsed.level -eq 'half') -and ([string]$fromCollapsed.tempReason -eq 'command') -and [bool]$fromCollapsed.mainPanesVisible -and
             [bool]$fromCollapsed.inputFocused -and ([string]$refold.level -eq 'collapsed')
$null = Set-Ui 'half'; $null = Set-Ui 'tuthu on'
$e3Checks = [ordered]@{
    selftestU1   = [bool]$uData.checks.'u1.snap' -and [bool]$uData.checks.'u1.heights' -and [bool]$uData.checks.'u1.toggle' -and [bool]$uData.checks.'u1.parser' -and [bool]$uData.checks.'u1.store'
    heights      = $heightsOk
    toggle       = $toggleOk
    persistence  = $persistOk
    autoCollapse = $autoOk
    clickThrough = $clickOk
    clickReaches = $hitOk
    ctrlShiftSpace = $commandOk
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("heights 40/{0}/{1} | toggle {2}->{3} | restart: {4} at {5},{6} (want {7},{8}), off-screen -> {9} inside={10} | auto-collapse after {11} s ({12}), new answer -> {13}, off -> {14} | click-through opacity {15} transparent={16}, Ctrl+Shift+Space opacity {17} transparent={18}, back {19}, off {20} | hit-test pid on/cmd/off {21}/{22}/{23} (host {24}, api={25}) | collapsed+cmd -> {26} focused={27}, refold {28} | failed=[{29}]" -f `
    $lv['half'].height, $lv['full'].height, $tog1.level, $tog2.level, $afterRestart.level, $afterRestart.rectPx.x, $afterRestart.rectPx.y, $wantX, $wantY,
    $offScreen.level, $offScreen.insideTarget, $autoSec, $auto.tempReason, $reopen.level, $stayed.level, $ctOn.opacity, $ctOn.transparent, $cmd.opacity, $cmd.transparent,
    $back.transparent, $ctOff.transparent, $hitOn, $hitCmd, $hitOff, $hostProc.Id, $script:HitApi, $fromCollapsed.level, $fromCollapsed.inputFocused, $refold.level, (Get-FailedNames $e3Checks))

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: U2 YouTube on the G272 -> K450 EQ (temp folder), U3 webhook does not block'
Reset-EqFolder
$cfgPath = Join-Path $EqDir 'config.txt'
$null = Set-Ui 'half'
$before = Get-UiState
$yt1 = Send-PipeJson 'YOUTUBE' $YouTubeTitle
$incYoutube = Get-EqInclude
$stamp1 = Get-FileMs $cfgPath
$yt2 = Send-PipeJson 'YOUTUBE' $YouTubeTitle
$stamp2 = Get-FileMs $cfgPath
$ytOff = Send-PipeJson 'YOUTUBE' 'off'
$incMusic = Get-EqInclude
$cfgBytes = [System.IO.File]::ReadAllBytes($cfgPath)
$cfgText = [System.IO.File]::ReadAllText($cfgPath)
$ytReal = Send-PipeJson 'YOUTUBE' 'real'
$u2Ok = [bool]$yt1.watching -and [bool]$yt1.lastSwitch.Ok -and [bool]$yt1.lastSwitch.Changed -and ($incYoutube -eq 'Include: nexus_k450_youtube.txt') -and
        ([string]$yt1.ui.level -eq 'collapsed') -and ([string]$yt1.ui.tempReason -eq 'youtube') -and
        ($stamp2 -eq $stamp1) -and ([string]$yt2.ui.youtube.lastSwitch.AtUtc -eq [string]$yt1.lastSwitch.AtUtc) -and
        -not [bool]$ytOff.watching -and ($incMusic -eq 'Include: nexus_k450_music.txt') -and ([string]$ytOff.ui.level -eq 'half') -and
        ($cfgBytes[0] -eq 0xEF) -and ($cfgText -match "Preamp: -3 dB`r`nInclude: nexus_k450_music.txt`r`nGraphicEQ")
$eventDriven = ([int]$before.youtube.hooks -eq 5) -and [bool]$before.youtube.active
# U3: /watch test and a /timer expiry against the silent listener
$watchStatus = Send-CockpitCommand '/watch'
$sentBefore = [long](Get-UiState).webhook.sentCount
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$watchTest = Send-CockpitCommand '/watch test'
$watchRoundTripMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
$sw.Restart(); $during = Get-UiState; $duringMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
$afterTest = Wait-Ui { param($s) [long]$s.webhook.sentCount -gt $sentBefore } 6000
$timerCmd = Send-CockpitCommand "/timer 1s Verify16 webhook $RunId"
$afterTimer = Wait-Ui { param($s) [long]$s.webhook.sentCount -gt ([long]$afterTest.webhook.sentCount) } 8000
$hook = $afterTest.webhook.lastResult
$hookTimer = $afterTimer.webhook.lastResult
$u3Ok = [bool]$watchTest.ok -and ([double]$watchTest.elapsedMs -lt 50) -and ($watchRoundTripMs -lt 500) -and ($duringMs -lt 500) -and
        ($null -ne $hook) -and ([string]$hook.Event -eq 'test') -and ([string]$hook.Error).StartsWith('timeout') -and ([double]$hook.ElapsedMs -ge 2800) -and ([double]$hook.ElapsedMs -le 4500) -and
        ($null -ne $hookTimer) -and ([string]$hookTimer.Event -eq 'timer_expired') -and ([string]$hookTimer.Host -eq '127.0.0.1') -and
        -not ([string]$watchStatus.message).Contains('nexus-verify16') -and -not ([string]$watchTest.message).Contains('nexus-verify16')
$e4Checks = [ordered]@{
    selftestU2U3 = (@($uData.checks.PSObject.Properties | Where-Object { ($_.Name -like 'u2.*' -or $_.Name -like 'u3.*') -and -not $_.Value }).Count -eq 0)
    eqSwitchLive = $u2Ok
    eventDriven  = $eventDriven
    webhookLive  = $u3Ok
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("YouTube -> '{0}' HUD {1} | same title: write {2} | leave -> '{3}' HUD {4} | real detection pass {5} ms | hooks {6}, events {7}/{8} accepted | /watch test {9} ms (pipe {10} ms, UISTATE during wait {11} ms) -> {12} {13} ms | timer -> {14} {15} | failed=[{16}]" -f `
    $incYoutube, $yt1.ui.level, $(if ($stamp2 -eq $stamp1) { 'none' } else { 'again!' }), $incMusic, $ytOff.ui.level, $ytReal.check.ElapsedMs,
    $before.youtube.hooks, $before.youtube.eventsAccepted, $before.youtube.eventsReceived, $watchTest.elapsedMs, $watchRoundTripMs, $duringMs,
    $hook.Error, $hook.ElapsedMs, $hookTimer.Event, $hookTimer.Error, (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45.0 MB (gate 52.0), idle CPU 0 % (< 0.5), fast regression, git, CI'
$null = Set-Ui 'half'
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 8000
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
$memJson = $null; try { $memJson = Send-PipeJson 'MEM' $null } catch { }
Restore-UserState
$null = Restart-Host $false   # the regression chain and the user get a HostService without the TASK-16 test variables
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
        -Headers @{ 'User-Agent' = 'nexus-verify-task16'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable or repository private: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45 = ($null -ne $idleMb) -and ($idleMb -le 45.0)   # target (Phase 3 hard gate 52.0)
    idleCpu          = ($idleCpu -lt 0.5)
    fastRegression   = ($fastExit -eq 0) -and ($null -ne $fastSummary) -and [bool]$fastSummary.pass -and ([double]$fastSummary.seconds -lt 90) -and ($null -ne $summaryLine)
    gitUnchanged     = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45.0; gate 52.0; HUD {1}) | idle CPU {2} % (5 last of {3}) | fast {4} s, {5} | git same={6} {7} | CI: {8} | failed=[{9}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $fastSummary.seconds, $summaryLine, $gitEnd.Same, ($gitEnd.Reasons -join '; '),
    $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))
if (-not ($ci.checked -and $ci.conclusion -eq 'success')) { Write-Host '  Confirm the green CI run in the GitHub Actions tab.' -ForegroundColor Yellow }

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-16'; gate = 'Gate 16'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; selftest = $uData }
    E2_B1toB6 = [ordered]@{ pass = $e2Pass; checks = $e2Checks; initial = $ui0; afterTestSnapshot = $uiTest; afterAnswer = $uiAns; f1On = $f1On.cheatSheetVisible; f1Off = $f1Off.cheatSheetVisible; state = $ui2 }
    E3_U1 = [ordered]@{ pass = $e3Pass; checks = $e3Checks; levels = $lv; toggle = @($tog1.level, $tog2.level); afterRestart = $afterRestart; offScreen = $offScreen; autoCollapse = $auto; autoCollapseSec = $autoSec; reopen = $reopen; stayed = $stayed; clickThroughOn = $ctOn; commandMode = $cmd; backToPassive = $back; clickThroughOff = $ctOff; hitTest = [ordered]@{ api = $script:HitApi; on = $hitOn; command = $hitCmd; off = $hitOff; host = $hostProc.Id }; fromCollapsed = $fromCollapsed; refold = $refold }
    E4_U2U3 = [ordered]@{ pass = $e4Pass; checks = $e4Checks; eqDir = $EqDir; youtube = $yt1; sameTitle = $yt2; leave = $ytOff; real = $ytReal; includeYoutube = $incYoutube; includeMusic = $incMusic; watchStatus = $watchStatus.message; watchTest = $watchTest; watchRoundTripMs = $watchRoundTripMs; uiStateDuringWaitMs = $duringMs; webhookTest = $hook; webhookTimer = $hookTimer; timer = $timerCmd.message }
    E5_MemoryCpuRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; fast = $fastSummary; summaryLine = $summaryLine; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
Write-Host "`nGate 16 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    try { $script:Webhook.Stop() } catch { }
    foreach ($v in $testVars) { if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue } }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
