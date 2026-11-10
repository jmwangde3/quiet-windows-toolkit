<#
.SYNOPSIS
  修复夜间闸门任务注册：确保 \QuietHDD\QuietHDD_NightGate 真正落地。

.DESCRIPTION
  quiet-hdd-daytime.ps1 的第 4 步注册任务失败时只打印黄字就继续，
  结果闸门脚本存在、维护时间也写好了，但计划任务不存在 —— 夜间没有任何拦截。
  本脚本用 4 种方式依次尝试注册，每种方式的报错都写进日志，
  最后强制校验，明确输出 PASS / FAIL。

  日志：C:\ProgramData\QuietHDD\install.log

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-fix.ps1"
#>
[CmdletBinding()]
param(
    [switch]$NoElevate,
    [switch]$FromElevated
)

$ErrorActionPreference = 'Continue'
$GateDir    = 'C:\ProgramData\QuietHDD'
$GatePs1    = Join-Path $GateDir 'gate.ps1'
$GateXml    = Join-Path $GateDir 'QuietHDD_NightGate.xml'
$InstallLog = Join-Path $GateDir 'install.log'
$TaskName   = 'QuietHDD_NightGate'
$TaskPath   = '\QuietHDD\'

function Ensure-GateDir {
    if (-not (Test-Path -LiteralPath $GateDir)) {
        try { New-Item -ItemType Directory -Path $GateDir -Force -ErrorAction Stop | Out-Null } catch { }
    }
}

function Log {
    param([string]$Text, [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    try {
        Ensure-GateDir
        $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $Text
        [System.IO.File]::AppendAllText($InstallLog, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    } catch { }
}

# ---------------- 提权 ----------------
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated) {
    Log '需要管理员权限，正在请求 UAC 提权...' 'Cyan'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-fix.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated'
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        Log '已在管理员窗口执行，请看那个窗口。' 'Green'
    } catch {
        Log ('提权失败: ' + $_.Exception.Message) 'Yellow'
        Log '请手动以管理员身份打开 PowerShell，运行：' 'Yellow'
        Log ('  powershell -ExecutionPolicy Bypass -NoProfile -File "' + $PSCommandPath + '" -NoElevate') 'Yellow'
    }
    return
}

Ensure-GateDir
Log '========================================' 'Cyan'
Log 'QuietHDD 闸门任务注册修复开始' 'Cyan'
Log ('时间     : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Log ('管理员   : ' + $IsAdmin)
Log ('PS 版本  : ' + $PSVersionTable.PSVersion.ToString())
Log ('脚本路径 : ' + $PSCommandPath)
Log '========================================' 'Cyan'

# ---------------- 1. 重写闸门脚本 ----------------
Log '=== 1/4 重写闸门脚本 gate.ps1 ===' 'Cyan'
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
    [System.IO.File]::WriteAllText($GatePs1, $GateBody, (New-Object System.Text.UTF8Encoding($true)))
    Log ('  已写入 ' + $GatePs1 + ' (' + (Get-Item -LiteralPath $GatePs1).Length + ' bytes, UTF-8 BOM)') 'Green'
} catch {
    Log ('  写入失败: ' + $_.Exception.Message) 'Yellow'
}

# ---------------- 2. 准备任务 XML ----------------
Log '=== 2/4 生成任务 XML ===' 'Cyan'
$today = Get-Date -Format 'yyyy-MM-dd'
$xmlBody = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>SYSTEM</Author>
    <Description>Quiet HDD night gate: disable disk maintenance tasks 23:58-10:02</Description>
    <URI>\QuietHDD\QuietHDD_NightGate</URI>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>${today}T23:58:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
    </CalendarTrigger>
    <CalendarTrigger>
      <StartBoundary>${today}T10:02:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
    </CalendarTrigger>
    <BootTrigger>
      <Enabled>true</Enabled>
      <Delay>PT2M</Delay>
    </BootTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <LogonType>ServiceAccount</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\QuietHDD\gate.ps1"</Arguments>
    </Exec>
  </Actions>
</Task>
"@
$xmlForCmdlet = ($xmlBody -split "`r?`n" | Where-Object { $_ -notmatch '^\s*<\?xml' }) -join "`r`n"
try {
    [System.IO.File]::WriteAllText($GateXml, $xmlBody, (New-Object System.Text.UnicodeEncoding($false, $true)))
    Log ('  已写入 ' + $GateXml + ' (UTF-16 LE + BOM)') 'Green'
} catch {
    Log ('  写 XML 失败: ' + $_.Exception.Message) 'Yellow'
}

# ---------------- 3. 四种方式依次注册 ----------------
Log '=== 3/4 注册计划任务 ===' 'Cyan'
$registered = $false
$winner = ''

# 方式 A：对象方式
if (-not $registered) {
    Log '  [A] Register-ScheduledTask（Action/Trigger/Settings 对象）'
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\QuietHDD\gate.ps1"'
        $tr1 = New-ScheduledTaskTrigger -Daily -At '23:58'
        $tr2 = New-ScheduledTaskTrigger -Daily -At '10:02'
        $tr3 = New-ScheduledTaskTrigger -AtStartup
        try { $tr3.Delay = 'PT2M' } catch { Log ('      Delay 设置跳过: ' + $_.Exception.Message) 'Yellow' }
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
        $prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Action $action -Trigger @($tr1, $tr2, $tr3) -Settings $set -Principal $prn -Force -ErrorAction Stop | Out-Null
        $registered = $true; $winner = 'A:Register-ScheduledTask/objects'
        Log '      OK' 'Green'
    } catch {
        Log ('      FAIL: ' + $_.Exception.Message) 'Yellow'
        if ($_.Exception.InnerException) { Log ('      INNER: ' + $_.Exception.InnerException.Message) 'Yellow' }
        if ($_.CategoryInfo) { Log ('      CATEGORY: ' + $_.CategoryInfo.ToString()) 'Yellow' }
    }
}

# 方式 B：先删后建
if (-not $registered) {
    Log '  [B] 先 Unregister 再 Register（清理残留后重试）'
    try {
        $old = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($old) { Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue; Log '      已删除旧任务' }
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\QuietHDD\gate.ps1"'
        $tr1 = New-ScheduledTaskTrigger -Daily -At '23:58'
        $tr2 = New-ScheduledTaskTrigger -Daily -At '10:02'
        $tr3 = New-ScheduledTaskTrigger -AtStartup
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
        $prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Action $action -Trigger @($tr1, $tr2, $tr3) -Settings $set -Principal $prn -ErrorAction Stop | Out-Null
        $registered = $true; $winner = 'B:Unregister+Register'
        Log '      OK' 'Green'
    } catch {
        Log ('      FAIL: ' + $_.Exception.Message) 'Yellow'
        if ($_.Exception.InnerException) { Log ('      INNER: ' + $_.Exception.InnerException.Message) 'Yellow' }
    }
}

# 方式 C：XML
if (-not $registered) {
    Log '  [C] Register-ScheduledTask -Xml'
    try {
        Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Xml $xmlForCmdlet -Force -ErrorAction Stop | Out-Null
        $registered = $true; $winner = 'C:Register-ScheduledTask/Xml'
        Log '      OK' 'Green'
    } catch {
        Log ('      FAIL: ' + $_.Exception.Message) 'Yellow'
        if ($_.Exception.InnerException) { Log ('      INNER: ' + $_.Exception.InnerException.Message) 'Yellow' }
    }
}

# 方式 D：schtasks 导入 XML
if (-not $registered) {
    Log '  [D] schtasks /create /xml'
    try {
        $out = & schtasks.exe /create /tn ($TaskPath + $TaskName) /xml $GateXml /f 2>&1
        Log ('      output: ' + (($out | Out-String).Trim() -replace "`r`n", ' | ')) 'Gray'
        $chk = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($chk) { $registered = $true; $winner = 'D:schtasks/xml'; Log '      OK' 'Green' }
        else { Log '      FAIL: schtasks 返回但任务仍不存在' 'Yellow' }
    } catch {
        Log ('      FAIL: ' + $_.Exception.Message) 'Yellow'
    }
}

# 方式 E：不建子文件夹，注册到根目录
if (-not $registered) {
    Log '  [E] 注册到任务计划程序根目录（不建 \QuietHDD\ 子文件夹）'
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ProgramData\QuietHDD\gate.ps1"'
        $tr1 = New-ScheduledTaskTrigger -Daily -At '23:58'
        $tr2 = New-ScheduledTaskTrigger -Daily -At '10:02'
        $tr3 = New-ScheduledTaskTrigger -AtStartup
        $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
        $prn = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($tr1, $tr2, $tr3) -Settings $set -Principal $prn -Force -ErrorAction Stop | Out-Null
        $registered = $true; $winner = 'E:root-folder/objects'
        Log '      OK' 'Green'
    } catch {
        Log ('      FAIL: ' + $_.Exception.Message) 'Yellow'
        if ($_.Exception.InnerException) { Log ('      INNER: ' + $_.Exception.InnerException.Message) 'Yellow' }
    }
}

# 方式 F：根目录 + schtasks 导入 XML
if (-not $registered) {
    Log '  [F] schtasks /create 到根目录（XML 去掉子文件夹）'
    try {
        $rootXml = $xmlBody.Replace('<URI>\QuietHDD\QuietHDD_NightGate</URI>', '<URI>\QuietHDD_NightGate</URI>')
        $rootXmlPath = Join-Path $GateDir 'QuietHDD_NightGate_root.xml'
        [System.IO.File]::WriteAllText($rootXmlPath, $rootXml, (New-Object System.Text.UnicodeEncoding($false, $true)))
        $out = & schtasks.exe /create /tn $TaskName /xml $rootXmlPath /f 2>&1
        Log ('      output: ' + (($out | Out-String).Trim() -replace "`r`n", ' | ')) 'Gray'
        $chk = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($chk) { $registered = $true; $winner = 'F:schtasks/root'; Log '      OK' 'Green' }
        else { Log '      FAIL: schtasks 返回但任务仍不存在' 'Yellow' }
    } catch {
        Log ('      FAIL: ' + $_.Exception.Message) 'Yellow'
    }
}

# ---------------- 4. 校验 ----------------
Log '=== 4/4 校验 ===' 'Cyan'
$g = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $g) { $g = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
if ($g) {
    $gi = $g | Get-ScheduledTaskInfo
    Log ('  PASS  任务存在: ' + $g.TaskPath + $g.TaskName) 'Green'
    Log ('        State=' + [string]$g.State + '  LastRun=' + $gi.LastRunTime + '  NextRun=' + $gi.NextRunTime + '  LastResult=' + $gi.LastTaskResult) 'Green'
    Log ('        Principal=' + $g.Principal.UserId + ' / ' + $g.Principal.LogonType + ' / ' + $g.Principal.RunLevel) 'Green'
    foreach ($t in $g.Triggers) {
        Log ('        Trigger: ' + $t.CimClass.CimClassName + '  Start=' + $t.StartBoundary + '  Delay=' + $t.Delay + '  Enabled=' + $t.Enabled) 'Green'
    }
    Log ('  使用的方式: ' + $winner) 'Green'
    try { Enable-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop | Out-Null; Log '  任务已启用' 'Green' } catch { Log ('  启用失败: ' + $_.Exception.Message) 'Yellow' }
} else {
    Log '  FAIL  任务仍然不存在 —— 所有注册方式都失败了' 'Red'
    Log '  请把本窗口里 [A]~[D] 的 FAIL 行拍下来，或直接把下面的日志文件发出来：' 'Yellow'
    Log ('  ' + $InstallLog) 'Yellow'
    Log '  手动兜底：任务计划程序(taskschd.msc) -> 操作 -> 导入任务 -> 选下面这个 XML' 'Yellow'
    Log ('  ' + $GateXml) 'Yellow'
}

# 立即按当前时间执行一次，保持状态一致
Log '--- 立即执行一次闸门（按当前时间自动决定白天/夜间） ---' 'Cyan'
try {
    & $GatePs1
    $tail = Get-Content -LiteralPath (Join-Path $GateDir 'gate.log') -Encoding UTF8 -Tail 3
    foreach ($l in $tail) { Log ('  ' + $l) }
} catch {
    Log ('  执行闸门失败: ' + $_.Exception.Message) 'Yellow'
    if ($_.InvocationInfo) { Log ('  at line ' + $_.InvocationInfo.ScriptLineNumber + ': ' + $_.InvocationInfo.Line.Trim()) 'Yellow' }
}

Log '========================================' 'Cyan'
Log '修复结束。完整日志：' 'Cyan'
Log ('  ' + $InstallLog) 'Cyan'
Log '========================================' 'Cyan'
