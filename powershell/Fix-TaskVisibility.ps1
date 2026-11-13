#Requires -Version 5.1
<#
    Fix-TaskVisibility.ps1 — 让普通用户读得到任务对象（重新注册法，多目标任务）。

    背景（2026-11-13 实测链）：
      * Task Scheduler 为每个任务在 HKLM\...\Schedule\TaskCache\Tree\<任务路径>\SD
        里另存一份自相对安全描述符，访问检查用的是这一份，跟任务文件 ACL、
        跟文件夹继承都无关。
      * 这份副本是「创建任务那一刻」算出来的：
          - RegisterTask(..., sddl:null)  → 只有 BA/SY 两条，普通用户 0x80070005
          - 对已存在的任务再 RegisterTask(..., sddl) → 只改到 XML 里的
            <RegistrationInfo><SecurityDescriptor>，TaskCache 那份纹丝不动
          - DeleteTask 之后重新 RegisterTask(..., sddl) → 这次才真的落到 TaskCache
      * IRegisteredTask::SetSecurityDescriptor 在同机实测里「不报错但也不生效」，
        回读为空 —— 所以本脚本把它只当快路径，失败就退回删+重建。

    安全性：
      * 删之前把任务 XML 全文备份到 C:\ProgramData\QuietMode\recovery\
        task-visibility-<时间戳>\，回滚 = 拿备份再 RegisterTask 一次。
      * 重建用的是任务自己的 XML 原文，只替换/插入 <SecurityDescriptor>；
        触发器、Actions、Principal 一字不改。
      * 重建会把任务「顺手启用」，所以脚本按原 Enabled 状态回填，并在 XML 里
        预先写 <Enabled>false</Enabled>，最后再核对 State 与原值一致。
      * 已经带 Users 读的任务直接跳过，重复执行安全。

    用法：
      .\Fix-TaskVisibility.ps1                  # 演练：只读，说清要动哪几个
      .\Fix-TaskVisibility.ps1 -Apply           # 真做（需要管理员）
      .\Fix-TaskVisibility.ps1 -Apply -Targets '\QuietHDD\QuietHDD_NightGate'
#>
[CmdletBinding()]
param(
    [string[]]$Targets = @(
        '\QuietMode\QuietMode-Reconcile',
        '\QuietHDD\QuietHDD_NightGate',
        '\QuietHDD\QuietHDD_DiskGate'
    ),
    # 管理员/系统全控，普通用户读
    [string]$DesiredSddl = 'D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;BU)',
    [string]$UsersReadAce = '(A;;FR;;;BU)',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$RecoveryRoot = Join-Path $env:ProgramData 'QuietMode\recovery'
$stateName    = @('Unknown', 'Disabled', 'Queued', 'Ready', 'Running')

function ConvertTo-TaskFolderPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '\' }
    $p = $Path.Trim()
    if (-not $p.StartsWith('\')) { $p = '\' + $p }
    while ($p.Length -gt 1 -and $p.EndsWith('\')) { $p = $p.Substring(0, $p.Length - 1) }
    return $p
}

function Split-TaskFullName {
    param([string]$Full)
    $p = ConvertTo-TaskFolderPath $Full
    $i = $p.LastIndexOf('\')
    $folder = if ($i -le 0) { '\' } else { $p.Substring(0, $i) }
    $name = $p.Substring($i + 1)
    return [pscustomobject]@{ Folder = $folder; Name = $name }
}

function Test-HasUserAce {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    return [bool]($Value -match '\(A;[^()]*;;;BU\)')
}

function Get-TaskSd {
    param($Task)
    foreach ($f in 7, 4, 1) {
        try {
            $s = [string]$Task.GetSecurityDescriptor($f)
            if ($s) { return $s }
        } catch { }
    }
    return $null
}

function Get-XmlTriggerCount {
    param([string]$Xml)
    if (-not $Xml) { return 0 }
    return ([regex]::Matches($Xml, '<(CalendarTrigger|BootTrigger|EventTrigger|LogonTrigger|TimeTrigger|RegistrationTrigger|IdleTrigger)')).Count
}

function Get-XmlEnabled {
    param([string]$Xml)
    $m = [regex]::Match($Xml, '<Enabled>\s*([a-zA-Z]+)\s*</Enabled>')
    if (-not $m.Success) { return $null }
    return ($m.Groups[1].Value -notmatch '^(?i:false|0)$')
}

function Set-XmlSecurityDescriptor {
    param([string]$Xml, [string]$Sddl)
    $x = [regex]::Replace($Xml, '<SecurityDescriptor>.*?</SecurityDescriptor>', '')
    if ($x -notmatch '<RegistrationInfo[^>]*>') { throw 'XML 里没有 <RegistrationInfo>，拒绝改写' }
    return ($x -replace '(<RegistrationInfo[^>]*>)', ('$1' + '<SecurityDescriptor>' + $Sddl + '</SecurityDescriptor>'))
}

function Set-XmlDisabled {
    param([string]$Xml)
    if ($Xml -match '<Enabled>\s*false\s*</Enabled>') { return $Xml }
    if ($Xml -match '<Enabled>') {
        return ([regex]::Replace($Xml, '<Enabled>.*?</Enabled>', '<Enabled>false</Enabled>'))
    }
    if ($Xml -notmatch '</Settings>') { throw 'XML 里没有 </Settings>，无法写 <Enabled>false</Enabled>' }
    return ($Xml -replace '</Settings>', '<Enabled>false</Enabled></Settings>')
}

function Get-PrincipalHint {
    param([string]$Xml)
    $mu = [regex]::Match($Xml, '<UserId>([^<]*)</UserId>')
    $mg = [regex]::Match($Xml, '<GroupId>([^<]*)</GroupId>')
    $uid = if ($mu.Success) { $mu.Groups[1].Value.Trim() } else { '' }
    if ($uid -match '^(?i:SYSTEM|NT AUTHORITY\\SYSTEM|S-1-5-18)$') {
        return [pscustomobject]@{ UserId = 'SYSTEM'; LogonType = 5; Kind = 'SYSTEM (服务账户)' }
    }
    if ([string]::IsNullOrEmpty($uid) -and -not $mg.Success) {
        return [pscustomobject]@{ UserId = 'SYSTEM'; LogonType = 5; Kind = '缺省按 SYSTEM 试' }
    }
    if ($mg.Success -and [string]::IsNullOrEmpty($uid)) {
        return [pscustomobject]@{ UserId = $null; LogonType = 4; Kind = 'GroupId=' + $mg.Groups[1].Value }
    }
    return [pscustomobject]@{ UserId = $null; LogonType = 3; Kind = '当前交互用户 ' + $uid }
}

# ------------------------------------------------------------------ 环境
$elevated = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

Write-Host ''
Write-Host '任务对象读取权限：修 SDDL（重新注册法）' -ForegroundColor White
Write-Host ("  目标     : " + $Targets.Count + " 个任务")
Write-Host ("  管理员   : " + $elevated)
Write-Host ("  模式     : " + $(if ($Apply) { 'Apply' } else { 'DryRun（只读）' }))
Write-Host ("  写入 SDDL: " + $DesiredSddl)
Write-Host ''

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$recoveryDir = Join-Path $RecoveryRoot ('task-visibility-' + $stamp)
if ($Apply -and $elevated) {
    New-Item -ItemType Directory -Path $recoveryDir -Force | Out-Null
    Write-Host ("  备份目录 : " + $recoveryDir) -ForegroundColor DarkGray
    Write-Host ''
}

$fail = 0
$fixed = 0
$skipped = 0

foreach ($target in $Targets) {
    $parts = Split-TaskFullName $target
    $folderPath = $parts.Folder
    $name = $parts.Name
    $full = ($folderPath.TrimEnd('\') + '\' + $name)

    Write-Host ("[" + $full + "]")

    $svc = $null
    $folder = $null
    $task = $null
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $folder = $svc.GetFolder($folderPath)
        $task = $folder.GetTask($name)
    } catch {
        if (-not $elevated -and -not $Apply) {
            Write-Host '  SKIP 演练模式读不到（需要管理员）' -ForegroundColor Yellow
        } else {
            Write-Host ('  FAIL 打不开任务：' + $_.Exception.Message) -ForegroundColor Red
            $fail++
        }
        try { if ($svc) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } } catch { }
        continue
    }

    $sdBefore = Get-TaskSd $task
    if (Test-HasUserAce $sdBefore) {
        Write-Host '  OK   已有 Users 读，跳过' -ForegroundColor Green
        $skipped++
        try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
        continue
    }

    $xmlBefore = [string]$task.Xml
    $enabledBefore = [bool]$task.Enabled
    $stateBefore = [int]$task.State
    $trigBefore = Get-XmlTriggerCount $xmlBefore
    $xmlEnabled = Get-XmlEnabled $xmlBefore
    $nextBefore = $null
    try { $nextBefore = [datetime]$task.NextRunTime } catch { }
    $principal = Get-PrincipalHint $xmlBefore

    Write-Host ('  SD 现值  : ' + $(if ($sdBefore) { $sdBefore } else { '(读不到)' }))
    Write-Host ('  状态     : Enabled=' + $enabledBefore + '  State=' + $stateName[$stateBefore] + '  触发器=' + $trigBefore)
    Write-Host ('  XML Enabled : ' + $(if ($null -eq $xmlEnabled) { '(未写)' } else { [string]$xmlEnabled }))
    Write-Host ('  Principal: ' + $principal.Kind)

    if (-not $Apply) {
        Write-Host '  将做     : 先试 SetSecurityDescriptor；不生效则备份 XML → 删任务 → 按原 XML 重建（带新 SDDL）→ 回填 Enabled' -ForegroundColor Yellow
        Write-Host ''
        try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
        continue
    }
    if (-not $elevated) {
        Write-Host '  SKIP 未提权' -ForegroundColor Yellow
        try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
        continue
    }

    # ---- 备份
    $safe = $name -replace '[\\/:*?"<>|]', '_'
    $xmlBak = Join-Path $recoveryDir ($name + '.xml')
    [IO.File]::WriteAllText($xmlBak, $xmlBefore, (New-Object System.Text.UTF8Encoding($false)))
    if ($sdBefore) {
        [IO.File]::WriteAllText((Join-Path $recoveryDir ($safe + '.sddl.txt')), $sdBefore + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    Write-Host ('  备份 XML : ' + $xmlBak) -ForegroundColor DarkGray

    $ok = $false
    $how = ''

    # ---- 快路径：SetSecurityDescriptor（不删任务）
    $newSddl = $null
    if ($sdBefore) { $newSddl = $sdBefore.TrimEnd() + $UsersReadAce }
    if ($newSddl) {
        try {
            $task.SetSecurityDescriptor($newSddl, 0)
            Start-Sleep -Milliseconds 120
            $probe = $folder.GetTask($name)
            $sdNow = Get-TaskSd $probe
            if (Test-HasUserAce $sdNow) {
                $ok = $true
                $how = 'SetSecurityDescriptor'
                Write-Host ('  DONE 快路径成功 → ' + $sdNow) -ForegroundColor Green
            } else {
                Write-Host ('  WARN 快路径没生效（回读：' + $(if ($sdNow) { $sdNow } else { '空' }) + '），改走重建') -ForegroundColor Yellow
            }
        } catch {
            Write-Host ('  WARN 快路径异常：' + $_.Exception.Message + '，改走重建') -ForegroundColor Yellow
        }
    }

    # ---- 稳健路径：删 + 按原 XML 重建
    if (-not $ok) {
        $xmlNew = Set-XmlSecurityDescriptor -Xml $xmlBefore -Sddl $DesiredSddl
        if (-not $enabledBefore) { $xmlNew = Set-XmlDisabled $xmlNew }
        $xmlNewBak = Join-Path $recoveryDir ($name + '.new.xml')
        [IO.File]::WriteAllText($xmlNewBak, $xmlNew, (New-Object System.Text.UTF8Encoding($false)))

        $registered = $false
        try {
            $folder.DeleteTask($name, 0)
            $newTask = $folder.RegisterTask($name, $xmlNew, 2, $principal.UserId, $null, $principal.LogonType, $DesiredSddl)
            $registered = $true
            if (-not $enabledBefore) {
                try { $newTask.Enabled = $false } catch { Write-Host ('  WARN 回填 Enabled=false 失败：' + $_.Exception.Message) -ForegroundColor Yellow }
            }
        } catch {
            Write-Host ('  FAIL 重建失败：' + $_.Exception.Message) -ForegroundColor Red
            # 兜底：按备份原样注册回去，任务不能丢
            try {
                $folder.RegisterTask($name, $xmlBefore, 6, $principal.UserId, $null, $principal.LogonType, $null) | Out-Null
                $r = $folder.GetTask($name)
                if (-not $enabledBefore) { try { $r.Enabled = $false } catch { } }
                Write-Host '  已按备份原样注册回去（SD 未改），任务还在' -ForegroundColor Yellow
            } catch {
                Write-Host ('  !! 恢复也失败，手工注册这个文件：' + $xmlBak) -ForegroundColor Red
            }
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
            $fail++
            continue
        }

        if ($registered) {
            Start-Sleep -Milliseconds 150
            $after = $null
            try { $after = $folder.GetTask($name) } catch { }
            if (-not $after) {
                Write-Host '  FAIL 重建后取不到任务' -ForegroundColor Red
                try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
                $fail++
                continue
            }
            $sdAfter = Get-TaskSd $after
            $enabledAfter = [bool]$after.Enabled
            $stateAfter = [int]$after.State
            $xmlAfter = [string]$after.Xml
            $trigAfter = Get-XmlTriggerCount $xmlAfter

            if (-not (Test-HasUserAce $sdAfter)) {
                Write-Host ('  FAIL 重建后 SD 仍无 Users 读：' + $(if ($sdAfter) { $sdAfter } else { '空' })) -ForegroundColor Red
                try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
                $fail++
                continue
            }

            # 状态核对：Disabled 的必须还是 Disabled
            if ($enabledAfter -ne $enabledBefore) {
                Write-Host ('  WARN Enabled 变了：' + $enabledBefore + ' -> ' + $enabledAfter + '，回填') -ForegroundColor Yellow
                try { $after.Enabled = $enabledBefore; Start-Sleep -Milliseconds 100 } catch { }
                $after = $folder.GetTask($name)
                $enabledAfter = [bool]$after.Enabled
                $stateAfter = [int]$after.State
            }

            Write-Host ('  DONE 重建成功  State=' + $stateName[$stateAfter] + '  Enabled=' + $enabledAfter + '  触发器=' + $trigAfter) -ForegroundColor Green
            Write-Host ('       SD : ' + $sdAfter) -ForegroundColor DarkGray
            if ($trigAfter -ne $trigBefore) {
                Write-Host ('       WARN 触发器数变了：' + $trigBefore + ' -> ' + $trigAfter) -ForegroundColor Yellow
            }
            if ($enabledAfter -ne $enabledBefore -or $stateAfter -ne $stateBefore) {
                Write-Host ('       FAIL 状态没回到原样：' + $stateName[$stateBefore] + ' -> ' + $stateName[$stateAfter]) -ForegroundColor Red
                try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
                $fail++
                continue
            }
            $how = 'delete + re-register'
            $ok = $true
        }
    }

    if ($ok) { $fixed++ } else { $fail++ }
    try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
    Write-Host ''
}

Write-Host ''
Write-Host ('汇总：修好 ' + $fixed + ' 个，跳过 ' + $skipped + ' 个，失败 ' + $fail + ' 个')
if ($Apply -and $elevated -and (Test-Path -LiteralPath $recoveryDir)) {
    Write-Host ('备份/回滚素材：' + $recoveryDir) -ForegroundColor DarkGray
    Write-Host '回滚单个任务：拿 <任务名>.xml 用 RegisterTask 再注册一次即可。' -ForegroundColor DarkGray
}
if ($fail -gt 0) { exit 2 }
if (-not $Apply) {
    Write-Host '演练结束：系统状态未改变。加 -Apply（管理员）执行。' -ForegroundColor Yellow
} else {
    Write-Host '完成。用普通权限跑 Grant-TaskRead.ps1 -Check 复验（不要用管理员窗口）。' -ForegroundColor Green
}
exit 0
