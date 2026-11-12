#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 16b automated verification (TASK-16b, ADR-66: Cockpit UI v2 Phase 2 - CUI-Opaque, CUI-Markdown,
    CUI-Layout, CUI-Lazy, CUI-Alliance, MCP notify_shift_complete, Gate 16 debt WAKE 40 -> 330 px).
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate (.ps1 with BOM, everything else without), selftest ui-v2b.
    E2  CUI-Opaque: HUD backdrop alpha >= 0xF8 at 330 and 640 px, click-through still 60 %. CUI-Markdown: selftest (counts,
        edge cases, 4 KB parse P95 < 1.0 ms) + a live answer rendered with the expected bold / code / block / bullet counts.
    E3  CUI-Layout: Q&A history from SQLite (real rows only, no test row), every view < 2.0 ms (selftest on a temp database too).
        CUI-Lazy / CUI-Alliance: Alt+Enter copies the next alliance step to the clipboard, Alt+A and F1 toggle their panels,
        number key 1 copies the code block. MCP notify_shift_complete reaches the Cockpit (apply < 2.0 ms, badge lit).
        Clipboard image hint once per new image (STA PowerShell only).
    E4  Gate 16 debt: 10 x Ctrl+Shift+Space (pipe COMMANDMODE = same handler) from 40 px: 330 px + input focused,
        WAKE P50 <= 10.0 ms, P95 <= 16.0 ms.
    E5  Settled idle Private WS <= 45.0 MB (hard 52.0), idle CPU 0.0 %, regression chain: Verify-Task16 -> Verify-Task13 ->
        Verify-Task12 -> Verify-Task13a -> Invoke-NexusRegression -FullRegression (Task14 -> Task11 -> ... -> Task03, -SkipLive
        unless -Live), git guard, CI. -FastE5 = Task16 -> Task13 -> -FastRegression (development loop only).
    In-app shortcuts are driven through the pipe commands LAZY / ALLIANCE / HISTORY, which call exactly the handlers the
    keys call (keystroke injection into a focused window is not reliable from a script). No Gemini call (number keys 3 / 4
    are not pressed), 0 tokens. The user's latest_dispatch.json, cockpit_ui.json and clipboard text are restored; at the end
    the script reports its own result through MCP notify_shift_complete (that is the shift_state.json left behind).
    Writes 03_Local_Data_Vault/handoff/task16b_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5,   # development loop: -FastRegression instead of the full chain
    [switch]$Live      # let the full chain make its real Gemini calls (Task07/09/10/11 E5); default -SkipLive (0 live calls since Gate 15)
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
$ReportPath = Join-Path $HandoffDir 'task16b_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$RunId      = Get-Date -Format 'HHmmss'
$CodeLine1  = 'var p = Process.GetCurrentProcess();'
$CodeLine2  = 'p.PriorityClass = ProcessPriorityClass.High;'
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$envNames = 'NEXUS_GEMINI_MOCK_RESPONSE', 'NEXUS_GEMINI_QA_MOCK_VERDICT', 'NEXUS_GEMINI_SIMULATE_TRANSIENT_FAILURES', 'NEXUS_RELAXED_SLA',
            'NEXUS_SIMULATE_COMBAT_MODE', 'NEXUS_SIMULATE_WINDOW_TITLE', 'NEXUS_UI_AUTOCOLLAPSE_MS'
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
# Gate 16c run 3 (same fix as Verify-Task16c): this script puts a test image on the clipboard, so this thread becomes the clipboard
# owner. A plain ReadLine never pumps messages; if the Cockpit then writes the clipboard, EmptyClipboard waits for this thread to
# answer WM_DESTROYCLIPBOARD and both sides wait for ever. The reply is awaited with Task.Wait (pumps on this STA thread), and a
# pipe command that does not answer in 30 s fails the run with its name instead of hanging it.
$script:PipeReplyTimeoutMs = 30000
function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($script:PipeReplyTimeoutMs)) {
            throw ("HostService did not answer the pipe command {0} within {1} s (UI thread busy or hung)" -f $line, ($script:PipeReplyTimeoutMs / 1000))
        }
        return $task.Result
    } finally { $client.Dispose() }
}
function Send-PipeRaw([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    return [string](Send-PipeLine ($req | ConvertTo-Json -Compress) | ConvertFrom-Json).result
}
function Send-PipeJson([string]$cmd, [string]$text) { return ((Send-PipeRaw $cmd $text) | ConvertFrom-Json) }
function Get-UiState { return Send-PipeJson 'UISTATE' $null }
function Set-Ui([string]$action) { return Send-PipeJson 'UI' $action }
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task16b.ps1.'
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
function Restart-Host {
    Stop-HostService
    return Start-HostIfNeeded
}
function Invoke-SelfTest([string]$scenario, [string]$prefix) {
    $path = Join-Path $HandoffDir "${prefix}_selftest_$scenario.json"
    if (Test-Path $path) { Remove-Item $path -Force }
    $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', $scenario) -PassThru -Wait
    if (-not (Test-Path $path)) { throw "Selftest $scenario did not write $path (exit $($p.ExitCode))" }
    $data = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($w in @($data.slaWarnings)) {
        if ($null -ne $w) { Write-Host ("[SLA-WARN (FastRegression jitter)] {0}: {1} = {2} ms (SLA {3} ms)" -f $scenario, $w.metric, $w.measuredMs, $w.slaMs) -ForegroundColor Yellow }
    }
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
function Test-LooksLikeTest([string]$aiModel, [string]$task) {
    return ($aiModel -match '(?i)mock') -or ($task -match '\(Gate [^)]*E[^)]*\)')
}
# dispatchId must be > 0: LatestDispatchFile.TryRead drops a non-local snapshot with dispatchId <= 0 (Gate 16 lesson, c1d3a3d).
$script:FakeDispatchId = 910000000L
function Write-Latest([string]$model, [string]$task, [string]$answer) {
    $script:FakeDispatchId++
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $o = [ordered]@{ event = 'ai_response'; source = 'Verify-Task16b'; dispatchId = $script:FakeDispatchId; status = 'PASSED'; retryCount = 0; evidenceNotes = ''
        timestampUtc = $now; updatedAtUtc = $now; taskSummary = $task; nexusMode = 'Micro'; squadId = 'Squad1'; squadName = 'Verify16b squad'
        primaryAgent = [ordered]@{ id = 'verify'; name = 'Verify16b Agent' }; qaAgent = [ordered]@{ id = '-'; name = '-' }; realityChecker = 'testing-reality-checker'
        aiResponse = $answer; aiModel = $model; turnCount = 1 }
    [System.IO.File]::WriteAllText($LatestPath, ($o | ConvertTo-Json -Depth 5), $Utf8NoBom)
}

$script:ClipApi = $false
try { Add-Type -AssemblyName System.Windows.Forms, System.Drawing; $script:ClipApi = ([System.Threading.Thread]::CurrentThread.ApartmentState -eq 'STA') } catch { }
function Set-ClipboardImage([string]$path) {
    $img = [System.Drawing.Image]::FromFile($path)
    try {
        $do = New-Object System.Windows.Forms.DataObject
        $do.SetImage($img)
        $do.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-Object System.IO.MemoryStream(, [byte[]](0, 0, 0, 0))))
        [System.Windows.Forms.Clipboard]::SetDataObject($do, $true)
    } finally { $img.Dispose() }
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
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    else { [System.IO.File]::WriteAllText($UiPath, '{ "heightLevel": "half", "leftPx": null, "topPx": null, "autoCollapse": true, "clickThrough": false }', $Utf8NoBom) }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    $script:Restored = $true
}
$MarkdownAnswer = @"
# Tối ưu FPS $RunId
Bước 1: **Ưu tiên tiến trình** game lên ``High`` và nền xuống ``BelowNormal``.

- Tắt **overlay** Discord
* Giữ GPU dưới ``80%``
``````csharp
$CodeLine1
$CodeLine2
``````
Xong.
"@

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, selftest ui-v2b'
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
$bSelf = Invoke-SelfTest 'ui-v2b' 'task16b'
$bData = $bSelf.data
$e1Checks = [ordered]@{
    build    = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom  = [bool]$bom.Pass
    selftest = ($bSelf.exitCode -eq 0) -and $bSelf.noBom -and [bool]$bData.pass
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | selftest ui-v2b pass={5} failed=[{6}] | failed=[{7}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $bData.pass, (@($bData.failed) -join ','), (Get-FailedNames $e1Checks))

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: CUI-Opaque (alpha >= 0xF8 at 330 / 640 px) and CUI-Markdown (selftest + live render)'
[System.IO.File]::WriteAllText($UiPath, '{ "heightLevel": "half", "leftPx": null, "topPx": null, "autoCollapse": false, "clickThrough": false }', $Utf8NoBom)
$hostProc = Restart-Host
$null = Send-PipeJson 'YOUTUBE' 'off'          # a real YouTube tab on the G272 must not fold the HUD during the test
$null = Send-PipeLine '{"cmd":"HIDE"}'
$null = Send-PipeLine '{"cmd":"TOGGLE"}'       # Passive HUD
Start-Sleep -Milliseconds 400
$half = Set-Ui 'half'
$full = Set-Ui 'full'
$ghost = Set-Ui 'xuyen on'
$null = Set-Ui 'xuyen off'
$null = Set-Ui 'half'
Write-Latest 'verify16b' "Verify16b markdown $RunId" $MarkdownAnswer
$md = Wait-Ui { param($s) $null -ne $s.markdown -and [int]$s.markdown.CodeBlocks -ge 1 -and ([string]$s.aiHeader) -notlike 'LỊCH SỬ*' } 3000
$m = $md.markdown
$e2Checks = [ordered]@{
    opaqueHalf      = ([int]$half.chromeAlpha -ge 0xF8) -and ([string]$half.level -eq 'half')
    opaqueFull      = ([int]$full.chromeAlpha -ge 0xF8) -and ([string]$full.level -eq 'full')
    ghostStill60    = [bool]$ghost.transparent -and ([math]::Abs([double]$ghost.opacity - 0.6) -lt 0.01)
    selftestCounts  = [bool]$bData.checks.'md.counts' -and [bool]$bData.checks.'md.codeBlockRaw' -and [bool]$bData.checks.'md.inlineOrder'
    selftestEdges   = [bool]$bData.checks.'md.edgeCases'
    parseUnder1ms   = [bool]$bData.checks.'md.parseUnder1ms'
    liveRender      = ($null -ne $m) -and ([int]$m.BoldRuns -eq 2) -and ([int]$m.CodeRuns -eq 3) -and ([int]$m.CodeBlocks -eq 1) -and
                      ([int]$m.Bullets -eq 2) -and ([int]$m.Headings -eq 1) -and ([double]$m.ParseMs -lt 1.0)
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("backdrop {0} (alpha {1}) at 330 / {2} at 640, click-through opacity {3} | selftest 4 KB parse P50 {4} / P95 {5} ms | live: bold {6}, code {7}, blocks {8}, bullets {9}, headings {10}, parse {11} ms, render {12} ms | failed=[{13}]" -f `
    $half.chromeBackground, $half.chromeAlpha, $full.chromeAlpha, $ghost.opacity, $bData.markdown.p50, $bData.markdown.p95,
    $m.BoldRuns, $m.CodeRuns, $m.CodeBlocks, $m.Bullets, $m.Headings, $m.ParseMs, $m.RenderMs, (Get-FailedNames $e2Checks))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: Q&A history (< 2 ms, real rows only), Alt+Enter / Alt+A / F1 / key 1, MCP notify_shift_complete, clipboard image hint'
$hist = Send-PipeJson 'HISTORY' 'refresh'
$histCount = [int]$hist.count
Start-Sleep -Milliseconds 500   # the 8 answers are pre-rendered at idle priority right after the list loads
$viewTimes = @(); $cacheMisses = 0
for ($round = 0; $round -lt 3; $round++) {
    for ($i = 0; $i -lt $histCount; $i++) {
        $hv = Send-PipeJson 'HISTORY' ([string]$i)
        $viewTimes += [double]$hv.viewMs
        if (-not [bool]$hv.lastView.cacheHit) { $cacheMisses++ }
    }
}
$histLive = Send-PipeJson 'HISTORY' 'live'
$histTestRows = @(@($hist.rows) | Where-Object { Test-LooksLikeTest ([string]$_.aiModel) ([string]$_.task) })
$viewP95 = Get-Percentile $viewTimes 95
# Alt+Enter (next alliance step to the clipboard), Alt+A, F1
$null = Send-PipeLine '{"cmd":"COMMANDMODE"}'
Start-Sleep -Milliseconds 200
$altEnter = Send-PipeJson 'LAZY' 'altenter'
$alliance = Send-PipeJson 'ALLIANCE' $null
# Gate 15 run 1 (bug a): the expected task id comes from current_task.md with the same rule as AllianceStatus.FirstToken
# (first "TASK-<digits>" in the first 20 lines, plus one lower-case letter not followed by a letter or digit), never a literal,
# so this check keeps working when Verify-Task16b runs as regression under a later task.
$specPath = Join-Path $HandoffDir 'current_task.md'
$specHead = if (Test-Path $specPath) { (Get-Content $specPath -Encoding UTF8 -TotalCount 20) -join "`n" } else { '' }
$ExpectedTaskId = if ($specHead -cmatch 'TASK-[0-9]+(?:[a-z](?![A-Za-z0-9]))?') { $Matches[0] } else { $null }
$clipAfterAltEnter = $null
if ($script:ClipApi) { try { $clipAfterAltEnter = Get-Clipboard -Raw -ErrorAction Stop } catch { } }
$altA1 = Send-PipeJson 'LAZY' 'alta'
$altA2 = Send-PipeJson 'LAZY' 'alta'
$f1a = Send-PipeJson 'LAZY' 'f1'
$f1b = Send-PipeJson 'LAZY' 'f1'
# Number keys on the markdown answer: 1 = copy the csharp block; 9 = nothing offered
Write-Latest 'verify16b' "Verify16b phím số $RunId" $MarkdownAnswer
Start-Sleep -Milliseconds 600
$key1 = Send-PipeJson 'LAZY' '1'
$key9 = Send-PipeJson 'LAZY' '9'
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
# MCP notify_shift_complete -> running Cockpit
$mcp = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Verify-Task16b'; gate = "Gate 16b E3 $RunId"; status = 'TEST'; summary = 'Kiểm thử chỉ báo ca (Verify-Task16b E3)' } })
$shiftRes = $mcp[2]
$uiShift = Get-UiState
$shiftFile = Join-Path $ConfigDir 'shift_state.json'
# Clipboard image hint (STA PowerShell only): a new image is offered once, the same image is not offered again
$imgFirst = $null; $imgSecond = $null
if ($script:ClipApi) {
    $png = Join-Path $env:TEMP "nexus_verify16b_$RunId.png"
    $bmp = New-Object System.Drawing.Bitmap 64, 48
    try { $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::DarkCyan); $g.Dispose(); $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }
    Set-ClipboardImage $png
    $null = Send-PipeLine '{"cmd":"COMMANDMODE"}'
    $imgFirst = Wait-Ui { param($s) $null -ne $s.clipboardProbe -and [bool]$s.clipboardProbe.image } 2000
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    $null = Send-PipeLine '{"cmd":"COMMANDMODE"}'
    Start-Sleep -Milliseconds 400
    $imgSecond = Get-UiState
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
}
$e3Checks = [ordered]@{
    selftestHistory  = [bool]$bData.checks.'hist.onlyRealAnswered' -and [bool]$bData.checks.'hist.queryUnder2ms'
    historyClean     = ($histTestRows.Count -eq 0)
    historyViewUnder2 = ($histCount -eq 0) -or (($null -ne $viewP95) -and ($viewP95 -lt 2.0))
    historyBackLive  = ([int]$histLive.index -eq -1)
    selftestAlliance = [bool]$bData.checks.'alliance.parse' -and [bool]$bData.checks.'alliance.implement' -and [bool]$bData.checks.'alliance.verify' -and [bool]$bData.checks.'alliance.review'
    altEnterCopies   = ($null -ne $ExpectedTaskId) -and ([string]$alliance.snapshot.TaskId -ceq $ExpectedTaskId) -and ([string]$alliance.lastCopy.NextStep -eq [string]$alliance.snapshot.NextStep) -and
                       ([int]$alliance.lastCopy.chars -gt 20) -and [bool]$alliance.lastCopy.systemClipboard -and
                       ((-not $script:ClipApi) -or (([string]$clipAfterAltEnter).Trim() -eq ([string]$alliance.snapshot.ClipboardText).Trim()))
    altAToggles      = [bool]$altA1.alliancePanelVisible -and -not [bool]$altA2.alliancePanelVisible
    f1Toggles        = [bool]$f1a.cheatSheetVisible -and -not [bool]$f1b.cheatSheetVisible
    key1CopiesCode   = [bool]$key1.lastLazy.ok -and (([string]$key1.lastCopied).Trim() -eq "$CodeLine1`n$CodeLine2") -and -not [bool]$key9.lastLazy.ok
    mcpShift         = ($null -ne $shiftRes) -and [bool]$shiftRes.ok -and [bool]$shiftRes.cockpitDelivered -and ([double]$shiftRes.cockpit.applyMs -lt 2.0) -and
                       [bool]$uiShift.shift.badgeVisible -and ([string]$uiShift.shift.badge -like "*Gate 16b E3 $RunId*") -and
                       (Test-Path $shiftFile) -and -not (Test-HasUtf8Bom $shiftFile)
    clipboardHint    = (-not $script:ClipApi) -or (($null -ne $imgFirst) -and [bool]$imgFirst.clipboardProbe.offered -and ([string]$imgFirst.commandHint -like '*Ảnh mới*') -and
                       ($null -ne $imgSecond) -and -not [bool]$imgSecond.clipboardProbe.offered)
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("history {0} rows (test rows {1}), load {2} ms, {3} views P95 {4} ms (cache misses $cacheMisses) | selftest temp DB query P95 {5} ms | Alt+Enter -> {6} ({7} chars, clipboard={8}) | Alt+A {9}/{10}, F1 {11}/{12} | key 1 '{13}' | MCP shift apply {14} ms, delivered={15}, badge '{16}' | image hint first={17} second={18} (STA={19}) | failed=[{20}]" -f `
    $histCount, $histTestRows.Count, $hist.loadMs, $viewTimes.Count, $viewP95, $bData.history.p95, $alliance.lastCopy.NextStep, $alliance.lastCopy.chars,
    $alliance.lastCopy.systemClipboard, $altA1.alliancePanelVisible, $altA2.alliancePanelVisible, $f1a.cheatSheetVisible, $f1b.cheatSheetVisible,
    $key1.lastLazy.message, $shiftRes.cockpit.applyMs, $shiftRes.cockpitDelivered, $uiShift.shift.badge,
    $(if ($imgFirst) { $imgFirst.clipboardProbe.offered } else { 'n/a' }), $(if ($imgSecond) { $imgSecond.clipboardProbe.offered } else { 'n/a' }), $script:ClipApi,
    (Get-FailedNames $e3Checks))
if ($histCount -eq 0) { Write-Host '  No real answered dispatch in this database: history view timing skipped (selftest covers it on a temp database).' -ForegroundColor Yellow }
if (-not $script:ClipApi) { Write-Host '  Clipboard checks skipped: this PowerShell is not STA (run Windows PowerShell 5.1 without -MTA).' -ForegroundColor Yellow }

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: Gate 16 debt - WAKE from 40 px to 330 px + focus (10 x Ctrl+Shift+Space path), P50 <= 10.0 / P95 <= 16.0 ms'
$wakes = @()
for ($i = 0; $i -lt 10; $i++) {
    $null = Set-Ui 'collapsed'
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    Start-Sleep -Milliseconds 300
    $resp = Send-PipeRaw 'COMMANDMODE' $null
    Start-Sleep -Milliseconds 250
    $w = (Get-UiState).lastWake
    $ms = if ($null -ne $w -and $null -ne $w.Ms) { [double]$w.Ms } else { $null }   # JSON number (the pipe text uses the PC culture)
    $wakes += [ordered]@{ ms = $ms; pipe = $resp; from = $w.From; to = $w.To; height = $w.Height; focused = $w.InputFocused; panes = $w.PanesVisible; layoutMs = $w.LayoutMs
        beforeLevelMs = $w.BeforeLevelMs; levelMs = $w.LevelAndShowMs; activateMs = $w.ActivateMs; focusMs = $w.FocusMs
        windowHeightMs = $(if ($w.LevelParts) { $w.LevelParts.WindowHeightMs } else { $null }); chromeMs = $(if ($w.LevelParts) { $w.LevelParts.ChromeMs } else { $null }) }
}
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$wakeMs = @($wakes | Where-Object { $null -ne $_.ms } | ForEach-Object { [double]$_.ms })
$layoutMs = @($wakes | Where-Object { $null -ne $_.layoutMs } | ForEach-Object { [double]$_.layoutMs })
$wakeP50 = Get-Percentile $wakeMs 50
$wakeP95 = Get-Percentile $wakeMs 95
$layoutP95 = Get-Percentile $layoutMs 95
# TASK-17 spec §2.3 (Architect, 2026-11-12): WAKE [SLA-WARN] when a single sample misses one DWM VSync. Within budget (P50 <= 10,0
# and P95 <= 16,0 ms) = PASS. Otherwise PASS with [SLA-WARN] when (P50 <= 11,0 and P95 <= 16,0) or (P95 <= 18,5 and at least
# 9 of the 10 samples <= 12,0 ms); anything else = FAIL.
$wakeWithin = ($null -ne $wakeP50) -and ($null -ne $wakeP95) -and ($wakeP50 -le 10.0) -and ($wakeP95 -le 16.0)
$wakeFast = @($wakeMs | Where-Object { $_ -le 12.0 }).Count
$wakeWarnA = ($null -ne $wakeP50) -and ($null -ne $wakeP95) -and ($wakeP50 -le 11.0) -and ($wakeP95 -le 16.0)
$wakeWarnB = ($null -ne $wakeP95) -and ($wakeP95 -le 18.5) -and ($wakeFast -ge 9)
$wakeOk = $wakeWithin -or $wakeWarnA -or $wakeWarnB
$wakeSlaWarnings = @()
if ($wakeOk -and -not $wakeWithin) {
    $wakeSlaWarnings += [ordered]@{ metric = 'wake'; p50 = $wakeP50; p95 = $wakeP95; fastSamples = $wakeFast; rule = $(if ($wakeWarnA) { 'P50 <= 11,0 and P95 <= 16,0' } else { 'P95 <= 18,5 and >= 9/10 samples <= 12,0' }) }
    Write-Host ("[SLA-WARN (one VSync, TASK-17 §2.3)] E4 WAKE P50 {0} / P95 {1} ms, {2}/10 samples <= 12 ms: {3}" -f $wakeP50, $wakeP95, $wakeFast, $wakeSlaWarnings[0].rule) -ForegroundColor Yellow
}
$wakeShapeOk = (@($wakes | Where-Object { $_.from -ne 'collapsed' -or $_.to -ne 'half' -or [double]$_.height -ne 330 -or -not [bool]$_.focused -or -not [bool]$_.panes }).Count -eq 0)
$e4Checks = [ordered]@{
    tenSamples  = ($wakeMs.Count -eq 10)
    collapsedToHalfFocused = $wakeShapeOk
    wakeP50     = ($null -ne $wakeP50) -and (($wakeP50 -le 10.0) -or $wakeOk)
    wakeP95     = ($null -ne $wakeP95) -and (($wakeP95 -le 16.0) -or $wakeOk)
}
$e4Pass = -not ($e4Checks.Values -contains $false)
Write-Result 'E4' $e4Pass ("WAKE 40 -> 330 px + focus: P50 {0} / P95 {1} ms (samples {2}) | P95 parts: before {5} / height+show {6} / Activate {7} / focus {8} ms | after the layout pass (info): P95 {3} ms | failed=[{4}]" -f `
    $wakeP50, $wakeP95, (($wakeMs | ForEach-Object { '{0:N2}' -f $_ }) -join ' / '), $layoutP95, (Get-FailedNames $e4Checks),
    (Get-Percentile @($wakes | ForEach-Object { [double]$_.beforeLevelMs }) 95), (Get-Percentile @($wakes | ForEach-Object { [double]$_.levelMs }) 95),
    (Get-Percentile @($wakes | ForEach-Object { [double]$_.activateMs }) 95), (Get-Percentile @($wakes | ForEach-Object { [double]$_.focusMs }) 95))
Write-Host ("  inside height+show, P95: chrome {0} ms, window Height {1} ms" -f (Get-Percentile @($wakes | Where-Object { $null -ne $_.chromeMs } | ForEach-Object { [double]$_.chromeMs }) 95),
    (Get-Percentile @($wakes | Where-Object { $null -ne $_.windowHeightMs } | ForEach-Object { [double]$_.windowHeightMs }) 95))

# --------------------------------------------- E5 ---------------------------------------------
$Task16bIdleBudgetMb = 45.0
Write-Step 'E5: settled idle Private WS <= 45.0 MB (hard 52.0), idle CPU 0.0 %, regression chain Task16 -> Task03, git, CI'
$null = Send-PipeLine '{"cmd":"HIDE"}'
Start-Sleep -Milliseconds 8000
$idleRead = Get-NexusSettledPrivateWs -ProcessId $hostProc.Id -LastTrimUtc {
    $mm = $null; try { $mm = Send-PipeJson 'MEM' $null } catch { }
    if ($mm -and $mm.lastTrim) { $mm.lastTrim.AtUtc } else { $null }
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
if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored before the regression chain.' }
$null = Restart-Host
$chain = @()
function Invoke-ChainStep([string]$name, [string[]]$scriptArgs, [string]$reportName) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $name) @scriptArgs | Out-Host
    $code = $LASTEXITCODE
    $rep = $null
    if ($reportName) { $rp = Join-Path $HandoffDir $reportName; if (Test-Path $rp) { $rep = Get-Content $rp -Raw -Encoding UTF8 | ConvertFrom-Json } }
    $pass = ($code -eq 0) -and ((-not $reportName) -or ($null -ne $rep -and [bool]$rep.allPassed))
    Write-Host ("  chain step {0} {1}: exit {2}, {3} s -> {4}" -f $name, ($scriptArgs -join ' '), $code, [math]::Round($sw.Elapsed.TotalSeconds, 1), $(if ($pass) { 'PASS' } else { 'FAIL' })) -ForegroundColor $(if ($pass) { 'Green' } else { 'Red' })
    return [ordered]@{ step = $name; args = ($scriptArgs -join ' '); exitCode = $code; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); pass = $pass }
}
$chain += Invoke-ChainStep 'Verify-Task16.ps1' @('-SkipBuild') 'task16_verification.json'
$chain += Invoke-ChainStep 'Verify-Task13.ps1' @('-SkipBuild') 'task13_verification.json'
if (-not $FastE5) {
    $chain += Invoke-ChainStep 'Verify-Task12.ps1' @('-SkipBuild') 'task12_verification.json'
    $chain += Invoke-ChainStep 'Verify-Task13a.ps1' @('-SkipBuild') 'task13a_verification.json'
}
if ($FastE5) {
    $chain += Invoke-ChainStep 'Invoke-NexusRegression.ps1' @('-FastRegression', '-SkipBuild') $null
} else {
    $fullArgs = @('-FullRegression', '-SkipBuild')
    if (-not $Live) { $fullArgs += '-SkipLive' }
    $chain += Invoke-ChainStep 'Invoke-NexusRegression.ps1' $fullArgs $null
}
$chainSummaryPath = Join-Path $HandoffDir $(if ($FastE5) { 'regression_fast.json' } else { 'regression_full.json' })
$chainSummary = if (Test-Path $chainSummaryPath) { Get-Content $chainSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task16b'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    # TASK-17a decision 17a.2 (Architect 2026-11-12): 43,5 MB was dropped (inside the WPF render-surface noise); the budget stays 45,0 MB
    privateWsUnder45 = [bool](Test-NexusIdleRam $idleMb $memJson 'Task16b').pass   # <= 45,0, or [SLA-WARN] <= 50,0 with GC heap <= 8,0
    idleCpuZero      = ([math]::Round($idleCpu, 1) -eq 0.0)
    regressionChain  = (@($chain | Where-Object { -not $_.pass }).Count -eq 0) -and ($null -ne $chainSummary) -and [bool]$chainSummary.pass
    gitUnchanged     = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45.0; hard 52.0; HUD {1}) | idle CPU {2} % ({3}) | chain {4} | git same={5} {6} | CI: {7} | failed=[{8}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), (($chain | ForEach-Object { "{0} {1}" -f $_.step, $(if ($_.pass) { 'PASS' } else { 'FAIL' }) }) -join ' -> '),
    $gitEnd.Same, ($gitEnd.Reasons -join '; '), $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks))
if (-not ($ci.checked -and $ci.conclusion -eq 'success')) { Write-Host '  Confirm the green CI run in the GitHub Actions tab.' -ForegroundColor Yellow }

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-16b'; gate = 'Gate 16b'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5; live = [bool]$Live
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; selftest = $bData }
    E2_OpaqueMarkdown = [ordered]@{ pass = $e2Pass; checks = $e2Checks; half = $half.chromeBackground; halfAlpha = $half.chromeAlpha; fullAlpha = $full.chromeAlpha; ghostOpacity = $ghost.opacity; liveMarkdown = $m; selftestMarkdown = $bData.markdown }
    E3_LayoutLazyAlliance = [ordered]@{ pass = $e3Pass; checks = $e3Checks; expectedTaskId = $ExpectedTaskId; history = $hist; historyViewsMs = $viewTimes; historyViewP95 = $viewP95; alliance = $alliance; altEnter = $altEnter; altA = @($altA1.alliancePanelVisible, $altA2.alliancePanelVisible); f1 = @($f1a.cheatSheetVisible, $f1b.cheatSheetVisible); key1 = $key1.lastLazy; key9 = $key9.lastLazy; mcpShift = $shiftRes; shiftUi = $uiShift.shift; clipboardSta = $script:ClipApi; imageFirst = $(if ($imgFirst) { $imgFirst.clipboardProbe } else { $null }); imageSecond = $(if ($imgSecond) { $imgSecond.clipboardProbe } else { $null }) }
    E4_Wake40to330 = [ordered]@{ pass = $e4Pass; checks = $e4Checks; samples = $wakes; p50 = $wakeP50; p95 = $wakeP95; slaWarnings = $wakeSlaWarnings; layoutP95Info = $layoutP95 }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; chain = $chain; chainSummary = $chainSummary; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
# The script reports its own shift (this is the shift_state.json it leaves behind, shown by Alt+A and the 40 px badge).
$final = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 16b'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task16b: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; RAM rảnh {5} MB, WAKE P95 {6} ms" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $idleMb, $wakeP95) } })
Write-Host "`nGate 16b verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
