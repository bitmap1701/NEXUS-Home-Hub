<#
.SYNOPSIS
    TASK-17b (ADR-70) "đo trước": where the idle memory of the FULL HostService goes, and which .NET runtime knob lowers it.
.DESCRIPTION
    Not a Gate run. For each variant (environment variables read by the .NET runtime at process start, nothing in the repo changes):
      default  = as shipped (v0.17.1 runtime settings)
      pgo0     = DOTNET_TieredPGO=0          (no dynamic-PGO instrumentation / extra code versions)
      gcnc     = DOTNET_gcConcurrent=0       (no background GC thread)
      tc0      = DOTNET_TieredCompilation=0  (one fully optimized JIT copy per method, no call-counting stubs)
      gen0     = DOTNET_GCgen0size=0x400000  (4 MB gen0 budget)
      combo    = pgo0 + gcnc + gen0
    1. selftest host-footprint (app, cockpit, warm-up, hotkeys, private test pipe, tray, load, /net, /mic list, hidden + trim)
    2. unless -SelfTestOnly: the real HostService restarted with the variant + mock Gemini (0 tokens), a Task16b-like load over
       the pipe (mock /ask, /calc, history, /net), HIDE, then the settled idle Private WS exactly as Verify-Task16b E5 reads it.
    At the end the HostService is started again with the user's normal settings. About 5 minutes; leave the PC idle.
    Output: table + 03_Local_Data_Vault/handoff/task17b_footprint_variants.json (git-ignored).
#>
param(
    [switch]$SkipBuild,
    [switch]$SelfTestOnly,
    [string[]]$Variants = @('default', 'pgo0', 'gcnc', 'tc0', 'gen0', 'combo')
)
$ErrorActionPreference = 'Stop'
$EngineRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot   = (Resolve-Path (Join-Path $EngineRoot '..')).Path
$HandoffDir = Join-Path $RepoRoot '03_Local_Data_Vault\handoff'
$HostExe    = Join-Path $EngineRoot 'NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'
$OutPath    = Join-Path $HandoffDir 'task17b_footprint_variants.json'
$PipeName   = 'NEXUS_Home_Cockpit_Pipe'
$Utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$MockAnswer = "**Gate 17b** mock: dùng ``Span<T>``.`n`n- ý một`n- ý hai`n`nXong."
$script:PipeReplyTimeoutMs = 30000
. (Join-Path $PSScriptRoot 'NexusVerify.Common.ps1')

$Knobs = [ordered]@{
    default = @{}
    pgo0    = @{ DOTNET_TieredPGO = '0' }
    gcnc    = @{ DOTNET_gcConcurrent = '0' }
    tc0     = @{ DOTNET_TieredCompilation = '0' }
    gen0    = @{ DOTNET_GCgen0size = '0x400000' }
    combo   = @{ DOTNET_TieredPGO = '0'; DOTNET_gcConcurrent = '0'; DOTNET_GCgen0size = '0x400000' }
}
$KnobNames = 'DOTNET_TieredPGO', 'DOTNET_gcConcurrent', 'DOTNET_TieredCompilation', 'DOTNET_GCgen0size', 'NEXUS_GEMINI_MOCK_RESPONSE'
$savedEnv = @{}
foreach ($v in $KnobNames) { if (Test-Path "Env:$v") { $savedEnv[$v] = (Get-Item "Env:$v").Value; Remove-Item "Env:$v" } }

function Send-PipeLine([string]$line, [int]$timeoutMs = 5000) {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect($timeoutMs)
        $writer = [System.IO.StreamWriter]::new($client, $Utf8NoBom); $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, $Utf8NoBom)
        $writer.WriteLine($line)
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($script:PipeReplyTimeoutMs)) { throw "HostService did not answer $line within 30 s" }
        return $task.Result
    } finally { $client.Dispose() }
}
function Send-PipeJson([string]$cmd, [string]$text) {
    $req = if ($text) { [ordered]@{ cmd = $cmd; text = $text } } else { [ordered]@{ cmd = $cmd } }
    return ([string](Send-PipeLine ($req | ConvertTo-Json -Compress) | ConvertFrom-Json).result | ConvertFrom-Json)
}
function Stop-HostService {
    if (-not (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { return }
    try { Send-PipeLine '{"cmd":"SHUTDOWN"}' | Out-Null } catch { }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
    if (Get-Process -Name 'NEXUSHome.HostService' -ErrorAction SilentlyContinue) {
        throw 'HostService did not exit after SHUTDOWN over the pipe. Close it yourself (Task Manager), then re-run.'
    }
}
function Start-HostService {
    Start-Process -FilePath $HostExe | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) { try { $null = Send-PipeJson 'SYNCSTATE' $null; break } catch { Start-Sleep -Milliseconds 300 } }
    Start-Sleep -Seconds 3
    return (Get-Process -Name 'NEXUSHome.HostService' | Select-Object -First 1)
}
function Set-Knobs($knobs) {
    foreach ($v in $KnobNames) { if (Test-Path "Env:$v") { Remove-Item "Env:$v" } }
    foreach ($k in $knobs.Keys) { Set-Item "Env:$k" $knobs[$k] }
}

if (-not $SkipBuild) {
    Stop-HostService # the running HostService locks its DLLs in bin\Release (MSB3027); started again at the end
    Write-Host 'Release build (-warnaserror)...' -ForegroundColor Cyan
    $buildText = (& dotnet build $EngineRoot -c Release -warnaserror -v minimal 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { Write-Host $buildText; throw 'Build failed.' }
}
if (-not (Test-Path $HostExe)) { throw "Missing $HostExe (build first)" }

$rows = @()
try {
    foreach ($name in $Variants) {
        if (-not $Knobs.Contains($name)) { throw "Unknown variant $name (use: $($Knobs.Keys -join ', '))" }
        Write-Host "`n=== $name ===" -ForegroundColor Cyan
        Set-Knobs $Knobs[$name]
        $stPath = Join-Path $HandoffDir 'task17b_selftest_host-footprint.json'
        if (Test-Path $stPath) { Remove-Item $stPath -Force }
        $p = Start-Process -FilePath $HostExe -ArgumentList @('--selftest', 'host-footprint') -PassThru -Wait
        $st = if (Test-Path $stPath) { Get-Content $stPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        $row = [ordered]@{ variant = $name; knobs = $Knobs[$name]; selftestExit = $p.ExitCode; selftestMs = $st.totalMs
            selftestIdleMb = $st.summary.idlePrivateWsMb; selftestGcMb = $st.summary.idleGcHeapMb
            cockpitMb = $st.summary.cockpitMb; outsideMb = $st.summary.outsideCockpitMb; perPart = $st.summary.perPart; phases = $st.phases
            hostIdleMb = $null; hostGcMb = $null; hostThreads = $null; hostRead = $null; hostError = $null }
        if (-not $SelfTestOnly) {
            try {
                Stop-HostService
                $env:NEXUS_GEMINI_MOCK_RESPONSE = $MockAnswer
                $hostProc = Start-HostService
                $null = Send-PipeJson 'COCKPIT' ('/ask! Gate 17b đo RAM ' + [guid]::NewGuid().ToString('N').Substring(0, 8))
                Start-Sleep -Milliseconds 1500
                $null = Send-PipeJson 'COCKPIT' '/calc sqrt(144)+2^8'
                $null = Send-PipeJson 'HISTORY' 'refresh'
                for ($i = 0; $i -lt 10; $i++) { $null = Send-PipeJson 'HISTORY' ([string]$i) }
                $null = Send-PipeJson 'HISTORY' 'live'
                $null = Send-PipeJson 'COCKPIT' '/net'
                Start-Sleep -Milliseconds 800
                $null = Send-PipeLine '{"cmd":"HIDE"}'
                Start-Sleep -Milliseconds 8000
                $read = Get-NexusSettledPrivateWs -ProcessId $hostProc.Id -LastTrimUtc {
                    $mm = $null; try { $mm = Send-PipeJson 'MEM' $null } catch { }
                    if ($mm -and $mm.lastTrim) { $mm.lastTrim.AtUtc } else { $null }
                }
                $mem = Send-PipeJson 'MEM' $null
                $row.hostIdleMb = $read.MedianMb; $row.hostRead = $read
                $row.hostGcMb = $mem.snapshot.GcHeapSizeMb; $row.hostThreads = $mem.snapshot.ThreadCount
            } catch { $row.hostError = $_.Exception.Message }
        }
        $rows += $row
        Write-Host ("  selftest idle {0} MB (GC {1}; cockpit {2}, outside {3}; {4} ms) | full host idle {5} MB (GC {6}, threads {7}) {8}" -f `
            $row.selftestIdleMb, $row.selftestGcMb, $row.cockpitMb, $row.outsideMb, $row.selftestMs, $row.hostIdleMb, $row.hostGcMb, $row.hostThreads, $row.hostError)
    }
} finally {
    Set-Knobs @{}
    foreach ($k in $savedEnv.Keys) { Set-Item "Env:$k" $savedEnv[$k] }
    if (-not $SelfTestOnly) { try { Stop-HostService; $null = Start-HostService; Write-Host "`nHostService started again with the normal settings." } catch { Write-Host $_.Exception.Message -ForegroundColor Yellow } }
}

Write-Host "`nVariant   selftest idle   full host idle   GC heap   outside Cockpit   ms" -ForegroundColor Cyan
foreach ($r in $rows) { Write-Host ("{0,-9} {1,13} {2,16} {3,9} {4,17} {5,5}" -f $r.variant, $r.selftestIdleMb, $r.hostIdleMb, $r.hostGcMb, $r.outsideMb, $r.selftestMs) }
$first = $rows | Select-Object -First 1
if ($first -and $first.perPart) {
    Write-Host "`nParts outside the Cockpit window ($($first.variant), MB):" -ForegroundColor Cyan
    $first.perPart.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-14} {1}" -f $_.Name, $_.Value) }
}
$git = Get-NexusGitState $RepoRoot
[System.IO.File]::WriteAllText($OutPath, ([ordered]@{ measuredAt = (Get-Date).ToString('o'); head = $git.Head; branch = $git.Branch; rows = $rows } | ConvertTo-Json -Depth 8), $Utf8NoBom)
Write-Host "`nWritten $OutPath"
