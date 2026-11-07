<#
.SYNOPSIS
    TASK-09 ADR-38: nạp nhanh Gemini API key vào DPAPI vault của NEXUS Home Hub (giống lệnh Cockpit "/apikey set <KEY>").

.DESCRIPTION
    - Khóa được nhập bằng Read-Host -AsSecureString: không hiện trên màn hình, không nằm trong tham số dòng lệnh,
      lịch sử PowerShell hay log.
    - Mã hóa Windows DPAPI (CurrentUser, entropy NEXUS_HOME_HUB_DPAPI_V1) đúng định dạng DpapiSecretVault, ghi
      03_Local_Data_Vault\config\gemini_key.dpapi (thư mục config\ và *.dpapi đã git-ignore).
    - Sau đó chạy "HostService --selftest apikey-status" để chính HostService giải mã và in khóa đã che
      (AIza...xyz (39 chars, Source: DPAPI)). Khóa thật không bao giờ được in ra.
    Tiếp theo: chạy Verify-Task09.ps1, mục E5 sẽ tự gọi Gemini thật.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\01_Foundation_Engine\tools\Set-NexusGeminiKey.ps1
#>
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8

$configDir = Join-Path $Root '03_Local_Data_Vault\config'
$vault     = Join-Path $configDir 'gemini_key.dpapi'
$handoff   = Join-Path $Root '03_Local_Data_Vault\handoff'
$hostExe   = Join-Path $Root '01_Foundation_Engine\NEXUSHome.HostService\bin\Release\net8.0-windows\NEXUSHome.HostService.exe'

$secure = Read-Host -AsSecureString 'Dán Gemini API key (không hiện trên màn hình) rồi Enter'
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
$plainBytes = $null
try {
    $plain = ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)).Trim()
    if ($plain.Length -eq 0) { throw 'Không nhận được ký tự nào. Dán khóa bằng Ctrl+V hoặc chuột phải (ô nhập không hiện gì là bình thường) rồi Enter.' }
    if ($plain.Length -lt 20 -or $plain.Length -gt 256) { throw "Khóa phải dài 20..256 ký tự (nhận được $($plain.Length) ký tự)." }
    if ($plain -match '\s') { throw 'Khóa không được chứa khoảng trắng.' }
    if (-not $plain.StartsWith('AIza')) { Write-Host 'Lưu ý: khóa Google AI Studio thường bắt đầu bằng "AIza".' -ForegroundColor Yellow }

    Add-Type -AssemblyName System.Security
    $entropy = [System.Text.Encoding]::UTF8.GetBytes('NEXUS_HOME_HUB_DPAPI_V1')
    $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
    $blob = [System.Security.Cryptography.ProtectedData]::Protect($plainBytes, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)

    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $temp = "$vault.$PID.tmp"
    [System.IO.File]::WriteAllBytes($temp, $blob)
    if (Test-Path $vault) { [System.IO.File]::Replace($temp, $vault, $null) } else { [System.IO.File]::Move($temp, $vault) }
    Write-Host "Đã mã hóa DPAPI (CurrentUser) -> $vault" -ForegroundColor Green
} finally {
    if ($plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    Remove-Variable plain -ErrorAction SilentlyContinue
}

if (Test-Path $hostExe) {
    $out = Join-Path $handoff 'task09_selftest_apikey-status.json'
    if (Test-Path $out) { Remove-Item $out }
    Start-Process -FilePath $hostExe -ArgumentList @('--selftest', 'apikey-status') -Wait
    if (Test-Path $out) {
        $s = Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
        $color = if ($s.pass) { 'Green' } else { 'Red' }
        Write-Host ("HostService đọc được khóa: {0} | nguồn {1}{2}" -f $s.masked, $s.source, $(if ($s.error) { " | lỗi: $($s.error)" } else { '' })) -ForegroundColor $color
    } else {
        Write-Host 'Không có báo cáo apikey-status (HostService chưa build bản TASK-09?).' -ForegroundColor Yellow
    }
} else {
    Write-Host "Chưa có $hostExe (chưa build): bỏ qua bước kiểm tra giải mã." -ForegroundColor Yellow
}
