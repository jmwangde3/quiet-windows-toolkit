<#
.SYNOPSIS
  机械硬盘夜间静音：00:00-10:00 不整理、不扫盘；白天照常自动维护。

.DESCRIPTION
  保留 Windows 自动维护，保留 SysMain 预读，只加一道可逆的"夜间闸门"：
    · 23:58  停止并禁用会读写机械盘的计划任务
    · 10:02  重新启用，让自动维护在白天执行
    · 开机时  自检一次（若在 23:00-10:00 之间则保持禁用）
  同时把自动维护开始时间设为上午 10:00，并取消"允许维护唤醒计算机"。

  闸门任务：\QuietHDD\QuietHDD_NightGate，以 SYSTEM 运行
  闸门日志：C:\ProgramData\QuietHDD\gate.log

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-daytime.ps1"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-daytime.ps1" -Restore
#>
[CmdletBinding()]
param(
    [switch]$Restore,
    [switch]$NoElevate,
    [switch]$FromElevated
)

$ErrorActionPreference = 'Continue'

function Write-Step($m) { Write-Host ("[*] " + $m) -ForegroundColor Cyan }
function Write-OK($m)   { Write-Host ("[OK] " + $m) -ForegroundColor Green }
function Write-Warn2($m){ Write-Host ("[!!] " + $m) -ForegroundColor Yellow }

# 提权后脚本运行在 TEMP 副本里，保留原始路径供回滚提示
$OriginalPath = $PSCommandPath
$originFile = Join-Path $env:TEMP 'quiet-hdd-daytime.origin'
if ($FromElevated -and (Test-Path -LiteralPath $originFile)) {
    $OriginalPath = [System.IO.File]::ReadAllText($originFile, [System.Text.Encoding]::UTF8).Trim()
}

$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated) {
    Write-Step '需要管理员权限，正在请求 UAC 提权...'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-daytime.ps1'
    [System.IO.File]::WriteAllText((Join-Path $env:TEMP 'quiet-hdd-daytime.origin'), $PSCommandPath, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    $sw = ''
    if ($Restore) { $sw += ' -Restore' }
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated' + $sw
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        Write-OK '已在管理员窗口执行，请看那个窗口。'
    } catch {
        Write-Warn2 ('提权失败: ' + $_.Exception.Message)
        Write-Warn2 '请手动以管理员身份打开 PowerShell，运行本脚本并加 -NoElevate'
    }
    return
}

$Tasks = @(
    @('\Microsoft\Windows\Defrag\',              'ScheduledDefrag'),
    @('\Microsoft\Windows\DiskCleanup\',         'SilentCleanup'),
    @('\Microsoft\Windows\Data Integrity Scan\', 'Data Integrity Check And Scan'),
    @('\Microsoft\Windows\Data Integrity Scan\', 'Data Integrity Scan'),
    @('\Microsoft\Windows\Chkdsk\',              'ProactiveScan'),
    @('\Microsoft\Windows\Shell\',               'IndexerAutomaticMaintenance')
)
$GateDir        = 'C:\ProgramData\QuietHDD'
$GateScriptPath = Join-Path $GateDir 'gate.ps1'
$MaintKey       = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'

# ---------------- 回滚 ----------------
if ($Restore) {
    Write-Step '=== 回滚：删除夜间闸门并恢复默认 ==='
    $del = 0
    foreach ($tp in @('\QuietHDD\', '')) {
        try {
            if ($tp -eq '') { Unregister-ScheduledTask -TaskName 'QuietHDD_NightGate' -Confirm:$false -ErrorAction Stop | Out-Null }
            else { Unregister-ScheduledTask -TaskName 'QuietHDD_NightGate' -TaskPath $tp -Confirm:$false -ErrorAction Stop | Out-Null }
            $del++
        } catch { }
    }
    if ($del -gt 0) { Write-OK ('已删除闸门任务（' + $del + ' 处）') } else { Write-Warn2 '未找到闸门任务（本就不存在则正常）' }
    foreach ($p in $Tasks) {
        try { Enable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop | Out-Null; Write-OK ('已启用 ' + $p[1]) }
        catch { Write-Warn2 ('启用失败 ' + $p[1] + ' : ' + $_.Exception.Message) }
    }
    try {
        Remove-ItemProperty -Path $MaintKey -Name 'MaintenanceBoundary' -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $MaintKey -Name 'WakeUp' -ErrorAction SilentlyContinue
        Write-OK '自动维护时间已恢复系统默认 (02:00)'
    } catch { Write-Warn2 $_.Exception.Message }
    try { Set-Service -Name SysMain -StartupType Automatic -ErrorAction Stop; Start-Service -Name SysMain -ErrorAction SilentlyContinue; Write-OK 'SysMain 已恢复 Automatic' }
    catch { Write-Warn2 ('SysMain: ' + $_.Exception.Message) }
    try { Remove-Item -LiteralPath $GateDir -Recurse -Force -ErrorAction SilentlyContinue; Write-OK '已清理 C:\ProgramData\QuietHDD' } catch { }
    return
}

# ---------------- 1. 确保自动维护与 SysMain 正常 ----------------
Write-Step '=== 1/5 确保自动维护与 SysMain 处于正常状态 ==='
$hNow0 = (Get-Date).Hour
$isNight0 = ($hNow0 -ge 23 -or $hNow0 -lt 10)
if ($isNight0) {
    Write-OK ('当前 ' + (Get-Date -Format 'HH:mm') + ' 属于夜间静音时段，跳过启用，交给闸门处理')
} else {
    foreach ($p in $Tasks) {
        $t0 = Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
        if (-not $t0) { Write-OK ('跳过（本机无此任务）: ' + $p[1]); continue }
        try { Enable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction Stop | Out-Null; Write-OK ('已启用 ' + $p[1]) }
        catch { Write-Warn2 ('启用失败 ' + $p[1] + ' : ' + $_.Exception.Message) }
    }
}
$sys = Get-Service -Name SysMain -ErrorAction SilentlyContinue
if ($sys) {
    if ($sys.StartType -ne 'Automatic') { try { Set-Service -Name SysMain -StartupType Automatic -ErrorAction Stop } catch { } }
    if ($sys.Status -ne 'Running') { try { Start-Service -Name SysMain -ErrorAction Stop } catch { } }
    $sys = Get-Service -Name SysMain
    Write-OK ('SysMain = ' + $sys.Status + ' / ' + $sys.StartType + '（保留预读，不动）')
}

# ---------------- 2. 维护窗口改到 10:00，禁止唤醒 ----------------
Write-Step '=== 2/5 自动维护改到上午 10:00，且不唤醒计算机 ==='
try {
    New-Item -Path $MaintKey -Force -ErrorAction Stop | Out-Null
    New-ItemProperty -Path $MaintKey -Name 'MaintenanceBoundary' -Value 10 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
    New-ItemProperty -Path $MaintKey -Name 'WakeUp' -Value 0 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
    Write-OK 'MaintenanceBoundary = 10（上午10点）   WakeUp = 0（不唤醒）'
} catch { Write-Warn2 ('写维护设置失败: ' + $_.Exception.Message) }

# ---------------- 3. 写闸门脚本 ----------------
Write-Step '=== 3/5 写入夜间闸门脚本 ==='
$GateBody = @'
$dir = 'C:\ProgramData\QuietHDD'
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$log = Join-Path $dir 'gate.log'
$h = (Get-Date).Hour
$night = ($h -ge 23 -or $h -lt 10)
$tasks = @(
  @('\Microsoft\Windows\Defrag\','ScheduledDefrag'),
  @('\Microsoft\Windows\DiskCleanup\','SilentCleanup'),
  @('\Microsoft\Windows\Data Integrity Scan\','Data Integrity Check And Scan'),
  @('\Microsoft\Windows\Data Integrity Scan\','Data Integrity Scan'),
  @('\Microsoft\Windows\Chkdsk\','ProactiveScan'),
  @('\Microsoft\Windows\Shell\','IndexerAutomaticMaintenance')
)
$msg = @()
foreach($p in $tasks){
  $t = Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
  if(-not $t){ $msg += ('SKIP:' + $p[1]); continue }
  try {
    if($night){
      Stop-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
      Disable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue | Out-Null
      $msg += ('OFF:' + $p[1])
    } else {
      Enable-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue | Out-Null
      $msg += ('ON:' + $p[1])
    }
  } catch { $msg += ('ERR:' + $p[1] + '=' + $_.Exception.Message) }
}
if($night){
  Get-Process defrag -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $msg += 'KILL:defrag'
}
$mode = 'DAY'
if($night){ $mode = 'NIGHT' }
$line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] mode=' + $mode + ' ' + ($msg -join ' ')
[System.IO.File]::AppendAllText($log, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
'@
try {
    New-Item -ItemType Directory -Path $GateDir -Force -ErrorAction Stop | Out-Null
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($GateScriptPath, $GateBody, $utf8Bom)
    Write-OK ('已写入 ' + $GateScriptPath)
} catch { Write-Warn2 ('写闸门脚本失败: ' + $_.Exception.Message) }

# ---------------- 4. 注册闸门任务 ----------------
Write-Step '=== 4/5 注册夜间闸门任务（23:58 关 / 10:02 开 / 开机自检） ==='
try {
    $action  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\QuietHDD\gate.ps1"'
    $t1 = New-ScheduledTaskTrigger -Daily -At '23:58'
    $t2 = New-ScheduledTaskTrigger -Daily -At '10:02'
    $t3 = New-ScheduledTaskTrigger -AtStartup
    try { $t3.Delay = 'PT2M' } catch { }
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName 'QuietHDD_NightGate' -TaskPath '\QuietHDD\' -Action $action -Trigger @($t1, $t2, $t3) -Settings $settings -Principal $principal -Force -ErrorAction Stop | Out-Null
    Write-OK '已注册 \QuietHDD\QuietHDD_NightGate'
} catch {
    Write-Warn2 ('注册任务失败: ' + $_.Exception.Message)
    $fixPath = Join-Path (Split-Path -Parent $OriginalPath) 'quiet-hdd-fix.ps1'
    Write-Warn2 '闸门没装上 = 夜间不会静音，必须补这一步。用修复脚本重试（6 种注册方式）：'
    Write-Warn2 ('  powershell -ExecutionPolicy Bypass -File "' + $fixPath + '"')
}

# ---------------- 5. 立即按当前时间生效一次 ----------------
Write-Step '=== 5/5 按当前时间立即生效一次 ==='
try {
    & $GateScriptPath
    $hNow = (Get-Date).Hour
    $modeNow = 'DAY'
    if ($hNow -ge 23 -or $hNow -lt 10) { $modeNow = 'NIGHT' }
    Write-OK ('已执行，当前模式 = ' + $modeNow)
} catch { Write-Warn2 ('执行闸门失败: ' + $_.Exception.Message) }

# ---------------- 核对 ----------------
Write-Host ''
Write-Step '=== 核对 ==='
foreach ($p in $Tasks) {
    $t = Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
    if ($t) { Write-Host ('  ' + $p[1] + ' : ' + [string]$t.State) }
}
$v = Get-ItemProperty -Path $MaintKey -ErrorAction SilentlyContinue
Write-Host ('  MaintenanceBoundary : ' + $v.MaintenanceBoundary + '    WakeUp : ' + $v.WakeUp)
$s = Get-Service SysMain -ErrorAction SilentlyContinue
Write-Host ('  SysMain : ' + $s.Status + ' / ' + $s.StartType)
$g = Get-ScheduledTask -TaskName 'QuietHDD_NightGate' -ErrorAction SilentlyContinue
if ($g) {
    $gi = $g | Get-ScheduledTaskInfo
    Write-Host ('  NightGate : ' + [string]$g.State + '   NextRun=' + $gi.NextRunTime)
}
Write-Host ''
$hNow2 = (Get-Date).Hour
if ($hNow2 -ge 23 -or $hNow2 -lt 10) {
    Write-Host '  当前是夜间静音时段：上面显示 Disabled 属正常，10:02 会自动恢复。'
} else {
    Write-Host '  当前是白天时段：任务为启用状态，自动维护在系统空闲时执行（可能马上开始跑）。'
}
Write-Host '  23:58 关闭整理/清理/索引维护，10:02 重新打开；开机 2 分钟后也会自检。'
Write-Host '  Windows 自动维护与 SysMain 均保留，维护在 10:00 后系统空闲时执行。'
Write-Host ('  回滚：powershell -ExecutionPolicy Bypass -File "' + $OriginalPath + '" -Restore')
