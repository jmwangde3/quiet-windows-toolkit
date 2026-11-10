# 关闭 RTSS 中两个游戏的 Scanline Sync（会加最多 1 帧延迟）
# 用法：右键 -> 使用 PowerShell 运行（会自动请求管理员权限）
# 运行前请先从托盘退出 RTSS，否则退出时可能覆盖文件

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限，正在提权..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$profiles = "C:\Program Files (x86)\RivaTuner Statistics Server\Profiles"
$targets  = @("ff7rebirth_.exe.cfg", "yysls.exe.cfg")
$stamp    = Get-Date -Format "yyyyMMdd_HHmmss"

$running = Get-Process -Name "RTSS" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "RTSS 正在运行。请先从托盘图标退出 RTSS 再运行本脚本。" -ForegroundColor Red
    Write-Host "（否则 RTSS 退出时会把内存里的旧配置写回去）"
    Read-Host "按回车退出"
    exit 1
}

foreach ($f in $targets) {
    $src = Join-Path $profiles $f
    if (-not (Test-Path $src)) { Write-Host "[skip] $f 不存在" -ForegroundColor DarkGray; continue }

    $bak = "$src.bak_$stamp"
    Copy-Item -LiteralPath $src -Destination $bak -Force

    $raw = Get-Content -LiteralPath $src -Raw
    $new = $raw -replace '(?m)^SyncScanline0=\d+', 'SyncScanline0=0' `
                -replace '(?m)^SyncScanline1=\d+', 'SyncScanline1=0' `
                -replace '(?m)^SyncPeriods=\d+',   'SyncPeriods=0'

    if ($new -eq $raw) {
        Write-Host "[nochange] $f 本来就是 0" -ForegroundColor DarkGray
        Remove-Item -LiteralPath $bak -Force
    } else {
        Set-Content -LiteralPath $src -Value $new -NoNewline -Encoding ASCII
        Write-Host "[fixed] $f  (备份: $([IO.Path]::GetFileName($bak)))" -ForegroundColor Green
    }
}

Write-Host "`n--- 当前状态 ---"
foreach ($f in $targets) {
    $src = Join-Path $profiles $f
    if (Test-Path $src) {
        Get-Content -LiteralPath $src | Select-String '^SyncScanline|^SyncPeriods' | ForEach-Object { "$f : $($_.Line)" }
    }
}

Write-Host "`n完成。重新启动 RTSS 即可生效。" -ForegroundColor Green
Read-Host "按回车关闭"
