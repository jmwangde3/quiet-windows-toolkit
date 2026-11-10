<#
.SYNOPSIS
  机械硬盘夜间"设备级"静音：23:58-10:02 把 HDD 设为脱机(Offline)，任何程序都读写不到。

.DESCRIPTION
  与已有的 \QuietHDD\QuietHDD_NightGate（只关计划任务）互为双保险：
    · NightGate 关掉会扫盘的任务        —— 软件层拦截
    · DiskGate  把整块机械盘标记脱机    —— 设备层兜底，连非计划任务的访问也挡住

  23:58 脱机 / 10:02 联机 / 开机 2 分钟后按当前时钟自检。
  SYSTEM 身份运行。日志：C:\ProgramData\QuietHDD\diskgate.log

  安全锁（自动跳过，不会误伤）：
    · 系统盘 / 启动盘不脱机
    · 含页面文件的盘不脱机
    · 盘上有正在运行的进程时不脱机（日志记 SKIP_BUSY），加 -Force 才强离

  部署前确认磁盘拓扑，把 diskgate.conf 的 Disks= 填成要脱机的磁盘号：
    · 系统盘 / 启动盘 / 含页面文件的盘自动跳过，不会被脱机
    · 其余机械盘按 Disks= 列表整盘脱机
  若有程序本体位于待脱机盘上，SKIP_BUSY 会跳过该盘并记日志；把这些程序迁到
  SSD 后重跑，或加 -Force 强制脱机。缓存目录、开机自启工具若仍在待脱机盘上，
  夜间调用会失败，建议一并迁到系统盘。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1"

.EXAMPLE
  # 连同空闲停转一起设成 5 分钟（当前 AC 值是 0 = 从不停转）
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -SpinDownSeconds 300

.EXAMPLE
  # 当场验证离线/联机链路（会短暂脱机目标盘约 2 秒）
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -TestNight

.EXAMPLE
  # 在 0:00-10:00 静音窗口内安装，但不想装完立刻掉盘
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -NoImmediate

.EXAMPLE
  # 只看状态
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -Status

.EXAMPLE
  # 一键回滚：删任务、把所有盘联机、恢复电源设置
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-offline.ps1" -Restore
#>
[CmdletBinding()]
param(
    [switch]$Restore,
    [switch]$Status,
    [switch]$TestNight,
    [switch]$NoImmediate,
    [switch]$Force,
    [int[]]$DiskNumbers = @(0,1),
    [int]$SpinDownSeconds = 0,
    [switch]$NoElevate,
    [switch]$FromElevated
)

$ErrorActionPreference = 'Continue'

function Write-Step($m) { Write-Host ("[*] " + $m) -ForegroundColor Cyan }
function Write-OK($m)   { Write-Host ("[OK] " + $m) -ForegroundColor Green }
function Write-Warn2($m){ Write-Host ("[!!] " + $m) -ForegroundColor Yellow }
function Write-Err2($m) { Write-Host ("[XX] " + $m) -ForegroundColor Red }

# ---------------- 提权 ----------------
$OriginalPath = $PSCommandPath
$originFile = Join-Path $env:TEMP 'quiet-hdd-offline.origin'
if ($FromElevated -and (Test-Path -LiteralPath $originFile)) {
    $OriginalPath = [System.IO.File]::ReadAllText($originFile, [System.Text.Encoding]::UTF8).Trim()
}
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated -and -not $Status) {
    Write-Step '需要管理员权限，正在请求 UAC 提权...'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-offline.ps1'
    [System.IO.File]::WriteAllText($originFile, $PSCommandPath, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    # 数组参数经 -File 命令行传递会被错误绑定（实测 '0,1' -> @(1)），改走参数文件
    $argsFile = Join-Path $env:TEMP 'quiet-hdd-offline.args'
    $payload = @(
        ('Restore='     + $(if ($Restore)     { '1' } else { '0' })),
        ('TestNight='   + $(if ($TestNight)   { '1' } else { '0' })),
        ('NoImmediate=' + $(if ($NoImmediate) { '1' } else { '0' })),
        ('Force='       + $(if ($Force)       { '1' } else { '0' })),
        ('Disks='       + ($DiskNumbers -join ',')),
        ('Spin='        + $SpinDownSeconds)
    )
    [System.IO.File]::WriteAllLines($argsFile, $payload, (New-Object System.Text.UTF8Encoding($false)))
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated'
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        Write-OK '已在管理员窗口执行，请看那个窗口。'
    } catch {
        Write-Warn2 ('提权失败: ' + $_.Exception.Message)
        Write-Warn2 '请手动以管理员身份打开 PowerShell，运行本脚本并加 -NoElevate'
    }
    return
}

# 提权后从参数文件恢复开关与磁盘号（避免 -File 数组绑定错误）
if ($FromElevated) {
    $argsFile = Join-Path $env:TEMP 'quiet-hdd-offline.args'
    if (Test-Path -LiteralPath $argsFile) {
        foreach ($l in (Get-Content -LiteralPath $argsFile -Encoding UTF8)) {
            if     ($l -match '^Restore=(\d)$')     { $Restore         = ($Matches[1] -eq '1') }
            elseif ($l -match '^TestNight=(\d)$')   { $TestNight       = ($Matches[1] -eq '1') }
            elseif ($l -match '^NoImmediate=(\d)$') { $NoImmediate     = ($Matches[1] -eq '1') }
            elseif ($l -match '^Force=(\d)$')       { $Force           = ($Matches[1] -eq '1') }
            elseif ($l -match '^Disks=(.+)$')       { $DiskNumbers     = @($Matches[1].Split(',') | ForEach-Object { [int]$_.Trim() }) }
            elseif ($l -match '^Spin=(\d+)$')       { $SpinDownSeconds = [int]$Matches[1] }
        }
        Write-OK ('已恢复提权参数: Disks=' + ($DiskNumbers -join ',') + '  Spin=' + $SpinDownSeconds + '  Restore=' + $Restore)
    } else {
        Write-Warn2 '参数文件缺失，使用默认值 Disks=0,1'
    }
}

# ---------------- 路径与常量 ----------------
$GateDir        = 'C:\ProgramData\QuietHDD'
$DiskGateScript = Join-Path $GateDir 'diskgate.ps1'
$ConfPath       = Join-Path $GateDir 'diskgate.conf'
$LogPath        = Join-Path $GateDir 'diskgate.log'
$SpinOrigFile   = Join-Path $GateDir 'spindown.orig'
$TaskName       = 'QuietHDD_DiskGate'
$TaskFolders    = @('\QuietHDD\', '\')

if (-not (Test-Path -LiteralPath $GateDir)) { New-Item -ItemType Directory -Path $GateDir -Force | Out-Null }

function Get-TaskAnywhere {
    foreach ($f in $TaskFolders) {
        $t = Get-ScheduledTask -TaskPath $f -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($t) { return $t }
    }
    return $null
}

function Get-SpinDownAC {
    $q = (& powercfg /q SCHEME_CURRENT SUB_DISK DISKIDLE 2>$null) -join "`n"
    $m = [regex]::Matches($q, '0x([0-9A-Fa-f]{8})')
    if ($m.Count -ge 1) { return $m[0].Groups[1].Value }
    return $null
}

# ---------------- 状态查看（只读，不需管理员） ----------------
if ($Status) {
    $h = (Get-Date).Hour
    Write-Step ('=== 状态板  ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $(if ($h -ge 23 -or $h -lt 10) {'静音窗口 0:00-10:00'} else {'白天时段'}) + ' ===')
    foreach ($n in $DiskNumbers) {
        try {
            $d = Get-Disk -Number $n -ErrorAction Stop
            $ls = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter + ':' }) -join ','
            $flag = if ($d.IsOffline) { 'OFFLINE <== 静音' } else { 'Online' }
            Write-Output ('  Disk ' + $n + '  ' + $d.FriendlyName.Trim().PadRight(28) + ' ' + $flag.PadRight(18) + ' 卷=' + $ls)
        } catch {
            Write-Output ('  Disk ' + $n + '  读取失败: ' + $_.Exception.Message)
        }
    }
    Write-Output ('  DISKIDLE(AC) = 0x' + (Get-SpinDownAC) + '  (0 = 从不停转)')
    $t = Get-TaskAnywhere
    if ($t) {
        $i = $t | Get-ScheduledTaskInfo
        Write-Output ('  DiskGate 任务: ' + $t.TaskPath + $t.TaskName + '  State=' + [string]$t.State + '  NextRun=' + $i.NextRunTime)
        foreach ($tr in $t.Triggers) { Write-Output ('    ' + $tr.CimClass.CimClassName + '  Start=' + $tr.StartBoundary + '  Delay=' + $tr.Delay) }
    } else {
        Write-Output '  DiskGate 任务: 未安装（或当前会话无权查看，请以管理员运行 -Status）'
    }
    if (Test-Path -LiteralPath $LogPath) {
        Write-Output '  --- diskgate.log 尾部 ---'
        Get-Content -LiteralPath $LogPath -Encoding UTF8 -Tail 8 | ForEach-Object { Write-Output ('    ' + $_) }
    } else { Write-Output '  diskgate.log: 尚无记录' }
    Write-Output '  提示：现有 QuietHDD_NightGate（关任务）与本方案并存，互不覆盖。'
    return
}

# ---------------- 回滚 ----------------
if ($Restore) {
    Write-Step '=== 回滚 DiskGate ==='
    foreach ($f in $TaskFolders) {
        try {
            if (Get-ScheduledTask -TaskPath $f -TaskName $TaskName -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskPath $f -TaskName $TaskName -Confirm:$false -ErrorAction Stop
                Write-OK ('已删除任务 ' + $f + $TaskName)
            }
        } catch { Write-Warn2 ('删除任务失败 ' + $f + ' : ' + $_.Exception.Message) }
    }
    foreach ($n in $DiskNumbers) {
        try {
            $d = Get-Disk -Number $n -ErrorAction Stop
            if ($d.IsOffline) {
                Set-Disk -Number $n -IsOffline $false -ErrorAction Stop
                Write-OK ('Disk ' + $n + ' 已联机')
            } else { Write-OK ('Disk ' + $n + ' 本来就是 Online') }
        } catch { Write-Warn2 ('Disk ' + $n + ' 联机失败: ' + $_.Exception.Message) }
    }
    if (Test-Path -LiteralPath $SpinOrigFile) {
        $orig = (Get-Content -LiteralPath $SpinOrigFile -Encoding UTF8 -Raw).Trim()
        if ($orig -match '^[0-9A-Fa-f]{8}$') {
            & powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE ('0x' + $orig) | Out-Null
            & powercfg /s SCHEME_CURRENT | Out-Null
            Write-OK ('DISKIDLE(AC) 已恢复为 0x' + $orig)
        }
        Remove-Item -LiteralPath $SpinOrigFile -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $DiskGateScript -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $ConfPath       -Force -ErrorAction SilentlyContinue
    Write-OK '已删除 diskgate.ps1 / diskgate.conf（日志保留）'
    Write-Host ''
    Write-Host '  QuietHDD_NightGate（关任务那道闸门）保持不动，自动维护与 SysMain 也保持不动。' -ForegroundColor Gray
    return
}

# ---------------- 安装 ----------------
Write-Step '=== 前置检查：命令行里引用目标盘的进程（只提示，不阻止脱机） ==='
$refPat = '[DdEeJj]:\\'
$refs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match $refPat -or $_.ExecutablePath -match $refPat })
if ($refs.Count -gt 0) {
    foreach ($r in $refs) {
        $hit = [regex]::Match([string]$r.CommandLine, '[DdEeJj]:\\[^"\s]*').Value
        if (-not $hit) { $hit = [string]$r.ExecutablePath }
        Write-Warn2 ('进程 ' + $r.Name + ' (PID ' + $r.ProcessId + ') 引用 ' + $hit + ' -> 脱机后该路径访问会失败')
    }
    Write-Warn2 '这些进程的本体不在目标盘上，闸门不会跳过；如需避免报错请改其缓存路径或夜间退出。'
} else { Write-OK '没有进程在命令行中引用 J:/D:/E:' }

Write-Step '=== 1/6 目标盘安全体检 ==='
$safeList = @()
foreach ($n in $DiskNumbers) {
    try {
        $d = Get-Disk -Number $n -ErrorAction Stop
        $ls = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })
        $pf = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Substring(0,1).ToUpper() })
        $hasPF = @($ls | Where-Object { $pf -contains $_ }).Count -gt 0
        $reason = ''
        if ($d.IsBoot -or $d.IsSystem) { $reason = '系统/启动盘' }
        elseif ($hasPF) { $reason = '含页面文件' }
        if ($reason -ne '') {
            Write-Warn2 ('Disk ' + $n + ' 跳过：' + $reason + '（绝不脱机）')
        } else {
            $safeList += $n
            Write-OK ('Disk ' + $n + '  ' + $d.FriendlyName.Trim() + '  卷=' + (($ls | ForEach-Object { $_ + ':' }) -join ',') + '  -> 可脱机')
        }
    } catch {
        Write-Warn2 ('Disk ' + $n + ' 读取失败，跳过: ' + $_.Exception.Message)
    }
}
if ($safeList.Count -eq 0) { Write-Err2 '没有可安全脱机的目标盘，已中止。'; return }
$DiskNumbers = $safeList

Write-Step '=== 2/6 写配置 ==='
$confLines = @(
    ('Disks=' + ($DiskNumbers -join ',')),
    ('Force=' + $(if ($Force) { '1' } else { '0' }))
)
[System.IO.File]::WriteAllLines($ConfPath, $confLines, (New-Object System.Text.UTF8Encoding($false)))
Write-OK ('已写 ' + $ConfPath + '  -> ' + ($confLines -join ' ; '))
$confDisks = [string]((Get-Content -LiteralPath $ConfPath -Encoding UTF8 | Where-Object { $_ -match '^Disks=' } | Select-Object -First 1) -replace '^Disks=','')
$expectDisks = $DiskNumbers -join ','
if ($confDisks -ne $expectDisks) {
    Write-Err2 ('配置回读校验失败: conf=Disks=' + $confDisks + ' 期望=Disks=' + $expectDisks + '，已中止')
    return
}
Write-OK ('配置回读校验通过: Disks=' + $confDisks)
$volSummary = @()
foreach ($n in $DiskNumbers) {
    $vs = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter + ':' })
    $volSummary += ('Disk' + $n + '(' + ($vs -join '/') + ')')
}
Write-OK ('今晚将脱机: ' + ($volSummary -join '  '))

Write-Step '=== 3/6 生成闸门脚本 ==='
$GateBody = @'
param([switch]$ForceNight, [switch]$ForceDay)

$ErrorActionPreference = 'Continue'
$dir  = 'C:\ProgramData\QuietHDD'
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$log  = Join-Path $dir 'diskgate.log'
$conf = Join-Path $dir 'diskgate.conf'

$disks = @(0,1)
$force = $false
if (Test-Path -LiteralPath $conf) {
  foreach ($line in (Get-Content -LiteralPath $conf -Encoding UTF8)) {
    if ($line -match '^Disks=(.+)$') { $disks = @($Matches[1].Split(',') | ForEach-Object { [int]$_.Trim() }) }
    if ($line -match '^Force=(\d+)$') { $force = ($Matches[1] -eq '1') }
  }
}

$h = (Get-Date).Hour
# Startup re-run (AtStartup trigger has PT2M delay): when the box booted less than
# 15 minutes ago, treat 06:00-10:01 as DAY and bring the disks online. The user is
# awake and needs the drives. An unattended reboot at 00:00-05:59 still stays NIGHT.
$dayStartHour = 10
try {
  $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
  if ($os -and $os.LastBootUpTime) {
    $ageMin = ((Get-Date) - $os.LastBootUpTime).TotalMinutes
    if ($ageMin -ge 0 -and $ageMin -lt 15) { $dayStartHour = 6 }
  }
} catch { }
$night = if ($ForceNight) { $true } elseif ($ForceDay) { $false } else { ($h -ge 23 -or $h -lt $dayStartHour) }
$msg = @('mode=' + $(if ($night) { 'NIGHT' } else { 'DAY' }) + $(if ($dayStartHour -eq 6) { '(startup)' } else { '' }))

foreach ($n in $disks) {
  try { $d = Get-Disk -Number $n -ErrorAction Stop }
  catch { $msg += ('ERR:Disk' + $n + ':notfound'); continue }

  if (-not $night) {
    if ($d.IsOffline) {
      try {
        Set-Disk -Number $n -IsOffline $false -ErrorAction Stop
        $after = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter + ':' }) -join ','
        $msg += ('ONLINE:Disk' + $n + ':letters=' + $after)
      } catch { $msg += ('FAIL_ONLINE:Disk' + $n + ':' + $_.Exception.Message) }
    } else { $msg += ('OK_ONLINE:Disk' + $n) }
    continue
  }

  if ($d.IsBoot -or $d.IsSystem) { $msg += ('SKIP_SYSTEM:Disk' + $n); continue }

  $pf = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Substring(0,1).ToUpper() })
  $letters = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })
  if (@($letters | Where-Object { $pf -contains $_ }).Count -gt 0) { $msg += ('SKIP_PAGEFILE:Disk' + $n); continue }

  if ($d.IsOffline) { $msg += ('ALREADY_OFF:Disk' + $n); continue }

  $busy = @()
  if ($letters.Count -gt 0) {
    $esc = ($letters | ForEach-Object { [regex]::Escape($_) }) -join ''
    $cls = '^"?' + '[' + $esc + ']:\\'
    $busy = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -match $cls -or $_.CommandLine -match $cls } | Select-Object -ExpandProperty ProcessId -Unique)
  }
  if ($busy.Count -gt 0 -and -not $force) {
    $msg += ('SKIP_BUSY:Disk' + $n + ':pids=' + ($busy -join '+'))
    continue
  }

  try {
    Set-Disk -Number $n -IsOffline $true -ErrorAction Stop
    $msg += ('OFFLINE:Disk' + $n)
  } catch { $msg += ('FAIL_OFFLINE:Disk' + $n + ':' + $_.Exception.Message) }
}

$line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + ($msg -join ' ')
Add-Content -LiteralPath $log -Value $line -Encoding UTF8
'@
[System.IO.File]::WriteAllText($DiskGateScript, $GateBody, (New-Object System.Text.UTF8Encoding($false)))
Write-OK ('已生成 ' + $DiskGateScript + ' (' + $GateBody.Length + ' 字符)')

Write-Step '=== 4/6 注册计划任务 ==='
$act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $DiskGateScript + '"')
$t1 = New-ScheduledTaskTrigger -Daily -At '23:58'
$t2 = New-ScheduledTaskTrigger -Daily -At '10:02'
$t3 = New-ScheduledTaskTrigger -AtStartup
try { $t3.Delay = 'PT2M' } catch { }
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
$prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

$registered = $false
foreach ($f in $TaskFolders) {
    try {
        Register-ScheduledTask -TaskName $TaskName -TaskPath $f -Action $act -Trigger @($t1,$t2,$t3) -Settings $set -Principal $prn -Force -ErrorAction Stop | Out-Null
        Write-OK ('注册成功: ' + $f + $TaskName)
        $registered = $true
        break
    } catch {
        Write-Warn2 ('注册到 ' + $f + ' 失败: ' + $_.Exception.Message)
    }
}
if (-not $registered) {
    Write-Err2 '两种路径都注册失败，已中止。请以管理员重新运行；或先跑 quiet-hdd-fix.ps1 排查任务注册环境。'
    return
}
$t = Get-TaskAnywhere
if ($t) {
    $i = $t | Get-ScheduledTaskInfo
    Write-OK ('校验: State=' + [string]$t.State + '  NextRun=' + $i.NextRunTime + '  Principal=' + $t.Principal.UserId + '/' + $t.Principal.RunLevel)
    foreach ($tr in $t.Triggers) { Write-OK ('  Trigger: ' + $tr.CimClass.CimClassName + ' Start=' + $tr.StartBoundary + ' Delay=' + $tr.Delay) }
} else {
    Write-Err2 '注册返回成功但查不到任务（ACL 可见性问题），请在管理员窗口复核。'
}

Write-Step '=== 5/6 可选：空闲停转 ==='
if ($SpinDownSeconds -gt 0) {
    $orig = Get-SpinDownAC
    if ($orig) {
        if (Test-Path -LiteralPath $SpinOrigFile) {
            Write-OK ('保留已有原值 0x' + (Get-Content -LiteralPath $SpinOrigFile -Encoding UTF8 -Raw).Trim() + '（不覆盖，保证 -Restore 能回到最初值）')
        } else {
            [System.IO.File]::WriteAllText($SpinOrigFile, $orig, (New-Object System.Text.UTF8Encoding($false)))
            Write-OK ('已保存原值 0x' + $orig)
        }
        & powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $SpinDownSeconds | Out-Null
        & powercfg /s SCHEME_CURRENT | Out-Null
        Write-OK ('DISKIDLE(AC) -> ' + $SpinDownSeconds + ' 秒')
    } else { Write-Warn2 '读取 DISKIDLE 原值失败，未改动电源设置' }
} else {
    Write-OK '未改动电源设置（要顺带设置空闲停转请加 -SpinDownSeconds 300）'
}

Write-Step '=== 6/6 立即执行一次并校验 ==='
$hPre = (Get-Date).Hour
if ($NoImmediate) {
    Write-OK '已跳过立即执行（-NoImmediate）；今晚 23:58 首次自动触发'
} else {
    if ($hPre -ge 23 -or $hPre -lt 10) {
        Write-Warn2 '当前处于 0:00-10:00 静音窗口：立即触发会马上把目标盘脱机，10:02 自动联机。'
        Write-Warn2 '不想现在就掉盘请加 -NoImmediate，或等 10:05 之后再运行本脚本。'
    }
    try {
        $cur = Get-TaskAnywhere
        if ($cur) { Start-ScheduledTask -TaskPath $cur.TaskPath -TaskName $cur.TaskName -ErrorAction Stop; Write-OK '已手动触发一次任务（按当前时钟判定）' }
    } catch { Write-Warn2 ('触发失败: ' + $_.Exception.Message) }
}

if ($TestNight) {
    Write-Warn2 'TestNight：强制夜间跑一次后强制白天恢复（J: 短暂脱机约 2 秒）'
    try {
        & $DiskGateScript -ForceNight
        Start-Sleep -Seconds 2
    } finally {
        & $DiskGateScript -ForceDay
    }
    Write-OK 'TestNight 完成，看下面日志应有 OFFLINE 与 ONLINE 各一行'
}

Start-Sleep -Seconds 3
Write-Host ''
Write-Host '--- disk gate.log 尾部 ---' -ForegroundColor Cyan
if (Test-Path -LiteralPath $LogPath) { Get-Content -LiteralPath $LogPath -Encoding UTF8 -Tail 6 | ForEach-Object { Write-Host ('  ' + $_) } }
else { Write-Warn2 '尚无日志，任务可能仍在启动' }

Write-Host ''
$hNow = (Get-Date).Hour
if ($hNow -ge 23 -or $hNow -lt 10) {
    Write-Host '  当前在 0:00-10:00 静音窗口：目标盘应为 OFFLINE（有进程占用的盘标 SKIP_BUSY，属预期保护），10:02 自动联机。' -ForegroundColor Yellow
} else {
    Write-Host '  当前是白天时段：目标盘 Online，自动维护照常；今晚 23:58 会自动脱机。' -ForegroundColor Green
}
Write-Host ('  回滚：powershell -ExecutionPolicy Bypass -File "' + $OriginalPath + '" -Restore') -ForegroundColor Gray
Write-Host ('  状态：powershell -ExecutionPolicy Bypass -File "' + $OriginalPath + '" -Status') -ForegroundColor Gray
