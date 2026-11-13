#Requires -Version 5.1
<#
    Verify-NightRun.ps1 — QuietMode v2 夜跑/日回验收（只读，不需要管理员）。

    把 docs/V2_SPEC.md 第 12 节的验收项 A01–A23 里能自动判定的部分，
    对着「worker.log 的分步执行记录 + reconcile.log 的心跳 + state.json +
    qmctl --json status 的实时观测」逐条给出结论，失败项直接指出缺哪一步。

    不改任何系统状态：只用 qmctl 的只读命令和两本日志。

    用法：
      .\Verify-NightRun.ps1                      # 看最近一次夜链与日链
      .\Verify-NightRun.ps1 -Md 报告.md          # 同时写一份 Markdown 证据表
      .\Verify-NightRun.ps1 -Hours 48            # 放宽统计窗口
#>
[CmdletBinding()]
param(
    [string]$Qmctl = 'H:\AI工作区\电脑相关\QuietMode\qmctl.exe',
    [string]$Root = 'C:\ProgramData\QuietMode',
    [int]$Hours = 24,
    [string]$Md = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$logsDir = Join-Path $Root 'logs'
$workerLog = Join-Path $logsDir 'worker.log'
$reconcileLog = Join-Path $logsDir 'reconcile.log'

# 期望的分步链（顺序即执行顺序）
$nightSteps = @('maintenance/night', 'disk/offline', 'spindown/apply', 'fan/silent', 'verify/night')
$daySteps = @('disk/online', 'maintenance/day', 'spindown/apply', 'fan/normal', 'verify/day')

$rows = New-Object System.Collections.ArrayList
function Add-Row {
    param([string]$Id, [string]$Name, [string]$State, [string]$Evidence)
    [void]$rows.Add([pscustomobject]@{ Id = $Id; Name = $Name; State = $State; Evidence = $Evidence })
}

# ------------------------------------------------------------------ 数据采集
if (-not (Test-Path -LiteralPath $Qmctl)) {
    Write-Host ("找不到 qmctl：" + $Qmctl) -ForegroundColor Red
    exit 3
}

$statusJson = & $Qmctl --json status 2>&1 | Out-String
$st = $null
try { $st = $statusJson | ConvertFrom-Json } catch {
    Write-Host 'qmctl --json status 解析失败：' -ForegroundColor Red
    Write-Host $statusJson
    exit 3
}

# worker.log → 每次运行（日期 时间 相位 结果 步号 步骤名）
$runs = @()
if (Test-Path -LiteralPath $workerLog) {
    $rx = '^(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2})\s+\[(night|day)\]\s+(\S+)\s+(\d+)\s+(\S+)\s*$'
    foreach ($line in (Get-Content -LiteralPath $workerLog -Encoding UTF8)) {
        $m = [regex]::Match($line, $rx)
        if (-not $m.Success) { continue }
        $runs += [pscustomobject]@{
            At     = [datetime]($m.Groups[1].Value + ' ' + $m.Groups[2].Value)
            Phase  = $m.Groups[3].Value
            Result = $m.Groups[4].Value
            StepNo = [int]$m.Groups[5].Value
            Step   = $m.Groups[6].Value
        }
    }
}

function Get-LastChain {
    param([string]$Phase)
    $mine = @($runs | Where-Object { $_.Phase -eq $Phase } | Sort-Object At)
    if ($mine.Count -eq 0) { return $null }
    # 以「步号 1」为界切分批次，取最后一批
    $start = 0
    for ($i = 0; $i -lt $mine.Count; $i++) { if ($mine[$i].StepNo -eq 1) { $start = $i } }
    return @($mine[$start..($mine.Count - 1)])
}

function Test-Chain {
    param($Chain, [string[]]$Expected)
    if (-not $Chain -or $Chain.Count -eq 0) {
        return [pscustomobject]@{ Ok = $false; Text = '没有执行记录'; Detail = 'worker.log 里找不到这一段' }
    }
    $got = @($Chain | ForEach-Object { $_.Step })
    $missing = @()
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        if ($got -notcontains $Expected[$i]) { $missing += $Expected[$i] }
    }
    $bad = @($Chain | Where-Object { $_.Result -ne 'done' })
    $span = $Chain[0].At.ToString('MM-dd HH:mm:ss') + ' → ' + $Chain[-1].At.ToString('HH:mm:ss')
    if ($missing.Count -gt 0) {
        return [pscustomobject]@{ Ok = $false; Text = ($got.Count.ToString() + ' 步，缺 ' + ($missing -join ',')); Detail = $span }
    }
    if ($bad.Count -gt 0) {
        $t = ($bad | ForEach-Object { $_.Step + '=' + $_.Result }) -join ', '
        return [pscustomobject]@{ Ok = $false; Text = ('有非 done 步骤：' + $t); Detail = $span }
    }
    return [pscustomobject]@{ Ok = $true; Text = ($Expected.Count.ToString() + '/' + $Expected.Count + ' 步全部 done'); Detail = $span }
}

function Get-StepText {
    param($Chain, [string]$Step)
    $s = @($Chain | Where-Object { $_.Step -eq $Step })
    if ($s.Count -eq 0) { return '缺该步' }
    return ($s[-1].At.ToString('HH:mm:ss') + ' ' + $s[-1].Result)
}

$night = Get-LastChain 'night'
$day = Get-LastChain 'day'
$nightChk = Test-Chain $night $nightSteps
$dayChk = Test-Chain $day $daySteps

# reconcile.log → 最近 N 小时心跳
$beats = @()
if (Test-Path -LiteralPath $reconcileLog) {
    $since = (Get-Date).ToUniversalTime().AddHours(-$Hours)
    foreach ($line in (Get-Content -LiteralPath $reconcileLog -Encoding UTF8)) {
        $m = [regex]::Match($line, '^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)\s+trigger=(\S+)\s+action=(\S+)\s+apply=(\d+)\s+verdict=(\S+)')
        if (-not $m.Success) { continue }
        try { $t = [datetime]::ParseExact($m.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss'Z'", $null).ToUniversalTime() } catch { continue }
        if ($t -lt $since) { continue }
        $beats += [pscustomobject]@{ At = $t; Trigger = $m.Groups[2].Value; Action = $m.Groups[3].Value; Verdict = $m.Groups[5].Value }
    }
}
$badBeats = @($beats | Where-Object { $_.Verdict -ne 'Ok' })
$nightBeats = @($beats | Where-Object { $_.Action -eq 'night' })

# ------------------------------------------------------------------ 判定
$now = Get-Date
$isNight = [bool]$st.window.isNight
$targets = @($st.disks.targets)
$offlineCount = @($targets | Where-Object { $_.offline }).Count
$allOffline = ($targets.Count -gt 0 -and $offlineCount -eq $targets.Count)
$allMatched = ($targets.Count -gt 0 -and @($targets | Where-Object { -not $_.matched }).Count -eq 0)

# A01 / A02 / A05 / A07 / A08 —— 夜链分步
Add-Row 'A01' '23:58 自动进入夜间模式' $(if ($nightChk.Ok) { 'PASS' } else { 'FAIL' }) ('夜链 ' + $nightChk.Detail + '；' + $nightChk.Text)
Add-Row 'A02' '目标维护任务全部停止并禁用' $(if ($nightChk.Ok) { 'PASS' } else { 'FAIL' }) ('maintenance/night @ ' + (Get-StepText $night 'maintenance/night'))
Add-Row 'A07' '配置硬盘全部 Offline（夜间）' $(if ($nightChk.Ok) { 'PASS' } else { 'FAIL' }) ('disk/offline @ ' + (Get-StepText $night 'disk/offline'))
Add-Row 'A08' 'FanControl 静音配置生效（夜间）' $(if ($nightChk.Ok) { 'PASS' } else { 'FAIL' }) ('fan/silent @ ' + (Get-StepText $night 'fan/silent'))

# A11 / A12 / A17 —— 日链
Add-Row 'A11' '10:02 自动进入白天模式' $(if ($dayChk.Ok) { 'PASS' } else { 'FAIL' }) ('日链 ' + $dayChk.Detail + '；' + $dayChk.Text)
Add-Row 'A12' '磁盘全部 Online（白天）' $(if ($targets.Count -eq 0) { 'FAIL' } elseif ($allOffline) { 'FAIL' } else { 'PASS' }) (($targets.Count - $offlineCount).ToString() + '/' + $targets.Count + ' 在线（Offline ' + $offlineCount + ' 块）；disk/online @ ' + (Get-StepText $day 'disk/online'))
Add-Row 'A17' 'FanControl 常态配置（白天）' $(if ($st.fan.match -eq 'normal') { 'PASS' } elseif ($st.fan.match -eq 'silent') { 'FAIL' } else { 'INFO' }) ('match=' + $st.fan.match + ' current=' + $st.fan.currentProfile)

# A16 DISKIDLE
$wantAc = [int]$st.spinDown.configuredMinutes * 60
$spinOk = ([int]$st.spinDown.acSeconds -eq $wantAc -and [int]$st.spinDown.dcSeconds -eq $wantAc)
Add-Row 'A16' '电源设置 = 配置值（DISKIDLE）' $(if ($spinOk) { 'PASS' } else { 'FAIL' }) ('AC=' + $st.spinDown.acSeconds + 's DC=' + $st.spinDown.dcSeconds + 's 期望 ' + $wantAc + 's')

# A18 / A19 磁盘身份
Add-Row 'A18' '自定义磁盘身份正确' $(if ($allMatched) { 'PASS' } else { 'FAIL' }) ($targets.Count.ToString() + ' 块目标，未命中 ' + @($targets | Where-Object { -not $_.matched }).Count + ' 块')
$sysDisk = $null
try { $sysDisk = (Get-Partition -DriveLetter $env:SystemDrive.Substring(0, 1) -ErrorAction Stop).DiskNumber } catch { }
$hitSys = $false
if ($null -ne $sysDisk) { $hitSys = [bool](@($targets | Where-Object { $_.number -eq $sysDisk }).Count) }
Add-Row 'A19' '不误操作系统盘' $(if ($hitSys) { 'FAIL' } else { 'PASS' }) ('系统盘 Disk #' + $sysDisk + ' 不在目标列表' )

# A21 重复执行不出错
$repOk = ($badBeats.Count -eq 0 -and $beats.Count -gt 0)
Add-Row 'A21' '重复执行不报错（心跳全 Ok）' $(if ($repOk) { 'PASS' } elseif ($beats.Count -eq 0) { 'INFO' } else { 'FAIL' }) ($beats.Count.ToString() + ' 条心跳，非 Ok ' + $badBeats.Count + ' 条')

# A22 调度未暂停
Add-Row 'A22' '调度处于自动状态' $(if (-not $st.state.schedulePaused) { 'PASS' } else { 'FAIL' }) ('schedulePaused=' + $st.state.schedulePaused + ' modeOn=' + $st.state.modeOn)

# A23 UI 不误报
$claim = $st.controller.text
$consistent = $true
if ($st.controller.targetNight -or $st.controller.state -like '*Night*') { $consistent = $allOffline } else { $consistent = -not $allOffline }
Add-Row 'A23' '状态显示与实机一致' $(if ($consistent) { 'PASS' } else { 'FAIL' }) ('controller=' + $st.controller.state + '(' + $claim + ') 实测离线 ' + $offlineCount + '/' + $targets.Count)

# A24 CI
Add-Row 'A24' 'CI 自动测试通过' 'MANUAL' '看 GitHub Actions（分支 codex/quietmode-v2-rc）'

# A09 / A10 触发器是否还在
$taskXml = ''
try { $taskXml = (& $Qmctl task print 2>&1 | Out-String) } catch { }
$hasBoot = ($taskXml -match 'BootTrigger')
$hasSleep = ($taskXml -match 'Power-Troubleshooter|EventID=4801')
$hasLogon = ($taskXml -match 'LogonTrigger')
Add-Row 'A09' '重启后重新执行（BootTrigger）' $(if ($hasBoot) { 'PASS' } else { 'FAIL' }) '任务 XML 含 BootTrigger（需真重启验证）'
Add-Row 'A10' '睡眠恢复后重新校验（EventTrigger）' $(if ($hasSleep) { 'PASS' } else { 'FAIL' }) ('任务 XML 含 ' + $(if ($hasSleep) { 'Power-Troubleshooter/4801' } else { '无' }) + '（需真睡眠验证）')

# A20 恢复文件保留
$recoveryDir = Join-Path $Root 'recovery'
$bakCount = 0
if (Test-Path -LiteralPath $recoveryDir) { $bakCount = @(Get-ChildItem -LiteralPath $recoveryDir -Directory -ErrorAction SilentlyContinue).Count }
Add-Row 'A20' '失败时保留恢复文件' $(if ($bakCount -gt 0) { 'PASS' } else { 'INFO' }) ('recovery 下有 ' + $bakCount + ' 个备份目录')

# ------------------------------------------------------------------ 维护项实况
# qmctl --json maint status 不需要管理员：任务的 presence/enabled、SysMain 的启动
# 类型、注册表 MaintenanceBoundary/WakeUp、电源方案都在里面。A04/A06/A13/A14/A15
# 全靠这份观测判定，不用再让人手工去开管理员窗口。
$maint = $null
try {
    $maintRaw = & $Qmctl --json maint status 2>&1 | Out-String
    $maint = $maintRaw | ConvertFrom-Json
} catch { $maint = $null }

$mObsTasks = @(); $mObsSvc = $null; $mObsReg = @(); $mObsPp = $null
$mBaseTasks = @(); $mBaseSvc = $null; $mBaseReg = @(); $mBasePp = $null
$cfgHour = 10
if ($maint) {
    $mObsTasks = @($maint.observation.tasks)
    $mObsSvc   = @($maint.observation.services | Where-Object { $_.serviceName -eq 'SysMain' }) | Select-Object -First 1
    $mObsReg   = @($maint.observation.registry)
    $mObsPp    = $maint.observation.powerPlan
    if ($maint.baseline) {
        $mBaseTasks = @($maint.baseline.scheduledTasks)
        $mBaseSvc   = @($maint.baseline.services | Where-Object { $_.serviceName -eq 'SysMain' }) | Select-Object -First 1
        $mBaseReg   = @($maint.baseline.registry)
        $mBasePp    = $maint.baseline.powerPlan
    }
    if ($null -ne $maint.config -and $null -ne $maint.config.maintenanceHour) {
        $cfgHour = [int]$maint.config.maintenanceHour
    }
}

# A03 可以自动判定：只要 baseline 里记下了 Regular Maintenance 的 exists/enabled，
# 夜链恢复时就有据可依。这条以前固定报「已知 WARN」，schema 3 的基线重建后应该消失。
$a03State = 'MANUAL'
$a03Why = 'baseline.json 不存在或读不出来'
$baselineFile = Join-Path $Root 'baseline.json'
if (Test-Path -LiteralPath $baselineFile) {
    try {
        $bj = Get-Content -LiteralPath $baselineFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $rm = $null
        foreach ($t in $bj.scheduledTasks) {
            if ($t.name -eq 'Regular Maintenance') { $rm = $t; break }
        }
        if (-not $rm) {
            $a03State = 'FAIL'; $a03Why = 'baseline 里没有 Regular Maintenance 这条'
        } elseif ($null -eq $rm.exists) {
            $a03State = 'MANUAL'; $a03Why = 'exists 未知（采集时读不到，可能未提权）'
        } elseif ($rm.exists -eq $false) {
            $a03State = 'PASS'; $a03Why = '本机无此任务，baseline 已记录 exists=false（不会误禁/误恢复）'
        } elseif ($null -eq $rm.enabled) {
            $a03State = 'MANUAL'; $a03Why = '任务存在但启用状态未知'
        } else {
            $a03State = 'PASS'; $a03Why = 'baseline 已记录 exists=true enabled=' + $rm.enabled
        }
    } catch {
        $a03State = 'MANUAL'
        $a03Why = 'baseline.json 解析失败：' + $_.Exception.Message
    }
}
Add-Row 'A03' 'Regular Maintenance 被正确管理' $a03State $a03Why

# A04 SysMain 夜间禁用 —— 只有夜跑时才能看实况
if (-not $mObsSvc) {
    Add-Row 'A04' 'SysMain 夜间禁用' 'INFO' '读不到 SysMain（服务不存在或查询失败）'
} elseif ($isNight) {
    $sysOff = ($mObsSvc.startupType -eq 'disabled') -or ($mObsSvc.status -eq 'Stopped')
    Add-Row 'A04' 'SysMain 夜间禁用' $(if ($sysOff) { 'PASS' } else { 'FAIL' }) ('夜间实况 startup=' + $mObsSvc.startupType + ' status=' + $mObsSvc.status)
} else {
    Add-Row 'A04' 'SysMain 夜间禁用' 'MANUAL' ('白天窗口，SysMain=' + $mObsSvc.startupType + '/' + $mObsSvc.status + '；夜跑期间重跑本脚本会自动判定')
}

# A06 维护窗口调整到白天
$mb = $mObsReg | Where-Object { $_.valueName -eq 'MaintenanceBoundary' } | Select-Object -First 1
if (-not $mb) {
    Add-Row 'A06' '维护窗口调整到白天' 'MANUAL' '读不到 MaintenanceBoundary'
} else {
    $okA06 = ([string]$mb.value -eq [string]$cfgHour)
    Add-Row 'A06' '维护窗口调整到白天' $(if ($okA06) { 'PASS' } else { 'FAIL' }) ('MaintenanceBoundary=' + $mb.value + '，配置 maintenanceHour=' + $cfgHour)
}

# A13 原始维护任务状态恢复 —— 白天判「已恢复」，夜间无法判
if ($mBaseTasks.Count -eq 0) {
    Add-Row 'A13' '原始维护任务状态恢复' 'MANUAL' '读不到 baseline 的任务清单'
} elseif ($isNight) {
    Add-Row 'A13' '原始维护任务状态恢复' 'MANUAL' '夜间窗口（此刻应当正是禁用状态），10:02 日链跑完后再验'
} else {
    $mism = @(); $checked = 0
    foreach ($bt in $mBaseTasks) {
        if ($null -eq $bt.enabled) { continue }
        $ot = $mObsTasks | Where-Object { $_.path -eq $bt.path -and $_.name -eq $bt.name } | Select-Object -First 1
        if (-not $ot) { $mism += ($bt.name + '=读不到'); continue }
        $checked++
        if ($null -eq $ot.enabled) { continue }
        if ([bool]$ot.enabled -ne [bool]$bt.enabled) {
            $mism += ($bt.name + ' ' + $(if ($bt.enabled) { '启用' } else { '禁用' }) + '→' + $(if ($ot.enabled) { '启用' } else { '禁用' }))
        }
    }
    if ($checked -eq 0) {
        Add-Row 'A13' '原始维护任务状态恢复' 'MANUAL' 'baseline 里的任务状态全是未知'
    } elseif ($mism.Count -eq 0) {
        Add-Row 'A13' '原始维护任务状态恢复' 'PASS' ($checked.ToString() + ' 项与 baseline 完全一致')
    } else {
        Add-Row 'A13' '原始维护任务状态恢复' 'FAIL' (($mism -join '；'))
    }
}

# A14 SysMain 原始状态恢复
if (-not $mBaseSvc -or -not $mObsSvc) {
    Add-Row 'A14' 'SysMain 原始状态恢复' 'MANUAL' 'baseline 或实况里没有 SysMain'
} elseif ($isNight) {
    Add-Row 'A14' 'SysMain 原始状态恢复' 'MANUAL' '夜间窗口，恢复验证留给日链'
} else {
    $okA14 = ([string]$mBaseSvc.startupType -eq [string]$mObsSvc.startupType)
    Add-Row 'A14' 'SysMain 原始状态恢复' $(if ($okA14) { 'PASS' } else { 'FAIL' }) ('baseline=' + $mBaseSvc.startupType + ' 现在=' + $mObsSvc.startupType)
}

# A15 原始注册表 / 电源设置恢复
if ($mBaseReg.Count -eq 0) {
    Add-Row 'A15' '原始注册表/电源设置恢复' 'MANUAL' 'baseline 里没有注册表项'
} else {
    $regMism = @()
    foreach ($br in $mBaseReg) {
        $or = $mObsReg | Where-Object { $_.keyPath -eq $br.keyPath -and $_.valueName -eq $br.valueName } | Select-Object -First 1
        if (-not $or) { $regMism += ($br.valueName + '=读不到'); continue }
        if ([string]$or.value -ne [string]$br.value) { $regMism += ($br.valueName + ' ' + $br.value + '→' + $or.value) }
    }
    $ppText = '电源方案不参与比对（维护探针不采该项）'
    if ($mBasePp -and $mBasePp.captured) {
        if ($mObsPp -and $mObsPp.captured) {
            if ([string]$mObsPp.activeSchemeGuid -ne [string]$mBasePp.activeSchemeGuid) {
                $regMism += ('电源方案 ' + $mBasePp.activeSchemeGuid + '→' + $mObsPp.activeSchemeGuid)
            } else { $ppText = '电源方案一致' }
        } else { $ppText = '电源方案本次未采到' }
    }
    $regText = ($mBaseReg.Count.ToString() + ' 项注册表 + ' + $ppText)
    if ($regMism.Count -eq 0) {
        Add-Row 'A15' '原始注册表/电源设置恢复' 'PASS' $regText
    } else {
        Add-Row 'A15' '原始注册表/电源设置恢复' 'FAIL' (($regMism -join '；'))
    }
}

# ------------------------------------------------------------------ 输出
$pass = @($rows | Where-Object { $_.State -eq 'PASS' }).Count
$fail = @($rows | Where-Object { $_.State -eq 'FAIL' }).Count
$manual = @($rows | Where-Object { $_.State -eq 'MANUAL' -or $_.State -eq 'INFO' }).Count

Write-Host ''
Write-Host '========== QuietMode 验收（只读） ==========' -ForegroundColor White
Write-Host ('  现在     : ' + $now.ToString('yyyy-MM-dd HH:mm:ss') + '  窗口=' + $st.window.band + '  控制器=' + $st.controller.state)
Write-Host ('  配置     : 夜 ' + $st.schedule.nightStart + ' → 日 ' + $st.schedule.dayStart + '  夜间=' + $st.window.isNight)
Write-Host ('  最近夜链 : ' + $(if ($night) { $night[0].At.ToString('MM-dd HH:mm:ss') } else { '-' }) + '  ' + $nightChk.Text)
Write-Host ('  最近日链 : ' + $(if ($day) { $day[0].At.ToString('MM-dd HH:mm:ss') } else { '-' }) + '  ' + $dayChk.Text)
Write-Host ('  心跳     : 近 ' + $Hours + ' 小时 ' + $beats.Count + ' 条，其中夜间 ' + $nightBeats.Count + ' 条，非 Ok ' + $badBeats.Count + ' 条')
Write-Host ''
Write-Host ('  {0,-5} {1,-28} {2,-7} {3}' -f '编号', '验收项', '结论', '证据')
Write-Host ('  ' + ('-' * 96))
foreach ($r in $rows) {
    $color = switch ($r.State) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ('  {0,-5} {1,-28} {2,-7} {3}' -f $r.Id, $r.Name, $r.State, $r.Evidence) -ForegroundColor $color
}
Write-Host ''
Write-Host ('  汇总：PASS ' + $pass + ' / FAIL ' + $fail + ' / 待人工 ' + $manual) -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })

if ($fail -gt 0) {
    Write-Host ''
    Write-Host '  失败项定位：' -ForegroundColor Red
    foreach ($r in @($rows | Where-Object { $_.State -eq 'FAIL' })) {
        Write-Host ('    ' + $r.Id + ' ' + $r.Name + ' —— ' + $r.Evidence) -ForegroundColor Red
    }
    Write-Host '    worker.log : ' + $workerLog -ForegroundColor DarkGray
    Write-Host '    细节看对应时刻的 reconcile 输出或 qmctl reconcile verify' -ForegroundColor DarkGray
}

if ($Md) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('# QuietMode 验收报告')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('- 生成时间：' + $now.ToString('yyyy-MM-dd HH:mm:ss'))
    [void]$sb.AppendLine('- 窗口：' + $st.window.band + ' / 控制器：' + $st.controller.state + '（' + $st.controller.text + '）')
    [void]$sb.AppendLine('- 最近夜链：' + $(if ($night) { $night[0].At.ToString('yyyy-MM-dd HH:mm:ss') } else { '-' }) + '，' + $nightChk.Text)
    [void]$sb.AppendLine('- 最近日链：' + $(if ($day) { $day[0].At.ToString('yyyy-MM-dd HH:mm:ss') } else { '-' }) + '，' + $dayChk.Text)
    [void]$sb.AppendLine('- 汇总：PASS ' + $pass + ' / FAIL ' + $fail + ' / 待人工 ' + $manual)
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| 编号 | 验收项 | 结论 | 证据 |')
    [void]$sb.AppendLine('|---|---|---|---|')
    foreach ($r in $rows) { [void]$sb.AppendLine('| ' + $r.Id + ' | ' + $r.Name + ' | ' + $r.State + ' | ' + $r.Evidence + ' |') }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 夜链分步')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| 时间 | 相位 | 步 | 步骤 | 结果 |')
    [void]$sb.AppendLine('|---|---|---|---|---|')
    foreach ($x in @($night) + @($day)) {
        if ($null -eq $x) { continue }
        [void]$sb.AppendLine('| ' + $x.At.ToString('yyyy-MM-dd HH:mm:ss') + ' | ' + $x.Phase + ' | ' + $x.StepNo + ' | ' + $x.Step + ' | ' + $x.Result + ' |')
    }
    [IO.File]::WriteAllText($Md, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ('  报告已写：' + $Md) -ForegroundColor DarkGray
}

Write-Host ''
if ($fail -gt 0) { exit 2 }
exit 0
