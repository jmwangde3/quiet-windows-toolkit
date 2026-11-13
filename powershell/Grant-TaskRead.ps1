#Requires -Version 5.1
<#
    Grant-TaskRead.ps1 — 让普通用户能「看到」计划任务，而不只是能读任务文件。

    背景（2026-11-13 实测）：
      安装器原来只给 C:\Windows\System32\Tasks\QuietMode\QuietMode-Reconcile
      加了 Users:ReadAndExecute，但 Task Scheduler 的访问检查先过「文件夹」
      这一关。普通用户枚举 216 个任务时，名字带 Quiet 的一个都看不到 ——
      文件夹 \QuietMode\ 与 \QuietHDD\ 的安全描述符里没有 Users 的读 ACE，
      里面的任务连枚举都出不来，托盘于是永远显示「已注册、无读取权限」。

    做法：只往文件夹 DACL 里加一条 Users 只读 ACE（含 OI/CI 继承），
    不改、不删任何已有 ACE；SDDL 原值打印出来，出问题可以照抄回去。
    重复执行安全（已有 ACE 就跳过），非管理员执行只报计划不动手。

    用法：
      .\Grant-TaskRead.ps1                        # 演练：只说要改什么
      .\Grant-TaskRead.ps1 -Apply                 # 真改（需要管理员）
      .\Grant-TaskRead.ps1 -Apply -Folders '\QuietMode\','\QuietHDD\'
#>
[CmdletBinding()]
param(
    [string[]]$Folders = @('\QuietMode\', '\QuietHDD\'),
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# DACL_SECURITY_INFORMATION = 0x00000004。只要 DACL 段，SACL 不碰。
$DaclOnly = 4
# Users 只读 + 目录/对象继承：GR = 读，GX = 执行，OICI = 子对象继承
$UsersReadAce = '(A;OICI;GRGX;;;BU)'

$elevated = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

Write-Host ''
Write-Host '计划任务文件夹读取权限' -ForegroundColor White
Write-Host ("  管理员 : " + $elevated)
Write-Host ("  模式   : " + $(if ($Apply) { 'Apply' } else { 'DryRun（只报计划）' }))
Write-Host ''

if (-not ($Apply -and $elevated)) {
    if ($Apply -and -not $elevated) {
        Write-Host '  需要管理员：改任务计划器的安全描述符要提权。' -ForegroundColor Yellow
    }
}

$failed = 0
foreach ($path in $Folders) {
    if (-not $path) { continue }
    if (-not $path.StartsWith('\')) { $path = '\' + $path }
    if (-not $path.EndsWith('\')) { $path = $path + '\' }

    Write-Host ("[" + $path + "]")
    $svc = $null
    $folder = $null
    try {
        # 每次新建实例：一个实例连接失败不该影响下一个文件夹
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $folder = $svc.GetFolder($path)
    } catch {
        Write-Host ("  FAIL 打不开文件夹：" + $_.Exception.Message) -ForegroundColor Red
        $failed++
        continue
    }

    $before = $null
    try { $before = [string]$folder.GetSecurityDescriptor($DaclOnly) }
    catch {
        Write-Host ("  FAIL 读不到安全描述符：" + $_.Exception.Message) -ForegroundColor Red
        $failed++
        continue
    }
    Write-Host ("  DACL 原值 : " + $before)

    if ($before -match '\(A;[^()]*;;;BU\)') {
        Write-Host '  OK   已有 Users 读权限，跳过' -ForegroundColor Green
        continue
    }
    if (-not $Apply) {
        Write-Host ("  将追加   : " + $UsersReadAce + "（Users 只读，含继承）") -ForegroundColor Yellow
        continue
    }
    if (-not $elevated) {
        Write-Host '  SKIP 未提权，不改' -ForegroundColor Yellow
        continue
    }

    # 把 ACE 插在 D: 之后、已有 ACE 之前。列表顺序不影响允许类 ACE 的判定，
    # 但保持「先加的新 ACE 在最前」便于人眼比对。
    $after = $before -replace '^D:', ('D:' + $UsersReadAce)
    try {
        $folder.SetSecurityDescriptor($after, 0)
    } catch {
        Write-Host ("  FAIL 写入失败：" + $_.Exception.Message) -ForegroundColor Red
        Write-Host ("  可用原值回滚：SetSecurityDescriptor('" + $before + "', 0)") -ForegroundColor Yellow
        $failed++
        continue
    }

    $check = $null
    try { $check = [string]$folder.GetSecurityDescriptor($DaclOnly) } catch { }
    if ($check -and $check -match '\(A;[^()]*;;;BU\)') {
        Write-Host ("  DONE 已追加 : " + $check) -ForegroundColor Green
    } else {
        Write-Host '  FAIL 写入后回读不到 Users ACE' -ForegroundColor Red
        $failed++
    }
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host ("结果：" + $failed + " 个文件夹失败") -ForegroundColor Red
    exit 2
}
if (-not $Apply) {
    Write-Host '演练结束：系统状态未改变。加 -Apply（管理员）执行。' -ForegroundColor Yellow
} else {
    Write-Host '完成。普通用户现在应能枚举到这些文件夹里的任务。' -ForegroundColor Green
}
exit 0
