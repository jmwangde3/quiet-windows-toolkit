#Requires -Version 5.1
<#
    Fix-TaskVisibility.ps1 — 让普通用户读得到 QuietMode 任务对象（含定位实验）。

    2026-11-13 22:09 第二轮结论：
      RegisterTask(..., sddl) 把 SDDL 写进了任务 XML 的
      <RegistrationInfo><SecurityDescriptor>，但 HKLM TaskCache 里那份 SD
      一个字节没动（仍是 (A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)…(A;;FR;;;SY)），
      非提权 GetTask 依旧 0x80070005。所以「注册时传 sddl」这条路不通。

    这一版先做定位实验再按结论修：
      [1] 打印四处 TaskCache SD：任务文件夹、任务、以及微软自己的对照组。
      [2] 在 \QuietMode\ 里建临时任务 __sddl_probe，读它的 TaskCache SD，立刻删掉
          —— 回答「新任务的 SD 是不是从文件夹继承」。
      [3] 按结果三选一：
          A 文件夹 SD 缺 Users 读 → 补文件夹 SD
          B 文件夹有、任务没有     → 删掉正式任务按原 XML 重建（先备份）
          C 重建仍不行             → 直接改写任务节点的 SD 值（先备份原串）

    用法：
      .\Fix-TaskVisibility.ps1                 # 只读：打印四处 SD + 将做什么
      .\Fix-TaskVisibility.ps1 -Apply          # 真做（需要管理员）
#>
[CmdletBinding()]
param(
    [string]$TaskPath = '\QuietMode\',
    [string]$TaskName = 'QuietMode-Reconcile',
    [string]$FolderAce = '(A;OICI;FR;;;BU)',
    [string]$TaskAce = '(A;;FR;;;BU)',
    [string]$ProbeName = '__sddl_probe',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$TaskCache    = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree'
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

function Get-RegSddl {
    param([string]$Key)
    try {
        $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Key, $false)
        if (-not $k) { return $null }
        try {
            if (($k.GetValueNames()) -notcontains 'SD') { return $null }
            $v = $k.GetValue('SD')
            if ($v -is [byte[]]) {
                $raw = New-Object System.Security.AccessControl.RawSecurityDescriptor($v, 0)
                return [string]$raw.GetSddlForm('Access')
            }
            return [string]$v
        } finally { $k.Close() }
    } catch { return $null }
}

function Set-RegSddl {
    param([string]$Key, [string]$Value)
    $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Key, $true)
    if (-not $k) { throw ('键打不开：' + $Key) }
    try {
        $old = $null
        if (($k.GetValueNames()) -contains 'SD') { $old = $k.GetValue('SD') }
        if ($old -is [byte[]]) {
            $sd = New-Object System.Security.AccessControl.CommonSecurityDescriptor($true, $false, $Value)
            $bytes = New-Object byte[] $sd.BinaryLength
            $sd.GetBinaryForm($bytes, 0)
            $k.SetValue('SD', $bytes, [Microsoft.Win32.RegistryValueKind]::Binary)
        } else {
            $k.SetValue('SD', $Value, [Microsoft.Win32.RegistryValueKind]::String)
        }
    } finally { $k.Close() }
}

function Test-HasUserAce {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    return [bool]($Value -match '\(A;[^()]*;;;BU\)')
}

function Add-UserReadAce {
    param([string]$Value, [string]$Ace)
    $m = [regex]::Match($Value, '^[OG]:.*?D:')
    if (-not $m.Success) { return $null }
    $head = $m.Value                       # 包含到 D: 为止的前缀
    $rest = $Value.Substring($head.Length) # D: 之后的控制位 + ACE 列表
    $flags = [regex]::Match($rest, '^[A-Z]*').Value
    $tail = $rest.Substring($flags.Length)
    return $head + $flags + $Ace + $tail
}

function Get-XmlBody {
    param([string]$Xml)
    return [regex]::Replace($Xml, '<SecurityDescriptor>.*?</SecurityDescriptor>', '')
}

$elevated = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

$folderPath = ConvertTo-TaskFolderPath $TaskPath
$plainPath  = $folderPath.TrimStart('\')
$taskFull   = ($plainPath + '\' + $TaskName).TrimStart('\')
$folderKey  = if ($plainPath) { $TaskCache + '\' + $plainPath } else { $TaskCache }
$taskKey    = $TaskCache + '\' + $taskFull
$probeKey   = if ($plainPath) { $TaskCache + '\' + $plainPath + '\' + $ProbeName } else { $TaskCache + '\' + $ProbeName }

Write-Host ''
Write-Host '任务对象读取权限：定位 + 修复' -ForegroundColor White
Write-Host ("  任务     : " + $folderPath.TrimEnd('\') + '\' + $TaskName)
Write-Host ("  管理员   : " + $elevated)
Write-Host ("  模式     : " + $(if ($Apply) { 'Apply' } else { 'DryRun（只读）' }))
Write-Host ''

Write-Host '  [1] TaskCache 现状' -ForegroundColor DarkGray
$folderSddl = Get-RegSddl $folderKey
$taskSddl   = Get-RegSddl $taskKey
$ctlFolder  = Get-RegSddl ($TaskCache + '\Microsoft\Windows\Defrag')
$ctlTask    = Get-RegSddl ($TaskCache + '\Microsoft\Windows\Defrag\ScheduledDefrag')
Write-Host ('      文件夹  : ' + $(if ($folderSddl) { $folderSddl } else { '(读不到)' }))
Write-Host ('      任务    : ' + $(if ($taskSddl) { $taskSddl } else { '(读不到)' }))
Write-Host ('      对照夹  : ' + $(if ($ctlFolder) { $ctlFolder } else { '(读不到)' }))
Write-Host ('      对照任务: ' + $(if ($ctlTask) { $ctlTask } else { '(读不到)' }))
Write-Host ''

$folderHas = Test-HasUserAce $folderSddl
$taskHas   = Test-HasUserAce $taskSddl
Write-Host ("  文件夹有 Users 读 : " + $folderHas)
Write-Host ("  任务有 Users 读   : " + $taskHas)
Write-Host ''

if (-not $Apply) {
    Write-Host '  将做：' -ForegroundColor Yellow
    if (-not $folderHas) { Write-Host '    - 给任务文件夹 SD 追加 (A;OICI;FR;;;BU)' -ForegroundColor Yellow }
    if (-not $taskHas)   { Write-Host '    - 建探针任务判断继承；必要时重建正式任务或直接改写任务 SD' -ForegroundColor Yellow }
    Write-Host '    - 之后用普通权限复验 GetTask' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '演练结束：系统状态未改变。加 -Apply（管理员）执行。' -ForegroundColor Yellow
    exit 0
}

if (-not $elevated) {
    Write-Host '  需要管理员。' -ForegroundColor Red
    exit 3
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$recoveryDir = Join-Path $RecoveryRoot ('task-visibility-' + $stamp)
New-Item -ItemType Directory -Path $recoveryDir -Force | Out-Null
Write-Host ("  备份目录 : " + $recoveryDir) -ForegroundColor DarkGray
if ($folderSddl) { [IO.File]::WriteAllText((Join-Path $recoveryDir 'folder-sddl.txt'), $folderSddl, (New-Object System.Text.UTF8Encoding($false))) }
if ($taskSddl)   { [IO.File]::WriteAllText((Join-Path $recoveryDir 'task-sddl.txt'), $taskSddl, (New-Object System.Text.UTF8Encoding($false))) }

$svc = $null
try {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $folder = $svc.GetFolder($folderPath)
} catch {
    Write-Host ("  FAIL 打不开文件夹：" + $_.Exception.Message) -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------- A 文件夹 SD
if (-not $folderHas) {
    $new = Add-UserReadAce -Value $folderSddl -Ace $FolderAce
    if (-not $new) { Write-Host '  FAIL 文件夹 SD 格式认不出，拒绝改写' -ForegroundColor Red; exit 2 }
    try {
        Set-RegSddl -Key $folderKey -Value $new
        $folderSddl = Get-RegSddl $folderKey
        if (Test-HasUserAce $folderSddl) { Write-Host '  DONE 文件夹 SD 已追加 Users 读' -ForegroundColor Green }
        else { Write-Host '  FAIL 文件夹 SD 写后仍无 Users ACE' -ForegroundColor Red; exit 2 }
    } catch {
        Write-Host ('  FAIL 写文件夹 SD 失败：' + $_.Exception.Message) -ForegroundColor Red
        exit 2
    }
}

# ---------------------------------------------------------------- B 探针实验
$probeXml = @'
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>QuietMode</Author>
    <Description>sddl inheritance probe (temporary)</Description>
  </RegistrationInfo>
  <Triggers />
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <Enabled>false</Enabled>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>C:\Windows\System32\cmd.exe</Command>
      <Arguments>/c exit 0</Arguments>
    </Exec>
  </Actions>
</Task>
'@

$probeHas = $false
Write-Host '  [2] 探针实验' -ForegroundColor DarkGray
try {
    try { $folder.DeleteTask($ProbeName, 0) | Out-Null } catch { }
    $folder.RegisterTask($ProbeName, $probeXml, 2, 'SYSTEM', $null, 5, $null) | Out-Null
    $probeSddl = Get-RegSddl $probeKey
    $probeHas = Test-HasUserAce $probeSddl
    Write-Host ('      探针 SD : ' + $(if ($probeSddl) { $probeSddl } else { '(读不到)' }))
    Write-Host ("      新任务继承到 Users 读 : " + $probeHas)
    if ($probeSddl) { [IO.File]::WriteAllText((Join-Path $recoveryDir 'probe-sddl.txt'), [string]$probeSddl, (New-Object System.Text.UTF8Encoding($false))) }
} catch {
    Write-Host ('      WARN 探针任务建不起来：' + $_.Exception.Message) -ForegroundColor Yellow
} finally {
    try { $folder.DeleteTask($ProbeName, 0) | Out-Null } catch { }
    if (Get-RegSddl $probeKey) { Write-Host '      WARN 探针任务没删干净，手工 schtasks /delete 一下' -ForegroundColor Yellow }
}

# ---------------------------------------------------------------- C 修任务
if ($taskHas) {
    Write-Host ''
    Write-Host '  OK 任务对象本来就有 Users 读，收工' -ForegroundColor Green
    exit 0
}

$task = $null
try { $task = $folder.GetTask($TaskName) } catch {
    Write-Host ('  FAIL 取不到任务：' + $_.Exception.Message) -ForegroundColor Red
    exit 2
}
$xmlBefore  = [string]$task.Xml
$nextBefore = $null
try { $nextBefore = [datetime]$task.NextRunTime } catch { }
$backup = Join-Path $recoveryDir ($TaskName + '.xml')
[IO.File]::WriteAllText($backup, $xmlBefore, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ''
Write-Host ("  任务 XML 备份 : " + $backup) -ForegroundColor DarkGray

$fixed = $false
if ($probeHas) {
    Write-Host '  策略 A：删除后按原 XML 重建（让任务继承文件夹的 Users 读）' -ForegroundColor Yellow
    try {
        $folder.DeleteTask($TaskName, 0) | Out-Null
        $folder.RegisterTask($TaskName, $xmlBefore, 6, 'SYSTEM', $null, 5, $null) | Out-Null
        $taskSddl = Get-RegSddl $taskKey
        $fixed = Test-HasUserAce $taskSddl
    } catch {
        Write-Host ('  FAIL 重建失败：' + $_.Exception.Message) -ForegroundColor Red
        try {
            $folder.RegisterTask($TaskName, $xmlBefore, 6, 'SYSTEM', $null, 5, $null) | Out-Null
            Write-Host '  已按备份重新注册，任务还在' -ForegroundColor Yellow
        } catch {
            Write-Host ('  !! 恢复也失败，用这个文件手工注册：' + $backup) -ForegroundColor Red
        }
        exit 2
    }
} else {
    Write-Host '  策略 B：往任务节点的 SD 里追加 Users 读（保留原有 owner/ACE）' -ForegroundColor Yellow
    try {
        $newTaskSddl = Add-UserReadAce -Value $taskSddl -Ace $TaskAce
        if (-not $newTaskSddl) { throw '现有任务 SD 格式认不出，拒绝改写' }
        Set-RegSddl -Key $taskKey -Value $newTaskSddl
        $taskSddl = Get-RegSddl $taskKey
        $fixed = Test-HasUserAce $taskSddl
    } catch {
        Write-Host ('  FAIL 写任务 SD 失败：' + $_.Exception.Message) -ForegroundColor Red
        exit 2
    }
}

Write-Host ''
Write-Host '  [3] 结果' -ForegroundColor DarkGray
Write-Host ('      任务 SD : ' + $(if ($taskSddl) { $taskSddl } else { '(读不到)' }))
$final = $null
try { $final = $folder.GetTask($TaskName) } catch { }
if (-not $final) {
    Write-Host '      FAIL 任务读不回来了' -ForegroundColor Red
    exit 2
}
$stateAfter = [int]$final.State
$nextAfter = $null
try { $nextAfter = [datetime]$final.NextRunTime } catch { }
$xmlAfter = [string]$final.Xml
Write-Host ('      State=' + $stateName[$stateAfter] + '  NextRun=' + $(if ($nextAfter) { $nextAfter.ToString('yyyy-MM-dd HH:mm:ss') } else { '-' }))
$same = ((Get-XmlBody $xmlBefore) -eq (Get-XmlBody $xmlAfter))
Write-Host ('      XML 主体一致（忽略 SecurityDescriptor）: ' + $same)
if ($nextBefore -and $nextAfter -and ($nextBefore -ne $nextAfter)) {
    Write-Host ('      WARN NextRun 变了：' + $nextBefore.ToString('yyyy-MM-dd HH:mm:ss') + ' -> ' + $nextAfter.ToString('yyyy-MM-dd HH:mm:ss')) -ForegroundColor Yellow
}
if ($stateAfter -eq 1) { Write-Host '      WARN 任务变成 Disabled，需要 Enable-ScheduledTask' -ForegroundColor Yellow }

Write-Host ''
if ($fixed) {
    Write-Host '完成。用普通权限跑 Grant-TaskRead.ps1 -Check 应该能看到 1 个任务 Ready。' -ForegroundColor Green
    exit 0
}
Write-Host '没修好：三份 SD 都留在备份目录，下一步只能试重启 Task Scheduler 服务或换数据源。' -ForegroundColor Red
exit 2
