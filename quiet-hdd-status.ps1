<#
.SYNOPSIS
  夜间静音状态板：一条命令看清闸门任务、维护窗口、日志与机械盘实时 IO。

.DESCRIPTION
  \QuietHDD\ 任务文件夹的 ACL 只给管理员/SYSTEM 读，
  普通 PowerShell 里 Get-ScheduledTask 看不到闸门任务，会被误判成"没装"。
  本脚本自动提权后只读查询，不做任何修改。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-status.ps1"
#>
[CmdletBinding()]
param(
    [switch]$NoElevate,
    [switch]$FromElevated
)

$ErrorActionPreference = 'Continue'
$GateDir = 'C:\ProgramData\QuietHDD'
$TaskName = 'QuietHDD_NightGate'

function P($Text, $Color = 'Gray') { Write-Host $Text -ForegroundColor $Color }

# ---------------- 提权（只读检查，仍需管理员才能看 \QuietHDD\） ----------------
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated) {
    P '需要管理员权限才能读取 \QuietHDD\ 任务文件夹，正在请求 UAC...' 'Cyan'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-status.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated'
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        P '已在管理员窗口执行，请看那个窗口。' 'Green'
    } catch {
        P ('提权失败: ' + $_.Exception.Message) 'Yellow'
    }
    return
}

$now = Get-Date
P '========================================' 'Cyan'
P ('QuietHDD 状态板   ' + $now.ToString('yyyy-MM-dd HH:mm:ss') + '   ' + $now.DayOfWeek) 'Cyan'
P ('管理员: ' + $IsAdmin + '   PS: ' + $PSVersionTable.PSVersion.ToString()) 'Cyan'
P '========================================' 'Cyan'

# ---------------- 1. 维护窗口 ----------------
P '' ; P '[1] Windows 自动维护设置' 'Cyan'
$mk = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'
$v = Get-ItemProperty -Path $mk -ErrorAction SilentlyContinue
$mb = $v.MaintenanceBoundary
$wu = $v.WakeUp
if ($null -eq $mb) { P '    MaintenanceBoundary = (未设置 -> 系统默认 02:00)' 'Yellow' }
else { P ('    MaintenanceBoundary = ' + $mb + ':00   ' + $(if ([int]$mb -eq 10) { 'OK 白天10点' } else { '注意：不是 10 点' })) $(if ([int]$mb -eq 10) { 'Green' } else { 'Yellow' }) }
if ($null -eq $wu) { P '    WakeUp = (未设置 -> 默认允许唤醒)' 'Yellow' }
else { P ('    WakeUp = ' + $wu + '   ' + $(if ([int]$wu -eq 0) { 'OK 不唤醒' } else { '注意：允许唤醒' })) $(if ([int]$wu -eq 0) { 'Green' } else { 'Yellow' }) }

# ---------------- 2. 闸门任务 ----------------
P '' ; P '[2] 夜间闸门任务' 'Cyan'
$g = $null
foreach ($tp in @('\QuietHDD\', '')) {
    try {
        if ($tp -eq '') { $g = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop }
        else { $g = Get-ScheduledTask -TaskPath $tp -TaskName $TaskName -ErrorAction Stop }
        if ($g) { break }
    } catch { }
}
if ($g) {
    $gi = $g | Get-ScheduledTaskInfo
    P ('    路径: ' + $g.TaskPath + $g.TaskName) 'Green'
    P ('    状态: ' + [string]$g.State + '   身份: ' + $g.Principal.UserId + ' / ' + $g.Principal.LogonType + ' / ' + $g.Principal.RunLevel) 'Green'
    P ('    上次: ' + $gi.LastRunTime + '   下次: ' + $gi.NextRunTime + '   结果码: ' + $gi.LastTaskResult) 'Green'
    foreach ($t in $g.Triggers) {
        $kind = $t.CimClass.CimClassName
        P ('    触发器: ' + $kind + '   Start=' + $t.StartBoundary + '   Delay=' + $t.Delay + '   Enabled=' + $t.Enabled) 'Green'
    }
    P ('    动作: ' + $g.Actions[0].Execute + ' ' + $g.Actions[0].Arguments) 'Gray'
    $gs = $g.Settings
    P ('    设置: StartWhenAvailable=' + $gs.StartWhenAvailable + '  WakeToRun=' + $gs.WakeToRun + '  多实例=' + $gs.MultipleInstances + '  仅在空闲=' + $gs.RunOnlyIfIdle + '  限时=' + $gs.ExecutionTimeLimit) 'Gray'
    $nyb = $null
    $nt = @($g.Triggers) | Where-Object { $_.StartBoundary -ne $null -and $_.StartBoundary -ne '' } | ForEach-Object { [datetime]$_.StartBoundary } | Sort-Object | Select-Object -First 1
    if ($nt) { P ('    最早每日触发时刻: ' + $nt.ToString('HH:mm') + '（23:58 关 / 10:02 开）') 'Gray' }
} else {
    P '    未找到任务 —— 闸门没装上，夜间不会静音' 'Red'
    P '    修复: powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-fix.ps1"' 'Yellow'
}

# ---------------- 3. 受管任务状态 ----------------
P '' ; P '[3] 受闸门管理的维护任务' 'Cyan'
$list = @(
    @('\Microsoft\Windows\Defrag\',              'ScheduledDefrag'),
    @('\Microsoft\Windows\DiskCleanup\',         'SilentCleanup'),
    @('\Microsoft\Windows\Data Integrity Scan\', 'Data Integrity Check And Scan'),
    @('\Microsoft\Windows\Data Integrity Scan\', 'Data Integrity Scan'),
    @('\Microsoft\Windows\Chkdsk\',              'ProactiveScan'),
    @('\Microsoft\Windows\Shell\',               'IndexerAutomaticMaintenance')
)
$h = $now.Hour
$isNight = ($h -ge 23 -or $h -lt 10)
foreach ($p in $list) {
    $t = Get-ScheduledTask -TaskPath $p[0] -TaskName $p[1] -ErrorAction SilentlyContinue
    if (-not $t) { P ('    ' + $p[1].PadRight(32) + ' 本机无此任务') 'DarkGray'; continue }
    $ti = $t | Get-ScheduledTaskInfo
    $state = [string]$t.State
    $expect = '待机'
    $color = 'Gray'
    if ($isNight) { if ($state -eq 'Disabled') { $expect = '符合(夜间应禁用)'; $color = 'Green' } else { $expect = '不符(夜间却是' + $state + ')'; $color = 'Red' } }
    else { if ($state -ne 'Disabled') { $expect = '符合(白天应启用)'; $color = 'Green' } else { $expect = '不符(白天却是禁用)'; $color = 'Red' } }
    P ('    ' + $p[1].PadRight(32) + ' ' + $state.PadRight(10) + ' 下次=' + $ti.NextRunTime + '   ' + $expect) $color
}

# ---------------- 4. 服务与进程 ----------------
P '' ; P '[4] 服务与进程' 'Cyan'
Get-Service SysMain, defragsvc -ErrorAction SilentlyContinue | ForEach-Object { P ('    ' + $_.Name.PadRight(10) + ' ' + $_.Status + ' / ' + $_.StartType) }
$dp = Get-Process defrag -ErrorAction SilentlyContinue
if ($dp) { P ('    defrag.exe 正在运行 PID=' + ($dp.Id -join ',')) 'Red' } else { P '    defrag.exe 未运行' 'Green' }

# ---------------- 5. 闸门日志 ----------------
P '' ; P '[5] 闸门日志（最近 8 条）' 'Cyan'
$gl = Join-Path $GateDir 'gate.log'
if (Test-Path -LiteralPath $gl) {
    Get-Content -LiteralPath $gl -Encoding UTF8 -Tail 8 | ForEach-Object { P ('    ' + $_) 'Gray' }
} else { P '    暂无日志（闸门还没跑过）' 'Yellow' }

# ---------------- 6. 机械盘实时 IO ----------------
P '' ; P '[6] 机械盘实时 IO（3 次采样，1 秒间隔）' 'Cyan'
$drives = @('J:', 'D:', 'E:')
$paths = $drives | ForEach-Object { '\LogicalDisk(' + $_ + ')\Disk Bytes/sec' }
try {
    $samples = Get-Counter $paths -SampleInterval 1 -MaxSamples 3 -ErrorAction Stop
    foreach ($obj in $samples) {
        $line = ''
        foreach ($s in $obj.CounterSamples) {
            $vol = ($s.Path -replace '.*logicaldisk\(', '') -replace '\).*', ''
            $line += ('  ' + $vol + '=' + [math]::Round($s.CookedValue / 1KB, 0) + 'KB/s')
        }
        P ('   ' + $line) 'Gray'
    }
} catch { P ('    采样失败: ' + $_.Exception.Message) 'Yellow' }

# ---------------- 结论 ----------------
P '' ; P '========================================' 'Cyan'
if ($g -and $mb -eq 10 -and $wu -eq 0) {
    P '  结论: 夜间静音已就位。23:58 关任务 / 10:02 开任务。' 'Green'
} elseif ($g) {
    P '  结论: 闸门任务在，但维护窗口设置不完整（看 [1]）。' 'Yellow'
} else {
    P '  结论: 闸门任务缺失，夜间不保证静音。' 'Red'
}
P ('  当前时段: ' + $(if ($isNight) { '夜间 23:00-10:00（任务应为 Disabled）' } else { '白天 10:00-23:00（任务应为 Enabled）' })) 'Cyan'
P ('  本机时钟: ' + $now.ToString('yyyy-MM-dd HH:mm:ss')) 'Cyan'
P '========================================' 'Cyan'
