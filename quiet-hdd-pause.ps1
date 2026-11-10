<#
.SYNOPSIS
  临时暂停 / 恢复 机械盘夜间脱机闸门（QuietHDD_DiskGate）。

.DESCRIPTION
  你已装好设备层静音：每晚 23:58 把 Disk0(J:) / Disk1(D:,E:) 脱机，10:02 联机。
  如果某晚你要用这些机械盘，跑 -Pause 让今晚不脱机；用完跑 -Resume 恢复日常静音。

  -Pause          禁用 DiskGate 任务，并确保 Disk0/Disk1 联机 -> 今晚随意读写 J/D/E
  -Resume         重新启用 DiskGate 任务 -> 下一个 23:58 起恢复脱机/联机节奏
                  （只启用任务，不动盘：今晚剩下的时间盘保持联机，不会再脱机）
  -Resume -ReArm  启用任务后立刻再跑一次闸门（按当前时钟判定）：
                  深夜用 -ReArm 会马上重新脱机，把今晚剩下的时间也恢复静音
  -ReArm          只按当前时钟跑一次闸门，不动任务（单独用）
  -Status         只读：任务 State + 各盘 IsOffline

  注意：NightGate（关系统维护任务）不挡你手动用盘，无需暂停。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Pause
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Resume
.EXAMPLE
  # 凌晨用完盘：恢复任务，并立刻把盘重新脱机（今晚剩下的时间继续静音）
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Resume -ReArm
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-pause.ps1" -Status
#>
[CmdletBinding()]
param(
    [switch]$Pause,
    [switch]$Resume,
    [switch]$Status,
    [switch]$ReArm,
    [switch]$NoElevate,
    [switch]$FromElevated
)

$ErrorActionPreference = 'Continue'
function P($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

$TaskPath = '\QuietHDD\'
$TaskName = 'QuietHDD_DiskGate'
$DiskNumbers = @(0, 1)

# -------- 提权（任务 ACL 仅管理员/SYSTEM 可读写） --------
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated) {
    P '需要管理员权限（任务在 \QuietHDD\ 下，ACL 仅管理员/SYSTEM 可读写），请求 UAC...' 'Cyan'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-pause.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated'
    if ($Pause)  { $al += ' -Pause' }
    if ($Resume) { $al += ' -Resume' }
    if ($Status) { $al += ' -Status' }
    if ($ReArm)  { $al += ' -ReArm' }
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        P '已在管理员窗口执行，请看那个窗口。' 'Green'
    } catch {
        P ('提权失败: ' + $_.Exception.Message) 'Yellow'
        P '请手动以管理员身份打开 PowerShell，运行本脚本。' 'Yellow'
    }
    return
}

# -------- 状态 --------
function Show-Status {
    $t = $null
    try { $t = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop } catch {}
    if ($t) { P ('  DiskGate 任务 State = ' + [string]$t.State) 'Green' }
    else { P '  DiskGate 任务未找到（可能未安装或当前会话无权读取）' 'Red' }
    foreach ($n in $DiskNumbers) {
        $d = Get-Disk -Number $n -ErrorAction SilentlyContinue
        if ($d) { P ('  Disk ' + $n + ' (' + ($d.FriendlyName.Trim()) + ') IsOffline = ' + $d.IsOffline) $(if ($d.IsOffline) { 'Yellow' } else { 'Green' }) }
    }
}

# 按当前时钟立刻跑一次闸门（深夜=NIGHT 脱机 / 白天=DAY 联机），不改任务状态
function Sync-Now {
    $gate = 'C:\ProgramData\QuietHDD\diskgate.ps1'
    if (-not (Test-Path -LiteralPath $gate)) { P ('  闸门脚本缺失: ' + $gate) 'Red'; return }
    P '  按当前时钟跑一次闸门（同步盘状态）...' 'Cyan'
    try { & $gate; P '  闸门已执行（有进程占用会 SKIP_BUSY 跳过，属保护）' 'Green' }
    catch { P ('  闸门执行失败: ' + $_.Exception.Message) 'Red' }
    foreach ($n in $DiskNumbers) {
        $d = Get-Disk -Number $n -ErrorAction SilentlyContinue
        if ($d) { P ('  Disk ' + $n + ' 现在 IsOffline = ' + $d.IsOffline) $(if ($d.IsOffline) { 'Yellow' } else { 'Green' }) }
    }
    P '  以 C:\ProgramData\QuietHDD\diskgate.log 最后一行为准。' 'Gray'
}

if ($Status) {
    P '=== QuietHDD DiskGate 状态 ===' 'Cyan'
    Show-Status
    return
}

if ($Pause) {
    P '=== 暂停今晚脱机（你要用盘） ===' 'Cyan'
    try {
        Disable-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop | Out-Null
        P '  DiskGate 任务已禁用：今晚 23:58 不会脱机' 'Green'
    } catch {
        P ('  禁用任务失败: ' + $_.Exception.Message) 'Red'
    }
    foreach ($n in $DiskNumbers) {
        try {
            $d = Get-Disk -Number $n -ErrorAction Stop
            if ($d.IsOffline) {
                Set-Disk -Number $n -IsOffline $false -ErrorAction Stop
                P ('  Disk ' + $n + ' 已联机（你可以用盘了）') 'Green'
            } else {
                P ('  Disk ' + $n + ' 本来就是联机') 'Gray'
            }
        } catch {
            P ('  Disk ' + $n + ' 联机失败: ' + $_.Exception.Message) 'Red'
        }
    }
    P '  用完跑 -Resume 即可恢复日常静音。' 'Cyan'
    return
}

if ($Resume) {
    P '=== 恢复日常静音（重新启用脱机闸门） ===' 'Cyan'
    try {
        Enable-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop | Out-Null
        P '  DiskGate 任务已启用：下一个 23:58 会重新脱机，10:02 联机' 'Green'
    } catch {
        P ('  启用任务失败: ' + $_.Exception.Message) 'Red'
    }
    if ($ReArm) {
        P '  ReArm：立刻按当前时钟同步盘状态' 'Cyan'
        Sync-Now
    } else {
        $hh = (Get-Date).Hour
        if ($hh -ge 23 -or $hh -lt 10) {
            P '  当前仍在静音窗口(23:00-10:00)：盘从现在起保持联机，本段凌晨不会再自动脱机。' 'Gray'
            P '  原因：23:58 那次 NIGHT 触发今晚已跑过，10:02 是 DAY 触发(联机,无害)，' 'Gray'
            P '        Enable 只恢复调度、不立即运行，所以要等下一天 23:58 才再脱机。' 'Gray'
            P '  想今晚剩下的夜继续静音，改用：' 'Yellow'
            P '    ... -Resume -ReArm' 'Yellow'
        } else {
            P '  当前是白天时段：盘保持联机正常；今晚 23:58 会照常自动脱机。' 'Green'
        }
    }
    return
}

if ($ReArm) {
    P '=== 只同步盘状态（不改任务） ===' 'Cyan'
    Sync-Now
    return
}

P '未指定动作。用法：' 'Yellow'
P '  -Pause            今晚用盘，停用脱机' 'Gray'
P '  -Resume           恢复日常静音（今晚不再脱机）' 'Gray'
P '  -Resume -ReArm    恢复并立刻重新脱机（今晚继续静音）' 'Gray'
P '  -ReArm            只按当前时钟同步盘状态' 'Gray'
P '  -Status           只看状态' 'Gray'
