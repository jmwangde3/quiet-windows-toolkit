#Requires -Version 5.1
<#
    Grant-TaskRead.ps1 — 让普通用户能「看到」计划任务，而不只是能读任务文件。

    背景（2026-11-13 实测）：
      安装器原来只给 C:\Windows\System32\Tasks\QuietMode\QuietMode-Reconcile
      加了 Users:ReadAndExecute，但 Task Scheduler 的访问检查先过「文件夹」
      这一关。普通用户枚举 216 个任务时，名字带 Quiet 的一个都看不到 ——
      文件夹 \QuietMode\ 与 \QuietHDD\ 的安全描述符里没有 Users 的读 ACE，
      里面的任务连枚举都出不来，托盘于是永远显示「已注册、无读取权限」。

    2026-11-13 22:0x 修复三处（21:58 那轮 2 个文件夹全 FAIL 的原因）：
      1) ITaskFolder::GetFolder 不接受结尾反斜杠：'\QuietMode\' 直接
         0x8007007B「文件名、目录名或卷标语法不正确」。必须归一化成 '\QuietMode'。
      2) SDDL 的控制位（AI / PAI）必须紧跟 D:，ACE 只能插在它后面。旧代码
         `-replace '^D:', ...` 会产出 D:(A;...;;;BU)AI(A;...;;;BA) 这种非法串。
      3) 权限位从 GRGX 改成 FR：照抄 \Microsoft\Windows 给 AU 的读法（同机实测
         非提权可枚举该文件夹下的任务），generic read 由服务端自己映射成任务权限。

    做法：只往 DACL 里加一条 Users 只读 ACE（含 OI/CI 继承），不改、不删任何
    已有 ACE；SDDL 原值打印出来，出问题可以照抄回去。文件夹改完再补每个任务
    文件自己的只读 ACE（已有就不动）。重复执行安全，非管理员执行只报计划不动手。

    2026-11-13 22:3x 追加实测：文件夹 ACE 对**已存在**的任务无效。
      \QuietHDD\ 文件夹早就补了 (A;OICI;FR;;;BU)，里面的两个任务依旧 0x80070005 ——
      每个任务的 SD 是注册那一刻的快照，另存在 TaskCache\Tree\<任务路径>\SD
      那份自相对二进制里，访问检查用的是它，事后改文件夹继承不下来。
      → 加 -IncludeTasks：逐个任务 GetSecurityDescriptor / SetSecurityDescriptor
        追加一条 (A;;FR;;;BU)。走服务自己的 API，TaskCache 那份由服务更新；
        不删任务、不重注册、触发器与 principal 一个字节都不动；原值先备份。

    两条死路，别再走（22:1x 各踩一次）：
      a) 直接写 HKLM\...\TaskCache\Tree\<路径>\SD —— 那键默认只给 SYSTEM/管理员读，
         普通用户那句会误报成「任务未注册」；就算提权写了，服务也未必重载。
      b) 按注册表子键里的 Path 值筛「这是任务还是子文件夹」—— 任务节点没有 Path，
         它只有 Id/Index/SD，于是筛出来 0 个（日志里「任务对象 : 共 0 个」）。
         用 GetTasks(1) 枚举，服务自己知道哪些是任务。

    用法：
      .\Grant-TaskRead.ps1                 # 演练：只说要改什么
      .\Grant-TaskRead.ps1 -Apply          # 真改（需要管理员）
      .\Grant-TaskRead.ps1 -Check          # 只读自检：当前身份能枚举到几个任务
      .\Grant-TaskRead.ps1 -Apply -IncludeTasks              # 连任务对象一起处理
      .\Grant-TaskRead.ps1 -Apply -Folders '\QuietMode\','\QuietHDD\'
#>
[CmdletBinding()]
param(
    [string[]]$Folders = @('\QuietMode\', '\QuietHDD\'),
    [switch]$Apply,
    [switch]$Check,
    [switch]$IncludeTasks
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$DaclOnly     = 4                        # DACL_SECURITY_INFORMATION，只碰 DACL
$UsersReadAce = '(A;OICI;FR;;;BU)'       # Users 只读，含子对象继承
$UsersSid     = 'S-1-5-32-545'           # BUILTIN\Users
$TaskRoot     = Join-Path $env:SystemRoot 'System32\Tasks'

$elevated = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

function ConvertTo-TaskFolderPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $p = $Path.Trim()
    if (-not $p.StartsWith('\')) { $p = '\' + $p }
    # 根是 '\'，不能剥；其余一律剥掉结尾反斜杠
    while ($p.Length -gt 1 -and $p.EndsWith('\')) { $p = $p.Substring(0, $p.Length - 1) }
    return $p
}

function Test-UsersReadAce {
    param([string]$Sddl)
    if ([string]::IsNullOrEmpty($Sddl)) { return $false }
    return [bool]($Sddl -match '\(A;[^()]*;;;BU\)')
}

function Add-DaclAce {
    param([string]$Sddl, [string]$Ace)
    if ([string]::IsNullOrEmpty($Sddl)) { return 'D:' + $Ace }
    if ($Sddl -eq 'D:NO_ACCESS_CONTROL') { return 'D:' + $Ace }
    $m = [regex]::Match($Sddl, '^D:[A-Z]*')
    if (-not $m.Success) { return $null }
    return $m.Value + $Ace + $Sddl.Substring($m.Value.Length)
}

function Grant-TaskFileRead {
    param([string]$FolderPath)

    $stat = [pscustomobject]@{ Total = 0; Added = 0; Failed = 0 }
    $leaf = $FolderPath.TrimStart('\')
    if ([string]::IsNullOrEmpty($leaf)) { return $stat }
    $dir = Join-Path $TaskRoot $leaf
    if (-not (Test-Path -LiteralPath $dir)) { return $stat }

    foreach ($file in @(Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue)) {
        $stat.Total++
        try {
            $fi = New-Object System.IO.FileInfo($file.FullName)
            $sec = $fi.GetAccessControl()
            $has = $false
            foreach ($r in @($sec.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))) {
                if ($r.AccessControlType -eq 'Allow' -and $r.IdentityReference.Value -eq $UsersSid) { $has = $true; break }
            }
            if ($has) { continue }

            $sidObj = New-Object System.Security.Principal.SecurityIdentifier($UsersSid)
            $ace = New-Object System.Security.AccessControl.FileSystemAccessRule(
                $sidObj,
                [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
                [System.Security.AccessControl.InheritanceFlags]::None,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)
            $sec.AddAccessRule($ace) | Out-Null
            $fi.SetAccessControl($sec)
            $stat.Added++
        } catch {
            Write-Host ("      WARN 任务文件补授权失败：" + $file.Name + " -> " + $_.Exception.Message) -ForegroundColor Yellow
            $stat.Failed++
        }
    }
    return $stat
}

# ---------------------------------------------------------------- 任务对象 SD
# 文件夹 ACE 只影响「以后注册的任务」；已注册任务的 SD 是注册那一刻的快照，
# 访问检查用它。改法只有一条：IRegisteredTask::GetSecurityDescriptor /
# SetSecurityDescriptor —— 走服务自己的 API，TaskCache 里那份副本由服务自己更新。
# 不要去手改 HKLM\...\TaskCache\Tree\<路径>\SD：那是个自相对二进制，格式一错
# 任务就整个读不出来，而且改完服务未必重载。
$UsersReadTaskAce = '(A;;FR;;;BU)'

# ---------------------------------------------------------------- 只读自检

if ($Check) {
    Write-Host ''
    Write-Host '计划任务读取自检（只读）' -ForegroundColor White
    Write-Host ("  当前身份 : " + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Write-Host ("  管理员   : " + $elevated)
    Write-Host ''
    $bad = 0
    foreach ($raw in $Folders) {
        $path = ConvertTo-TaskFolderPath $raw
        if (-not $path) { continue }
        Write-Host ("[" + $path + "]")
        $svc = $null
        try {
            $svc = New-Object -ComObject Schedule.Service
            $svc.Connect()
            $folder = $svc.GetFolder($path)
            $tasks = @($folder.GetTasks(1))
            Write-Host ("  OK   枚举到 " + $tasks.Count + " 个任务") -ForegroundColor Green
            foreach ($t in $tasks) {
                $stateName = @('Unknown', 'Disabled', 'Queued', 'Ready', 'Running')[[int]$t.State]
                Write-Host ("       - " + $t.Name + "  [" + $stateName + "]")
            }
        } catch {
            Write-Host ("  FAIL " + $_.Exception.Message) -ForegroundColor Red
            $bad++
        } finally {
            if ($svc) { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { } }
        }
    }
    Write-Host ''
    if ($bad -gt 0) { Write-Host ("结果：" + $bad + " 个文件夹不可读") -ForegroundColor Red; exit 2 }
    exit 0
}

# ---------------------------------------------------------------- 授权 / 演练
Write-Host ''
Write-Host '计划任务文件夹读取权限' -ForegroundColor White
Write-Host ("  管理员 : " + $elevated)
Write-Host ("  模式   : " + $(if ($Apply) { 'Apply' } else { 'DryRun（只报计划）' }))
Write-Host ''

if ($Apply -and -not $elevated) {
    Write-Host '  需要管理员：改任务计划器的安全描述符要提权。' -ForegroundColor Yellow
    Write-Host ''
}

$failed = 0
foreach ($raw in $Folders) {
    $path = ConvertTo-TaskFolderPath $raw
    if (-not $path) { continue }

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
        Write-Host ("       已尝试路径：" + $path) -ForegroundColor DarkGray
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

    if (Test-UsersReadAce $before) {
        Write-Host '  OK   已有 Users 读权限，跳过文件夹' -ForegroundColor Green
        if ($Apply -and $elevated) {
            $r0 = Grant-TaskFileRead -FolderPath $path
            Write-Host ("  任务文件 : 共 " + $r0.Total + " 个，补授权 " + $r0.Added + " 个")
            if ($r0.Failed -gt 0) { $failed++ }
        }
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

    $after = Add-DaclAce -Sddl $before -Ace $UsersReadAce
    if (-not $after) {
        Write-Host '  FAIL 安全描述符不是 D: 开头，拒绝改写' -ForegroundColor Red
        $failed++
        continue
    }
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
    if (-not $check) {
        Write-Host '  FAIL 写入后回读不到安全描述符' -ForegroundColor Red
        $failed++
        continue
    }
    if (-not (Test-UsersReadAce $check)) {
        Write-Host '  FAIL 写入后回读不到 Users ACE' -ForegroundColor Red
        Write-Host ("       现值：" + $check) -ForegroundColor DarkGray
        $failed++
        continue
    }
    # 原有 ACE 一条都不能少（防止 SetSecurityDescriptor 把 DACL 覆盖成只剩新 ACE）
    $lost = 0
    foreach ($m in [regex]::Matches($before, '\([AD];[^()]*\)')) {
        if ($check.IndexOf($m.Value) -lt 0) { $lost++ }
    }
    if ($lost -gt 0) {
        Write-Host ("  FAIL 写回后丢了 " + $lost + " 条原有 ACE") -ForegroundColor Red
        Write-Host ("       现值：" + $check) -ForegroundColor DarkGray
        $failed++
        continue
    }
    Write-Host ("  DONE 已追加 : " + $check) -ForegroundColor Green

    $r = Grant-TaskFileRead -FolderPath $path
    Write-Host ("  任务文件 : 共 " + $r.Total + " 个，补授权 " + $r.Added + " 个")
    if ($r.Failed -gt 0) { $failed++ }

    if ($svc) { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { } }
}

# ---------------------------------------------------------------- 任务对象
if ($IncludeTasks) {
    Write-Host ''
    Write-Host '任务对象读取权限（IRegisteredTask::SecurityDescriptor）' -ForegroundColor White
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDir = Join-Path $env:TEMP ('qm-tasksd-' + $stamp)

    foreach ($raw in $Folders) {
        $path = ConvertTo-TaskFolderPath $raw
        if (-not $path) { continue }
        Write-Host ("[" + $path + "]")

        $svc = $null
        $folder = $null
        try {
            $svc = New-Object -ComObject Schedule.Service
            $svc.Connect()
            $folder = $svc.GetFolder($path)
        } catch {
            Write-Host ("  FAIL 打不开文件夹：" + $_.Exception.Message) -ForegroundColor Red
            $failed++
            continue
        }

        $tasks = @()
        try { $tasks = @($folder.GetTasks(1)) } catch { }
        if ($tasks.Count -eq 0) {
            Write-Host '  （枚举不到任务）' -ForegroundColor DarkGray
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
            continue
        }

        $seen = 0; $done = 0; $bad = 0
        foreach ($t in $tasks) {
            $name = [string]$t.Name
            $seen++

            $before = $null
            try { $before = [string]$t.GetSecurityDescriptor($DaclOnly) }
            catch {
                Write-Host ("  FAIL 读不到 " + $name + " 的安全描述符：" + $_.Exception.Message) -ForegroundColor Red
                $bad++; continue
            }
            if ($before -match '\(A;[^()]*;;;BU\)') {
                Write-Host ("  OK   " + $name + " 已有 Users 读") -ForegroundColor Green
                continue
            }
            if (-not $Apply) {
                Write-Host ("  将追加 : " + $name + "  " + $UsersReadTaskAce + "（不改触发器、不重注册）") -ForegroundColor Yellow
                continue
            }
            if (-not $elevated) {
                Write-Host ("  SKIP " + $name + "（未提权）") -ForegroundColor Yellow
                continue
            }

            $newSddl = Add-DaclAce -Sddl $before -Ace $UsersReadTaskAce
            if (-not $newSddl) {
                Write-Host ("  FAIL " + $name + " 的 SD 不是 D: 开头，拒绝改写") -ForegroundColor Red
                $bad++; continue
            }

            if (-not (Test-Path -LiteralPath $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
            $bak = Join-Path $backupDir (($name -replace '[\\/:*?"<>|]', '_') + '.sddl.txt')
            [IO.File]::WriteAllText($bak, $before + "`r`n", (New-Object System.Text.UTF8Encoding($true)))

            try { $t.SetSecurityDescriptor($newSddl, 0) } catch {
                Write-Host ("  FAIL 写入 " + $name + " 失败：" + $_.Exception.Message) -ForegroundColor Red
                Write-Host ("       原值已备份：" + $bak) -ForegroundColor Yellow
                $bad++; continue
            }

            $check = $null
            try { $check = [string]$t.GetSecurityDescriptor($DaclOnly) } catch { }
            if (-not $check) {
                Write-Host ("  FAIL " + $name + " 写入后回读不到 SD（服务没接受）") -ForegroundColor Red
                Write-Host ("       原值已备份：" + $bak) -ForegroundColor Yellow
                $bad++; continue
            }
            if ($check -notmatch '\(A;[^()]*;;;BU\)') {
                Write-Host ("  FAIL " + $name + " 回读仍没有 Users 读") -ForegroundColor Red
                Write-Host ("       现值：" + $check) -ForegroundColor DarkGray
                $bad++; continue
            }
            $lost = 0
            foreach ($m in [regex]::Matches($before, '\([AD];[^()]*\)')) {
                if ($check.IndexOf($m.Value) -lt 0) { $lost++ }
            }
            if ($lost -gt 0) {
                Write-Host ("  FAIL " + $name + " 写回后丢了 " + $lost + " 条原有 ACE") -ForegroundColor Red
                Write-Host ("       现值：" + $check) -ForegroundColor DarkGray
                $bad++; continue
            }
            Write-Host ("  DONE " + $name) -ForegroundColor Green
            Write-Host ("       现值：" + $check) -ForegroundColor DarkGray
            $done++
        }

        try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
        Write-Host ("  任务对象 : 共 " + $seen + " 个，补授权 " + $done + " 个，失败 " + $bad + " 个")
        if ($bad -gt 0) { $failed++ }
    }
    if ($Apply -and $elevated -and (Test-Path -LiteralPath $backupDir)) {
        Write-Host ("  原值备份 : " + $backupDir) -ForegroundColor DarkGray
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
    if ($IncludeTasks) { Write-Host '      任务对象也已按需补上 Users 读（-IncludeTasks）。' -ForegroundColor Green }
    Write-Host '复验：不要用管理员窗口，直接跑 .\Grant-TaskRead.ps1 -Check' -ForegroundColor DarkGray
}
exit 0
