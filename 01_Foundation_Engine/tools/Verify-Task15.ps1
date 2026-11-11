#Requires -Version 5.1
<#
.SYNOPSIS
    NEXUS Home Hub - Gate 15 automated verification (TASK-15, ADR-62: /ctx Active Window Context, Anti-Cheat Safe Guard,
    Sensitive Window Denylist, MCP get_active_window_context), with the Gate 16b WAKE budget kept.
.DESCRIPTION
    E1  Release build (-warnaserror, 0/0), Zero-BOM gate, selftest ctx-safeguard (rule edge cases, every row is_test = 1).
    E2  Live HostService: 10 anti-cheat samples (vgc, EasyAntiCheat, cs2, valorant) + 10 sensitive windows (Bitwarden,
        InPrivate, .env - Notepad, Vietcombank OTP) through /ctx status and /ctx <question>: 100 % blocked, guard P95 <= 0,5 ms,
        no dispatch / no Gemini call, and a unique marker of the titles is found neither in latest_dispatch.json nor in the
        SQLite file or its WAL. A running anti-cheat process blocks before the title is read.
    E3  /ctx status on the real window this script runs in (the Cockpit is passive, so it is the foreground) and on a normal
        simulated window; clipboard text <= 1 000 chars, block <= 1 200, a secret-looking clipboard is never attached;
        1 + 25 x /ctx <question> on the same window = cache hits, P95 <= 3,0 ms, 0 tokens; MCP get_active_window_context:
        JSON fields, warm P95 <= 2,0 ms (the first call of the fresh McpTools process is reported, not graded).
    E4  WAKE 40 -> 330 px (Ctrl+Shift+Space path): ColdFirstWake = first wake right after the E2 HostService restart (LevelParts
        recorded, report only, not graded); each wake waits until the window is back at 40 px plus 50 ms outside the timer;
        1 Warmup wake right after E3 (recorded with its LevelParts, <= 60 ms), then
        N = 10 scored wakes P50 <= 10,0 / P95 <= 16,0 ms, each with LevelParts HwndCaptureMs / WindowHeightMs / ActivateMs /
        FocusMs in the JSON; the Q&A history holds no /ctx test row.
    E5  Settled idle Private WS <= 45,0 MB (hard 52,0), idle CPU 0,0 %, regression chain Verify-Task16b (which runs Task16 ->
        Task13 -> Task12 -> Task13a -> full chain Task14 ... Task03, -SkipLive) with 0 live Gemini calls (Task07/09/10/11
        reports audited), git guard, CI.
    Gemini is mocked (0 live tokens). The user's latest_dispatch.json, cockpit_ui.json and clipboard text are restored; the
    simulation hooks are switched off; at the end the script reports its own result through MCP notify_shift_complete.
    Writes 03_Local_Data_Vault/handoff/task15_verification.json (UTF-8 without BOM).
#>
param(
    [switch]$SkipBuild,
    [switch]$FastE5   # development loop: Verify-Task16b -FastE5 instead of its full chain
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
$DbPath     = Join-Path $VaultRoot 'structured_db\nexus_memory.db'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$McpExe     = Join-Path $EngineRoot 'NEXUSHome.McpTools\bin\Release\net8.0\NEXUSHome.McpTools.exe'
$ReportPath = Join-Path $HandoffDir 'task15_verification.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$RunId      = Get-Date -Format 'HHmmss'
$Marker     = "ctxmark$RunId" + (Get-Random -Minimum 1000 -Maximum 9999)
$MockAnswer = 'Gate 15 ctx mock'
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
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself, then re-run Verify-Task15.ps1.'
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
# The Gemini mock lives in the HostService environment (TestRun: every /ctx dispatch row is is_test = 1).
function Restart-Host([bool]$mock) {
    Stop-HostService
    if ($mock) { $env:NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer }
    try { $p = Start-HostIfNeeded }
    finally { if (Test-Path Env:NEXUS_GEMINI_MOCK_RESPONSE) { Remove-Item Env:NEXUS_GEMINI_MOCK_RESPONSE } }
    return $p
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
$script:FakeDispatchId = 920000000L
function Write-Latest([string]$model, [string]$task, [string]$answer) {
    $script:FakeDispatchId++
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $o = [ordered]@{ event = 'ai_response'; source = 'Verify-Task15'; dispatchId = $script:FakeDispatchId; status = 'PASSED'; retryCount = 0; evidenceNotes = ''
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


function Find-DbPath {
    if (Test-Path $DbPath) { return $DbPath }
    $c = Get-ChildItem $VaultRoot -Filter 'nexus_memory.db' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { return $c.FullName } else { return $null }
}
function Test-FileHasText([string]$path, [string]$text) {
    if (-not $path -or -not (Test-Path $path)) { return $false }
    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite, Delete')
    try {
        $bytes = New-Object byte[] $fs.Length
        $read = 0
        while ($read -lt $bytes.Length) { $n = $fs.Read($bytes, $read, $bytes.Length - $read); if ($n -le 0) { break }; $read += $n }
    } finally { $fs.Dispose() }
    $needle = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hay = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes, 0, $read)          # byte-for-byte (Latin-1) search
    return $hay.Contains([System.Text.Encoding]::GetEncoding(28591).GetString($needle))
}
function Invoke-Ctx([string]$text) { return Send-PipeJson 'COCKPIT' $text }

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
        try { $null = Send-PipeJson 'CTXSIM' 'off'; $null = Send-PipeJson 'CTXSIM' 'running:real' } catch { }
    }
    if ($null -ne $uiBackup) { [System.IO.File]::WriteAllBytes($UiPath, $uiBackup) }
    if ($null -ne $clipboardBackup -and $clipboardBackup.Length -gt 0) { try { Set-Clipboard -Value $clipboardBackup } catch { } }
    $script:Restored = $true
}

try {
# --------------------------------------------- E1 ---------------------------------------------
Write-Step 'E1: Release build (-warnaserror), Zero-BOM, selftest ctx-safeguard'
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
$cSelf = Invoke-SelfTest 'ctx-safeguard' 'task15'
$cData = $cSelf.data
$e1Checks = [ordered]@{
    build    = ($buildCode -eq 0) -and ($buildWarnings -eq 0) -and ($buildErrors -eq 0)
    zeroBom  = [bool]$bom.Pass
    selftest = ($cSelf.exitCode -eq 0) -and $cSelf.noBom -and [bool]$cData.pass
    edgeRules = [bool]$cData.checks.'e1.antiCheatNames' -and [bool]$cData.checks.'e1.denylistRules' -and [bool]$cData.checks.'e1.blockAndFingerprint'
}
$e1Pass = -not ($e1Checks.Values -contains $false)
Write-Result 'E1' $e1Pass ("build code={0} warnings={1} errors={2} | Zero-BOM: {3} files, {4} offender(s) | selftest ctx-safeguard pass={5} failed=[{6}] | failed=[{7}]" -f `
    $buildCode, $buildWarnings, $buildErrors, $bom.Checked, @($bom.Offenders).Count, $cData.pass, (@($cData.failed) -join ','), (Get-FailedNames $e1Checks))

# Gate 15 run 2 (Architect point 6): every WAKE sample carries its time breakdown. Names follow the Architect's brief and map
# to the Cockpit's WakeRecord (no Cockpit code change): hwndCaptureMs = BeforeLevelMs (hotkey timestamp -> before ApplyLevel:
# foreground HWND capture, HUD mode, click-through), windowHeightMs = LevelParts.WindowHeightMs (inside levelAndShowMs),
# activateMs = Activate(), focusMs = command box focus; chrome/clamp/rest/layout are kept for information.
function Get-WakeSample {
    $null = Set-Ui 'collapsed'
    $null = Send-PipeLine '{"cmd":"PASSIVE"}'
    Start-Sleep -Milliseconds 300
    # Gate 15 run 3 (Architect point 2): the window must be back at 40 px, then a 50 ms static pause outside the timer so DWM
    # has composed the 40 px surface before the timed 40 -> 330 px wake. Window.Height logic of Gate 16 / 16b is unchanged.
    $pre = Wait-Ui { param($u) ([string]$u.level -eq 'collapsed') -and ([double]$u.height -le 40.5) } 1000
    $settled = ($null -ne $pre) -and ([string]$pre.level -eq 'collapsed') -and ([double]$pre.height -le 40.5)
    Start-Sleep -Milliseconds 50
    $null = Send-PipeRaw 'COMMANDMODE' $null
    Start-Sleep -Milliseconds 250
    $w = (Get-UiState).lastWake
    $parts = if ($w) { $w.LevelParts } else { $null }
    return [ordered]@{
        ms = $(if ($w) { [double]$w.Ms } else { $null }); from = $w.From; to = $w.To; focused = $w.InputFocused
        settledAt40 = $settled; preHeight = $(if ($pre) { $pre.height } else { $null }); prePhysicalPx = $(if ($pre -and $pre.rectPx) { $pre.rectPx.h } else { $null })
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

# --------------------------------------------- E2 ---------------------------------------------
Write-Step 'E2: live blocks (10 anti-cheat + 10 sensitive), P95 <= 0,5 ms, no title in latest_dispatch.json / SQLite / WAL, 0 tokens'
$hostProc = Restart-Host $true
# Gate 15 run 3 (Architect point 1a): the first wake of the freshly started HostService is ColdFirstWake. It is recorded with its
# LevelParts for diagnosis only (run 2: Activate 54,6 of 62,9 ms) and is not graded; the Warmup <= 60 ms after E3 and the
# N = 10 scored wakes of E4 stay as they are. E2-E3 only use passive TOGGLE, so without this the Warmup was this cold wake.
$coldLevel0 = [string](Get-UiState).level
$coldFirstWake = Get-WakeSample
Write-Host ('  ColdFirstWake (report only) ' + (Format-WakeParts $coldFirstWake)) -ForegroundColor DarkGray
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui $(if (@('collapsed', 'half', 'full') -contains $coldLevel0) { $coldLevel0 } else { 'half' })
$null = Send-PipeJson 'YOUTUBE' 'off'
$null = Send-PipeLine '{"cmd":"HIDE"}'
$null = Send-PipeLine '{"cmd":"TOGGLE"}'
Start-Sleep -Milliseconds 300
$null = Send-PipeJson 'CTXSIM' 'running:'          # isolate the denylist from whatever runs on this PC
$acSamples = 'vgc|Riot Vanguard', 'EasyAntiCheat|EasyAntiCheat', 'cs2|Counter-Strike 2', 'valorant|VALORANT'
$sensSamples = "Bitwarden|Bitwarden $Marker", "msedge|InPrivate $Marker - Microsoft Edge", "Notepad|.env $Marker - Notepad", "msedge|Vietcombank OTP $Marker"
foreach ($w in @($acSamples + $sensSamples)) { $null = Send-PipeJson 'CTXSIM' $w; $null = Invoke-Ctx '/ctx status' }   # warm-up (JIT)
$acRuns = @(); $sensRuns = @()
for ($i = 0; $i -lt 10; $i++) {
    $null = Send-PipeJson 'CTXSIM' $acSamples[$i % 4]
    $r = Invoke-Ctx '/ctx --no-clip đây là gì?'
    $acRuns += [ordered]@{ window = $acSamples[$i % 4]; status = $r.ctx.Status; shield = $r.ctx.Shield; guardMs = [double]$r.ctx.GuardMs; dispatch = $r.dispatchId; ai = $r.aiPending; msg = $r.message }
    $null = Send-PipeJson 'CTXSIM' $sensSamples[$i % 4]
    $r = Invoke-Ctx $(if ($i % 2 -eq 0) { '/ctx status' } else { '/ctx --no-clip đây là gì?' })
    $sensRuns += [ordered]@{ window = ($sensSamples[$i % 4] -replace [regex]::Escape($Marker), '<marker>'); status = $r.ctx.Status; title = $r.ctx.WindowTitle; process = $r.ctx.ProcessName; guardMs = [double]$r.ctx.GuardMs; dispatch = $r.dispatchId; ai = $r.aiPending; msg = $r.message }
}
# anti-cheat running in the background: blocked before the title is read
$null = Send-PipeJson 'CTXSIM' 'running:notepad,vgc'
$null = Send-PipeJson 'CTXSIM' "Notepad|ghi chú $Marker"
$runningRes = Invoke-Ctx '/ctx status'
$null = Send-PipeJson 'CTXSIM' 'running:'
Start-Sleep -Milliseconds 300
$latestText = if (Test-Path $LatestPath) { [System.IO.File]::ReadAllText($LatestPath) } else { '' }
$db = Find-DbPath
$leakLatest = $latestText.Contains($Marker)
$leakDb = Test-FileHasText $db $Marker
$leakWal = Test-FileHasText "$db-wal" $Marker
$leakMsg = @($acRuns + $sensRuns | Where-Object { ([string]$_.msg).Contains($Marker) -or ([string]$_.title).Contains($Marker) }).Count
$acP95 = Get-Percentile @($acRuns | ForEach-Object { $_.guardMs }) 95
$sensP95 = Get-Percentile @($sensRuns | ForEach-Object { $_.guardMs }) 95
$e2Checks = [ordered]@{
    selftestBlocks    = [bool]$cData.checks.'e2.antiCheatBlocked' -and [bool]$cData.checks.'e2.sensitiveBlocked' -and [bool]$cData.checks.'e2.blockP95' -and
                        [bool]$cData.checks.'e2.runningAntiCheat' -and [bool]$cData.checks.'e2.noLeak' -and [bool]$cData.checks.'e2.zeroTokens'
    antiCheat100      = (@($acRuns | Where-Object { $_.status -ne 'BLOCKED_ANTICHEAT' }).Count -eq 0)
    sensitive100      = (@($sensRuns | Where-Object { $_.status -ne 'BLOCKED_SENSITIVE' -or $null -ne $_.title -or $null -ne $_.process }).Count -eq 0)
    blockP95          = ($null -ne $acP95) -and ($acP95 -le 0.5) -and ($null -ne $sensP95) -and ($sensP95 -le 0.5)
    noGeminiNoRow     = (@($acRuns + $sensRuns | Where-Object { [long]$_.dispatch -gt 0 -or [bool]$_.ai }).Count -eq 0)
    runningBlocksFirst = ([string]$runningRes.ctx.Status -eq 'BLOCKED_ANTICHEAT') -and ([string]$runningRes.ctx.Shield -eq 'anticheat-running') -and ($null -eq $runningRes.ctx.WindowTitle)
    noLeak            = (-not $leakLatest) -and (-not $leakDb) -and (-not $leakWal) -and ($leakMsg -eq 0) -and ($null -ne $db)
}
$e2Pass = -not ($e2Checks.Values -contains $false)
Write-Result 'E2' $e2Pass ("anti-cheat {0}/10 blocked P95 {1} ms | sensitive {2}/10 blocked P95 {3} ms | running vgc -> {4} ({5}) | selftest P95 {6} / {7} ms | marker in latest={8} db={9} wal={10} msgs={11} ({12}) | failed=[{13}]" -f `
    @($acRuns | Where-Object { $_.status -eq 'BLOCKED_ANTICHEAT' }).Count, $acP95, @($sensRuns | Where-Object { $_.status -eq 'BLOCKED_SENSITIVE' }).Count, $sensP95,
    $runningRes.ctx.Status, $runningRes.ctx.Shield, $cData.blocks.acP95, $cData.blocks.sensP95, $leakLatest, $leakDb, $leakWal, $leakMsg, $db, (Get-FailedNames $e2Checks))
Write-Host ("  real Toolhelp32 scan on this PC (selftest): {0} ms cold, matches [{1}]" -f $cData.blocks.realScan.coldMs, (@($cData.blocks.realScan.coldMatches) -join ', '))

# --------------------------------------------- E3 ---------------------------------------------
Write-Step 'E3: /ctx on the real window and a normal window, clipboard limits, 25 cache hits P95 <= 3,0 ms, MCP get_active_window_context <= 2,0 ms'
$null = Send-PipeJson 'CTXSIM' 'off'
$null = Send-PipeJson 'CTXSIM' 'running:real'
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$realStatus = Invoke-Ctx '/ctx --no-clip status'      # the Cockpit is passive: the foreground is the window running this script
$null = Send-PipeJson 'CTXSIM' 'running:'
$normal = "Code|ActiveWindowContext.cs - NEXUS $RunId - Visual Studio Code"
$null = Send-PipeJson 'CTXSIM' $normal
$clipShort = $null; $clipLong = $null; $clipSecret = $null
if ($script:ClipApi) {
    Set-Clipboard -Value "đoạn chữ ngữ cảnh $RunId"
    $clipShort = Invoke-Ctx '/ctx status'
    Set-Clipboard -Value ('x' * 5000)
    $clipLong = Invoke-Ctx '/ctx status'
    Set-Clipboard -Value ('khóa sk-' + ('c' * 24))
    $clipSecret = Invoke-Ctx '/ctx status'
}
$question = "Đoạn này làm gì? $RunId"
$first = Invoke-Ctx "/ctx --no-clip $question"
Start-Sleep -Seconds 2   # the mock answer is stored in GeminiResponseCache when the background call ends
$hitMs = @(); $hitCount = 0; $ctxIds = @([long]$first.dispatchId)
for ($i = 0; $i -lt 25; $i++) {
    $r = Invoke-Ctx "/ctx --no-clip $question"
    $hitMs += [double]$r.elapsedMs
    if ([bool]$r.cacheHit) { $hitCount++ }
    if ($r.dispatchId) { $ctxIds += [long]$r.dispatchId }
}
$hitP95 = Get-Percentile $hitMs 95
# MCP: 11 calls in one fresh McpTools process (call 1 = cold start, reported only)
$mcpCalls = @(); for ($i = 0; $i -lt 11; $i++) { $mcpCalls += @{ name = 'get_active_window_context'; arguments = [ordered]@{ includeClipboard = $false } } }
$mcp = Invoke-Mcp $mcpCalls
$mcpResults = @(2..12 | ForEach-Object { $mcp[$_] })
$mcpWarm = @($mcpResults | Select-Object -Skip 1 | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_.totalMs })
$mcpP95 = Get-Percentile $mcpWarm 95
$mcpShape = ($mcpResults.Count -eq 11) -and (@($mcpResults | Where-Object { $null -eq $_ -or $null -eq $_.status -or $null -eq $_.guardMs -or [int]$_.tokens -ne 0 }).Count -eq 0)
$mcpOk = $mcpResults[1]
$e3Checks = [ordered]@{
    selftestFlow   = [bool]$cData.checks.'e3.status' -and [bool]$cData.checks.'e3.firstAnswer' -and [bool]$cData.checks.'e3.cacheHits' -and
                     [bool]$cData.checks.'e3.cacheHitP95' -and [bool]$cData.checks.'e3.otherWindowMiss' -and [bool]$cData.checks.'e3.inspectShape' -and [bool]$cData.checks.'e3.inspectP95'
    realWindow     = ([string]$realStatus.ctx.Status -eq 'OK' -and ([string]$realStatus.ctx.ProcessName).Length -gt 0 -and [int]([string]$realStatus.ctx.ContextBlock).Length -le 1200) -or
                     ([string]$realStatus.ctx.Shield -eq 'anticheat-running')
    clipboard      = (-not $script:ClipApi) -or (
                        ([string]$clipShort.ctx.ClipText -eq "đoạn chữ ngữ cảnh $RunId") -and
                        ([string]$clipLong.ctx.ClipText).Length -eq 1000 -and ([string]$clipLong.ctx.ContextBlock).Length -le 1200 -and
                        ($null -eq $clipSecret.ctx.ClipText))
    firstAnswer    = ([string]$first.kind -eq 'ctx') -and ([long]$first.dispatchId -gt 0) -and -not [bool]$first.cacheHit
    cacheHits      = ($hitCount -eq 25) -and ($null -ne $hitP95) -and ($hitP95 -le 3.0)   # a cache hit deducts 0 tokens (selftest e3.cacheHits compares the token ledger)
    mcp            = $mcpShape -and ($null -ne $mcpP95) -and ($mcpP95 -le 2.0) -and (([string]$mcpOk.status -eq 'OK') -or ([string]$mcpOk.shield -eq 'anticheat-running'))
}
$e3Pass = -not ($e3Checks.Values -contains $false)
Write-Result 'E3' $e3Pass ("real window: {0} {1} '{2}' ({3}) | clipboard short/long/secret: {4}/{5}/{6} chars | first #{7} answered={8} | 25 hits={9} P95 {10} ms | MCP cold {11} ms, warm P95 {12} ms, status {13} | failed=[{14}]" -f `
    $realStatus.ctx.Status, $realStatus.ctx.ProcessName, $realStatus.ctx.WindowTitle, $realStatus.ctx.Shield,
    $(if ($clipShort) { ([string]$clipShort.ctx.ClipText).Length } else { 'n/a' }), $(if ($clipLong) { ([string]$clipLong.ctx.ClipText).Length } else { 'n/a' }),
    $(if ($clipSecret) { ([string]$clipSecret.ctx.ClipText).Length } else { 'n/a' }), $first.dispatchId, (-not [bool]$first.cacheHit), $hitCount, $hitP95,
    $(if ($mcpResults[0]) { $mcpResults[0].totalMs } else { 'n/a' }), $mcpP95, $mcpOk.status, (Get-FailedNames $e3Checks))
if ([string]$realStatus.ctx.Shield -eq 'anticheat-running') { Write-Host ("  This PC runs an anti-cheat process ({0}): /ctx correctly refuses every window while it runs." -f $realStatus.ctx.MatchedRule) -ForegroundColor Yellow }

# --------------------------------------------- E4 ---------------------------------------------
Write-Step 'E4: WAKE 40 -> 330 px still P50 <= 10,0 / P95 <= 16,0 ms; no /ctx test row in the Q&A history'
$null = Send-PipeJson 'CTXSIM' 'off'
# Warm measurement convention: one Warmup wake right after E3 (recorded, must be <= 60 ms), then N = 10 scored samples.
$warmup = Get-WakeSample
Write-Host ('  Warmup  ' + (Format-WakeParts $warmup)) -ForegroundColor DarkGray
$wakes = @()
for ($i = 0; $i -lt 10; $i++) {
    $s = Get-WakeSample
    $wakes += $s
    Write-Host ('  WAKE {0,2} {1}' -f ($i + 1), (Format-WakeParts $s)) -ForegroundColor DarkGray
}
$null = Send-PipeLine '{"cmd":"PASSIVE"}'
$null = Set-Ui 'half'
$wakeMs = @($wakes | Where-Object { $null -ne $_.ms } | ForEach-Object { [double]$_.ms })
$wakeP50 = Get-Percentile $wakeMs 50
$wakeP95 = Get-Percentile $wakeMs 95
$hist = Send-PipeJson 'HISTORY' 'refresh'
$histIds = @($hist.rows | ForEach-Object { [long]$_.id })
$ctxInHistory = @($ctxIds + @($cData.flow.testIds) | Where-Object { $histIds -contains [long]$_ })
$e4Checks = [ordered]@{
    warmupUnder60 = ($null -ne $warmup.ms) -and ([double]$warmup.ms -le 60.0) -and ([string]$warmup.from -eq 'collapsed') -and ([string]$warmup.to -eq 'half')
    wakeSettled40 = [bool]$warmup.settledAt40 -and (@($wakes | Where-Object { -not [bool]$_.settledAt40 }).Count -eq 0)
    wakeParts  = (@($wakes | Where-Object { $null -eq $_.LevelParts.HwndCaptureMs -or $null -eq $_.LevelParts.WindowHeightMs -or $null -eq $_.LevelParts.ActivateMs -or $null -eq $_.LevelParts.FocusMs }).Count -eq 0)
    wakeShape  = (@($wakes | Where-Object { $_.from -ne 'collapsed' -or $_.to -ne 'half' -or -not [bool]$_.focused }).Count -eq 0) -and ($wakeMs.Count -eq 10)
    wakeP50    = ($null -ne $wakeP50) -and ($wakeP50 -le 10.0)
    wakeP95    = ($null -ne $wakeP95) -and ($wakeP95 -le 16.0)
    historyClean = ($ctxInHistory.Count -eq 0) -and [bool]$cData.checks.'e4.historyClean' -and (@($hist.rows | Where-Object { Test-LooksLikeTest ([string]$_.aiModel) ([string]$_.task) }).Count -eq 0)
}
$e4Pass = -not ($e4Checks.Values -contains $false)
$slowest = $wakes | Where-Object { $null -ne $_.ms } | Sort-Object { [double]$_.ms } -Descending | Select-Object -First 1
Write-Host ('  ColdFirstWake (report only, first wake after the E2 restart) ' + (Format-WakeParts $coldFirstWake)) -ForegroundColor DarkGray
Write-Result 'E4' $e4Pass ("Warmup {0} (<= 60) | WAKE N=10 P50 {1} / P95 {2} ms ({3}) | slowest {4} | history {5} rows, /ctx test rows in it: {6} | failed=[{7}]" -f `
    (Format-WakeParts $warmup), $wakeP50, $wakeP95, (($wakeMs | ForEach-Object { '{0:N2}' -f $_ }) -join ' / '),
    $(if ($slowest) { Format-WakeParts $slowest } else { 'n/a' }), $histIds.Count, $ctxInHistory.Count, (Get-FailedNames $e4Checks))

# --------------------------------------------- E5 ---------------------------------------------
Write-Step 'E5: settled idle Private WS <= 45,0 MB (hard 52,0), idle CPU 0,0 %, regression Verify-Task16b (-> Task16 ... Task03), git, CI'
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
Restore-UserState
if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored before the regression chain.' }
$null = Restart-Host $false
$chainArgs = @('-SkipBuild'); if ($FastE5) { $chainArgs += '-FastE5' }
$chainStartUtc = [DateTime]::UtcNow
$swChain = [System.Diagnostics.Stopwatch]::StartNew()
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Task16b.ps1') @chainArgs | Out-Host
$chainExit = $LASTEXITCODE
$chainSeconds = [math]::Round($swChain.Elapsed.TotalSeconds, 1)
# Gate 15 run 2 (Architect point 7b): the chain runs with -SkipLive, so the four scripts that can call Gemini (Task07 ask-live,
# Task09 live-e2e, Task10 live-e2e10, Task11 live-e2e11) must each report skipLive=true and liveCallAttempted=false in a
# report written during this chain. The full chain must have run all four; the -FastE5 chain runs only Task11.
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
$chainReport = if (Test-Path (Join-Path $HandoffDir 'task16b_verification.json')) { Get-Content (Join-Path $HandoffDir 'task16b_verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$gitEnd = Compare-NexusGitState $gitStart (Get-NexusGitState $RepoRoot) -RequireClean
$ci = [ordered]@{ checked = $false; note = 'GitHub API not queried' }
try {
    $runs = Invoke-RestMethod -Uri ("https://api.github.com/repos/bitmap1701/NEXUS-Home-Hub/actions/runs?branch={0}&per_page=5" -f [uri]::EscapeDataString($gitStart.Branch)) `
        -Headers @{ 'User-Agent' = 'nexus-verify-task15'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 10
    $run = @($runs.workflow_runs | Where-Object { $_.head_sha -eq $gitStart.Head } | Select-Object -First 1)
    $ci = if ($run.Count -gt 0) { [ordered]@{ checked = $true; status = $run[0].status; conclusion = $run[0].conclusion; url = $run[0].html_url } } else { [ordered]@{ checked = $true; status = 'no run for this HEAD yet' } }
} catch { $ci = [ordered]@{ checked = $false; note = "GitHub API not reachable: $($_.Exception.Message)" } }
$e5Checks = [ordered]@{
    privateWsUnder45 = ($null -ne $idleMb) -and ($idleMb -le 45.0)
    idleCpuZero      = ([math]::Round($idleCpu, 1) -eq 0.0)
    regressionChain  = ($chainExit -eq 0) -and ($null -ne $chainReport) -and [bool]$chainReport.allPassed
    zeroLiveCalls    = ($liveCalls.Count -eq 0) -and ($liveMissing.Count -eq 0)
    gitUnchanged     = $gitEnd.Same
}
$e5Pass = -not ($e5Checks.Values -contains $false)
Write-Result 'E5' $e5Pass ("settled idle Private WS median {0} MB (<= 45,0; hard 52,0; HUD {1}) | idle CPU {2} % ({3}) | Verify-Task16b chain exit {4} in {5} s (allPassed={6}) | live calls in chain: {11} (audited {12}; missing [{13}]) | git same={7} {8} | CI: {9} | failed=[{10}]" -f `
    $idleMb, $memJson.hudLabel, $idleCpu, ($cpuSeries -join '/'), $chainExit, $chainSeconds, $chainReport.allPassed, $gitEnd.Same, ($gitEnd.Reasons -join '; '),
    $(if ($ci.checked) { "{0} {1}" -f $ci.status, $ci.conclusion } else { $ci.note }), (Get-FailedNames $e5Checks),
    $liveCalls.Count, (@($liveAudit | Where-Object { $_.ranInChain } | ForEach-Object { $_.report }) -join ','), (@($liveMissing | ForEach-Object { $_.report }) -join ','))

$overall = $e1Pass -and $e2Pass -and $e3Pass -and $e4Pass -and $e5Pass
$report = [ordered]@{
    task = 'TASK-15'; gate = 'Gate 15'; timestamp = (Get-Date).ToUniversalTime().ToString('o'); machine = $env:COMPUTERNAME
    allPassed = $overall; overallPass = $overall; gitStart = $gitStart; fastE5 = [bool]$FastE5
    E1_BuildBomSelftest = [ordered]@{ pass = $e1Pass; checks = $e1Checks; buildExitCode = $buildCode; buildWarnings = $buildWarnings; buildErrors = $buildErrors; bomChecked = $bom.Checked; bomOffenders = $bom.Offenders; selftest = $cData }
    E2_Shields = [ordered]@{ pass = $e2Pass; checks = $e2Checks; antiCheat = $acRuns; sensitive = $sensRuns; acP95 = $acP95; sensP95 = $sensP95; running = $runningRes.ctx; leak = [ordered]@{ latest = $leakLatest; db = $leakDb; wal = $leakWal; messages = $leakMsg; dbPath = $db } }
    E3_CtxFlow = [ordered]@{ pass = $e3Pass; checks = $e3Checks; realWindow = $realStatus.ctx; clipShort = $clipShort.ctx; clipLong = $(if ($clipLong) { [ordered]@{ clipChars = ([string]$clipLong.ctx.ClipText).Length; blockChars = ([string]$clipLong.ctx.ContextBlock).Length } } else { $null }); clipSecretAttached = $(if ($clipSecret) { $null -ne $clipSecret.ctx.ClipText } else { $null }); first = $first; hitsMs = $hitMs; hitP95 = $hitP95; hitCount = $hitCount; mcp = $mcpResults; mcpWarmP95 = $mcpP95 }
    E4_Wake = [ordered]@{ pass = $e4Pass; checks = $e4Checks; coldFirstWake = $coldFirstWake; coldFirstWakeGraded = $false; settlePauseMs = 50; warmup = $warmup; warmupLimitMs = 60.0; n = 10; samples = $wakes; p50 = $wakeP50; p95 = $wakeP95; historyIds = $histIds; ctxIds = $ctxIds }
    E5_FootprintRegression = [ordered]@{ pass = $e5Pass; checks = $e5Checks; idlePrivateWsMb = $idleMb; idleRead = $idleRead; mem = $memJson; idleCpuPct = $idleCpu; cpuSeries = $cpuSeries; chainExit = $chainExit; chainSeconds = $chainSeconds; chainAllPassed = $chainReport.allPassed; liveAudit = $liveAudit; liveCallsInChain = $liveCalls.Count; gitReasons = $gitEnd.Reasons; ci = $ci }
}
[System.IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 10), $Utf8NoBom)
$null = Invoke-Mcp @(@{ name = 'notify_shift_complete'; arguments = [ordered]@{ agent = 'Claude Code'; gate = 'Gate 15'; status = $(if ($overall) { 'ALL PASS' } else { 'FAIL' })
    summary = ("Verify-Task15: E1 {0} E2 {1} E3 {2} E4 {3} E5 {4}; chặn P95 {5}/{6} ms, ColdFirstWake {12} ms, Warmup {9} ms, WAKE P50 {7} / P95 {10} ms, RAM {8} MB, live calls {11}" -f $e1Pass, $e2Pass, $e3Pass, $e4Pass, $e5Pass, $acP95, $sensP95, $wakeP50, $idleMb, $warmup.ms, $wakeP95, $liveCalls.Count, $coldFirstWake.ms) } })
Write-Host "`nGate 15 verification report written to $ReportPath (allPassed=$overall)" -ForegroundColor $(if ($overall) { 'Green' } else { 'Red' })
if (-not $overall) { exit 1 }
exit 0
}
finally {
    Restore-UserState
    if (Restore-NexusLatestDispatch $HandoffDir $latestBackup) { Write-Host '  latest_dispatch.json of the user restored.' }
    foreach ($k in $savedUserEnv.Keys) { Set-Item "Env:$k" $savedUserEnv[$k] }
}
