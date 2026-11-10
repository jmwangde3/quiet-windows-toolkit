<#
.SYNOPSIS
  深夜机械硬盘降噪：停掉正在跑的磁盘整理，禁用自动维护里的碎片整理与夜间扫盘任务，
  并把 Windows 自动维护从 02:00 挪到白天。

.DESCRIPTION
  直接运行本脚本会自动请求管理员权限（UAC），提权后的窗口会保留输出。
  默认动作：
    1. 立即停止正在运行的 ScheduledDefrag / SilentCleanup / Data Integrity Scan /
       ProactiveScan / IndexerAutomaticMaintenance，并结束残留 defrag.exe
    2. 永久禁用上述会在夜间读写机械盘的任务
    3. 自动维护时间改为 14:00，并禁止维护唤醒计算机
    4. 关闭 SysMain(Superfetch) 预读，减少后台读盘
  默认不启用"硬盘空闲停转"，避免 14TB 企业盘反复启停的咔哒声。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-night.ps1"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-night.ps1" -Restore
#>
[CmdletBinding()]
param(
    [switch]$Restore,           # 回滚全部改动
    [switch]$KeepDefrag,         # 保留碎片整理任务（只改维护窗口时间）
    [switch]$KeepSysMain,        # 保留 SysMain 预读服务
    [switch]$SpinDownHDD,        # 空闲后让硬盘停转
    [int]$SpinDownMinutes = 30,
    [int]$MaintenanceHour = 14,  # 自动维护改到白天几点
    [switch]$FromElevated,       # 内部使用：标记已是提权后的实例
    [switch]$NoElevate           # 不自动提权（已在管理员窗口里运行时用）
)

$ErrorActionPreference = 'Continue'

# 提权后脚本会运行在 TEMP 副本里，保留原始路径供回滚提示使用
$OriginalPath = $PSCommandPath
$originFile = Join-Path $env:TEMP 'quiet-hdd-night.origin'
if ($FromElevated -and (Test-Path -LiteralPath $originFile)) {
    $OriginalPath = [System.IO.File]::ReadAllText($originFile, [System.Text.Encoding]::UTF8).Trim()
}

function Write-Step($msg) { Write-Host ("[*] " + $msg) -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host ("[OK] " + $msg) -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host ("[!!] " + $msg) -ForegroundColor Yellow }

# ---------- UAC 自提权 ----------
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated) {
    Write-Step '需要管理员权限，正在请求 UAC 提权...'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-night.ps1'
    [System.IO.File]::WriteAllText((Join-Path $env:TEMP 'quiet-hdd-night.origin'), $PSCommandPath, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    $sw = ''
    if ($Restore)      { $sw += ' -Restore' }
    if ($KeepDefrag)   { $sw += ' -KeepDefrag' }
    if ($KeepSysMain)  { $sw += ' -KeepSysMain' }
    if ($SpinDownHDD)  { $sw += ' -SpinDownHDD -SpinDownMinutes ' + $SpinDownMinutes }
    if ($MaintenanceHour -ne 14) { $sw += ' -MaintenanceHour ' + $MaintenanceHour }
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated' + $sw
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        Write-OK '已在弹出的管理员窗口执行，请查看那个窗口的输出。'
    } catch {
        Write-Warn2 ('提权失败: ' + $_.Exception.Message)
        Write-Warn2 '请手动以管理员身份打开 PowerShell，运行本脚本并加 -NoElevate'
    }
    return
}
if (-not $IsAdmin) { Write-Warn2 '当前不是管理员，以下改动可能失败。' }

# 会在夜间读写机械盘的任务
$DiskTasks = @(
    @('\Microsoft\Windows\Defrag\',                 'ScheduledDefrag'),
    @('\Microsoft\Windows\DiskCleanup\',            'SilentCleanup'),
    @('\Microsoft\Windows\Data Integrity Scan\',    'Data Integrity Check And Scan'),
    @('\Microsoft\Windows\Data Integrity Scan\',    'Data Integrity Scan'),
    @('\Microsoft\Windows\Data Integrity Scan\',    'Data Integrity Scan for Crash Recovery'),
    @('\Microsoft\Windows\Chkdsk\',                 'ProactiveScan'),
    @('\Microsoft\Windows\Shell\',                  'IndexerAutomaticMaintenance')
)

$MaintKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'

# ---------- 回滚 ----------
if ($Restore) {
    Write-Step '=== 回滚：恢复所有默认设置 ==='
    foreach ($p in $DiskTasks) {
        try { Enable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop | Out-Null; Write-OK ('已启用 ' + $p[1]) }
        catch { Write-Warn2 ('启用失败 ' + $p[1] + ' : ' + $_.Exception.Message) }
    }
    try {
        Remove-ItemProperty -Path $MaintKey -Name 'MaintenanceBoundary' -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $MaintKey -Name 'WakeUp' -ErrorAction SilentlyContinue
        Write-OK '自动维护时间已恢复为系统默认 (02:00)'
    } catch { Write-Warn2 $_.Exception.Message }
    try {
        Set-Service -Name SysMain -StartupType Automatic -ErrorAction Stop
        Start-Service -Name SysMain -ErrorAction SilentlyContinue
        Write-OK 'SysMain 已恢复 Automatic'
    } catch { Write-Warn2 ('SysMain 恢复失败: ' + $_.Exception.Message) }
    powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 0 2>&1 | Out-Null
    powercfg /s SCHEME_CURRENT 2>&1 | Out-Null
    Write-OK '交流电源下硬盘停转已恢复为"从不"'
    return
}

# ---------- 1. 即时止血 ----------
Write-Step '=== 1/5 停止当前正在运行的磁盘任务 ==='
foreach ($p in $DiskTasks) {
    try {
        $t = Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
        if ($t -and $t.State -eq 'Running') {
            Stop-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop
            Write-OK ('已停止 ' + $p[1])
        }
    } catch { Write-Warn2 ('停止失败 ' + $p[1] + ' : ' + $_.Exception.Message) }
}
$dp = Get-Process -Name defrag -ErrorAction SilentlyContinue
if ($dp) { $dp | Stop-Process -Force -ErrorAction SilentlyContinue; Write-OK '已结束残留 defrag.exe' }
else { Write-OK '当前没有 defrag.exe 在跑' }

# ---------- 2. 禁用夜间扫盘任务 ----------
Write-Step '=== 2/5 禁用夜间会读写机械盘的任务 ==='
foreach ($p in $DiskTasks) {
    if ($KeepDefrag -and $p[1] -eq 'ScheduledDefrag') { Write-OK ('按要求保留 ' + $p[1]); continue }
    try { Disable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop | Out-Null; Write-OK ('已禁用 ' + $p[1]) }
    catch { Write-Warn2 ('禁用失败 ' + $p[1] + ' : ' + $_.Exception.Message) }
}
$coord = Get-ScheduledTask -TaskPath '\Microsoft\Windows\TaskScheduler\' -TaskName 'Regular Maintenance' -ErrorAction SilentlyContinue
if ($coord) {
    try { Disable-ScheduledTask -TaskPath '\Microsoft\Windows\TaskScheduler\' -TaskName 'Regular Maintenance' -ErrorAction Stop | Out-Null; Write-OK '已禁用 Regular Maintenance 协调器' }
    catch { Write-Warn2 ('禁用协调器失败: ' + $_.Exception.Message) }
} else { Write-OK '本机无 Regular Maintenance 协调器（由维护设置直接调度），跳过' }

# ---------- 3. 自动维护窗口改到白天 ----------
Write-Step ('=== 3/5 把自动维护窗口改到 ' + $MaintenanceHour + ':00，并禁止唤醒 ===')
try {
    New-Item -Path $MaintKey -Force -ErrorAction Stop | Out-Null
    New-ItemProperty -Path $MaintKey -Name 'MaintenanceBoundary' -Value $MaintenanceHour -PropertyType DWord -Force -ErrorAction Stop | Out-Null
    New-ItemProperty -Path $MaintKey -Name 'WakeUp' -Value 0 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
    Write-OK ('维护窗口 = ' + $MaintenanceHour + ':00，WakeUp = 0')
    Write-Warn2 '若控制面板仍显示 02:00，请手动：控制面板 -> 安全性与维护 -> 自动维护 -> 更改维护设置'
} catch { Write-Warn2 ('写维护时间失败: ' + $_.Exception.Message) }

# ---------- 4. 后台服务 ----------
Write-Step '=== 4/5 后台服务 ==='
if ($KeepSysMain) { Write-OK '按要求保留 SysMain' }
else {
    try {
        Stop-Service -Name SysMain -Force -ErrorAction SilentlyContinue
        Set-Service -Name SysMain -StartupType Disabled -ErrorAction Stop
        Write-OK 'SysMain(Superfetch) 已关闭并禁用'
    } catch { Write-Warn2 ('SysMain 处理失败: ' + $_.Exception.Message) }
}
Write-OK 'defragsvc 保持 Manual（不主动启动，仅供白天手动"优化驱动器"使用）'

# ---------- 5. 可选：空闲停转 ----------
Write-Step '=== 5/5 硬盘空闲停转（可选） ==='
if ($SpinDownHDD) {
    $sec = $SpinDownMinutes * 60
    powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec 2>&1 | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec 2>&1 | Out-Null
    powercfg /s SCHEME_CURRENT 2>&1 | Out-Null
    Write-OK ('空闲 ' + $SpinDownMinutes + ' 分钟后停转')
} else {
    Write-OK '保持"从不"停转（避免 14TB 盘反复启停的咔哒声）'
}

# ---------- 结果核对 ----------
Write-Host ''
Write-Step '=== 结果核对 ==='
foreach ($p in $DiskTasks) {
    $t = Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
    if ($t) { Write-Host ('  ' + $p[1] + ' : ' + $t.State) }
}
$v = Get-ItemProperty $MaintKey -ErrorAction SilentlyContinue
Write-Host ('  MaintenanceBoundary : ' + $v.MaintenanceBoundary + '   WakeUp : ' + $v.WakeUp)
$s = Get-Service SysMain -ErrorAction SilentlyContinue
Write-Host ('  SysMain : ' + $s.Status + ' / ' + $s.StartType)
Write-Host ''
Write-Step '当前机械盘 IO（2 秒采样）'
Get-Counter '\LogicalDisk(J:)\Disk Bytes/sec','\LogicalDisk(D:)\Disk Bytes/sec','\LogicalDisk(E:)\Disk Bytes/sec' -SampleInterval 1 -MaxSamples 2 -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CounterSamples | ForEach-Object { Write-Host ('  ' + ($_.Path -replace '.*logicaldisk','LogicalDisk') + ' : ' + [math]::Round($_.CookedValue,0) + ' B/s') }
}
Write-Host ''
Write-OK '完成。今晚不会再被自动整理吵醒。'
Write-Host '  白天手动整理：此电脑 -> 右键 J: -> 属性 -> 工具 -> 优化'
Write-Host ('  回滚：powershell -ExecutionPolicy Bypass -File "' + $OriginalPath + '" -Restore')
