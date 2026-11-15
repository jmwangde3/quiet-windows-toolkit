#Requires -Version 5.1
<#
    Test-QuietModeAclApply.ps1 — 在**本机真实环境**验证 ACL 授权链路。

    对象是一个一次性的测试任务：\QuietMode-ACL-Test-<GUID>\QuietMode-ACL-Probe
    它 Disabled、没有触发器、动作是 `cmd.exe /c exit 0`、引用不到任何真实资源。
    测试全程不碰生产任务、不碰真实磁盘、不碰 SysMain / FanControl。

    为什么要有这个脚本
    ------------------
    v2.0.5 的兼容性结论里，「PowerShell 7.7.0 下真实 -Mode Apply」一直是
    MANUAL_REQUIRED —— 因为它要写真实任务对象 SDDL、改 System32\Tasks 下的文件
    ACL、禁真实维护任务、把真盘 Offline。日常主机上做这些不合适。
    但 ACL 这一层其实可以拆出来单独验证：写 SDDL 与 Set-Acl 的风险只落在
    「那一两个对象」身上，只要对象是隔离的、可回滚的、事后能证明生产没变，
    就可以在本机做实，而不是留在待办里靠推断。

    所以本脚本只做一件事：拿生产代码（Grant-TaskRead.ps1）去授权一个测试对象，
    然后从三个角度回读证明它做了什么、没做什么，再把对象恢复原样。

    用生产实现，不用复制品
    ----------------------
    真正写 ACL 的动作全部发生在 Grant-TaskRead.ps1 里，本脚本以子进程调用它 ——
    与安装器 [15] 步调用的是同一个脚本、同一批函数。本脚本自己**不写一行授权逻辑**，
    只做四件事：造夹具、读回、比对、恢复。
    如果把授权逻辑抄一份进来，测的就是抄的那份，没有意义。

    五档模式（默认只读）
    -------------------
      -Preflight  只读预检。逐项检查前置条件，任何一项不满足都 exit 3。
      -Apply      真实写入。需要管理员 + PowerShell 7.7.0。
      -Check      只读核对。证明「检查动作本身不改变任何状态」。
      -Rollback   把测试对象恢复到 Apply 之前的状态。
      -Cleanup    注销测试任务、删测试文件夹，并核对生产未被触碰。

    退出码
    ------
      0  本次模式的动作全部通过
      2  有失败项（详见输出与报告）
      3  前置条件不满足，未做任何写操作

    生产保护
    --------
    每一个对任务对象的操作都先过 Assert-TestScoped：
      · 任务文件夹必须以 '\QuietMode-ACL-Test-' 开头
      · 且必须严格等于 '\QuietMode-ACL-Test-<当前 TestId>'
      · 且不得落在 \Microsoft\Windows、\QuietHDD、\QuietMode 之下
      · 任务名必须是 'QuietMode-ACL-Probe'
    任何一条不成立立即 throw，不进入写路径。这条断言不是注释，是运行时代码。

    中断恢复
    --------
    原始状态在写第一笔 ACL 之前就落了盘：
      <恢复根>\<TestId>\state.json     原始 SDDL / 原始文件 ACL / 存在性 / 阶段
      <恢复根>\<TestId>\report.md      本次运行的报告
      <恢复根>\current.json            最后一次测试的 TestId（-Rollback/-Cleanup 用它）
    所以进程被杀、机器重启、人在中途 Ctrl+C，都不会丢掉恢复资料。
    恢复失败时绝不删恢复目录 —— 留着手工复原。
#>
[CmdletBinding()]
param(
    [switch]$Preflight,
    [switch]$Apply,
    [switch]$Check,
    [switch]$Rollback,
    [switch]$Cleanup,

    # 复用某一次测试的 ID（默认新生成；-Rollback / -Cleanup 读 current.json）
    [string]$TestId,

    # 报告输出路径；默认 <恢复根>\<TestId>\report.md
    [string]$ReportPath,

    # 恢复根；默认 $env:ProgramData\QuietMode\AclTestRecovery
    [string]$RecoveryRoot
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# ---------------------------------------------------------------- 常量

$script:TestFolderPrefix = 'QuietMode-ACL-Test-'
$script:TestTaskName     = 'QuietMode-ACL-Probe'
$script:ForbiddenPrefixes = @('\Microsoft\Windows', '\QuietHDD', '\QuietMode')

$DaclOnly = 4                     # DACL_SECURITY_INFORMATION
$DaclFull = 15                    # OWNER|GROUP|DACL|SACL
$UsersSid = 'S-1-5-32-545'

$script:RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:GrantScript = Join-Path $PSScriptRoot 'Grant-TaskRead.ps1'
$script:StateModule = Join-Path $PSScriptRoot 'Modules\QuietMode.TaskState.psm1'
$script:TaskRoot    = Join-Path $env:SystemRoot 'System32\Tasks'

$script:Results  = [ordered]@{}
$script:Failures = New-Object System.Collections.ArrayList
$script:Warnings = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------- 输出

function Write-Head {
    param([string]$Text)
    # 注意：Write-Host ' ' 在 PowerShell 7.7 下不输出空行（空串被优化掉），
    # 两个版本的输出会粘成一行。用单个空格代替，视觉上等价且可靠。
    Write-Host ' '
    Write-Host ('== ' + $Text + ' ' + ('=' * [Math]::Max(0, 58 - $Text.Length))) -ForegroundColor White
}

function Write-Ok   { param([string]$Text) Write-Host ('  OK   ' + $Text) -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host ('  FAIL ' + $Text) -ForegroundColor Red; [void]$script:Failures.Add($Text) }
function Write-Warn2 { param([string]$Text) Write-Host ('  WARN ' + $Text) -ForegroundColor Yellow; [void]$script:Warnings.Add($Text) }
function Write-Note { param([string]$Text) Write-Host ('       ' + $Text) -ForegroundColor DarkGray }
function Write-Step { param([string]$Text) Write-Host ('  ->   ' + $Text) -ForegroundColor Cyan }

# ---------------------------------------------------------------- 模式

$modes = @()
if ($Preflight) { $modes += 'Preflight' }
if ($Apply)     { $modes += 'Apply' }
if ($Check)     { $modes += 'Check' }
if ($Rollback)  { $modes += 'Rollback' }
if ($Cleanup)   { $modes += 'Cleanup' }

if ($modes.Count -gt 1) {
    Write-Host ('一次只能指定一个模式，收到：' + ($modes -join ' / ')) -ForegroundColor Red
    Write-Host '分开跑。默认（不带任何模式开关）等于 -Preflight。' -ForegroundColor Yellow
    exit 3
}
$Mode = 'Preflight'
if ($modes.Count -eq 1) { $Mode = $modes[0] }

# Rollback / Cleanup 允许在非 7.7.0 下跑 —— 它们是恢复通道，
# 把一个已经写坏的 ACL 卡在「必须换 shell 才能修」的状态上是不合理的。
$script:RequirePsExact = ($Mode -eq 'Preflight' -or $Mode -eq 'Apply' -or $Mode -eq 'Check')

# ---------------------------------------------------------------- 身份与环境

$script:PsVersion = [string]$PSVersionTable.PSVersion
# $IsWindows 是 PowerShell 6+ 才有的自动变量（而且只读），5.1 下根本不存在 ——
# 直接拿它判断会让 5.1 认成「不是 Windows」，赋值给 $script:IsWindows 在 7 上
# 又会撞「只读变量」而中止。所以自己用一个名字，值仍从环境变量取。
$script:OnWindows = ($env:OS -eq 'Windows_NT')
if (-not $script:OnWindows) {
    Write-Host '本脚本只能在 Windows 上运行。' -ForegroundColor Red
    exit 3
}

$script:IsAdmin = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $script:IsAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

$script:OsVersion = ''
try {
    $script:OsVersion = [string][Environment]::OSVersion.Version
} catch { }

# ---------------------------------------------------------------- 判据（转发到共享模块）

if (-not (Test-Path -LiteralPath $script:StateModule)) {
    Write-Host ('缺少判据模块：' + $script:StateModule) -ForegroundColor Red
    Write-Host '没有它无法判断「已有权限」与「权限超范围」，拒绝继续。' -ForegroundColor Red
    exit 3
}
Import-Module $script:StateModule -Force -ErrorAction Stop

function Test-UsersReadAce {
    param([string]$Sddl)
    return Test-QmUsersReadAce -Sddl $Sddl
}

function Test-FileHasUsersRead {
    param($Acl)
    return Test-QmFileHasUsersRead -Acl $Acl
}

# ---------------------------------------------------------------- 生产保护

function Get-NormalFolderPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    $p = $Path.Trim()
    if (-not $p.StartsWith('\')) { $p = '\' + $p }
    while ($p.Length -gt 1 -and $p.EndsWith('\')) { $p = $p.Substring(0, $p.Length - 1) }
    return $p
}

function Get-ExpectedFolderPath {
    return ('\' + $script:TestFolderPrefix + $script:TestId)
}

function Assert-TestScoped {
    <#
        任何针对任务对象的操作前都必须过这一关。
        不返回 bool —— 不满足就 throw，避免「调用方忘了看返回值」这种漏网。
    #>
    param([string]$TaskFolder, [string]$TaskName = $script:TestTaskName)

    $norm = Get-NormalFolderPath $TaskFolder
    $problems = @()

    if ([string]::IsNullOrEmpty($script:TestId)) {
        $problems += '当前没有有效的 TestId'
    } elseif ($script:TestId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        # TestId 会被拼进任务文件夹名。-TestId 是命令行参数，人能传任意字符串；
        # 不锁格式的话，'..\QuietMode' 这类值虽然不会造成路径穿越（那是任务计划器
        # 的文件夹名，不是文件路径），但会造出难以审计的对象名。只认标准 GUID。
        $problems += ('TestId 不是合法 GUID：' + $script:TestId)
    }

    $expect = Get-ExpectedFolderPath

    if (-not $norm.StartsWith('\' + $script:TestFolderPrefix)) {
        $problems += ('任务文件夹不在测试命名空间内：' + $TaskFolder)
    }
    if ($norm -ne $expect) {
        $problems += ('实际路径与当前 TestId 不符：期望 ' + $expect + '，实际 ' + $norm)
    }
    foreach ($p in $script:ForbiddenPrefixes) {
        if ($norm -eq $p -or $norm.StartsWith($p + '\')) {
            $problems += ('命中生产保护前缀 ' + $p + '：' + $TaskFolder)
        }
    }
    if ($TaskName -ne $script:TestTaskName) {
        $problems += ('任务名不是测试任务名：' + $TaskName)
    }

    if ($problems.Count -gt 0) {
        throw ('生产保护拦下本次操作：' + ($problems -join '；'))
    }
    return $true
}

# ---------------------------------------------------------------- COM / 任务对象

function Connect-Scheduler {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    return $svc
}

function Close-Scheduler {
    param($Service)
    if ($null -eq $Service) { return }
    try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($Service) } catch { }
}

function Get-TestFolder {
    <# 取测试文件夹对象；不存在返回 $null（不 throw，调用方自己决定） #>
    param($Service)
    $path = Get-ExpectedFolderPath
    [void](Assert-TestScoped -TaskFolder $path)
    try { return $Service.GetFolder($path) } catch { return $null }
}

function Get-TestTask {
    <# 取测试任务对象；不存在返回 $null #>
    param($Service)
    $folder = Get-TestFolder -Service $Service
    if ($null -eq $folder) { return $null }
    try { return $folder.GetTask($script:TestTaskName) } catch { return $null }
}

function Get-SddlWithFlags {
    <#
        读一段安全描述符。默认先试 0xF（OWNER|GROUP|DACL|SACL），
        非提权读不到 SACL 时自动退回 4（只 DACL）。
        返回 @{ Sddl; Flags }，Flags=0 表示两种都读不到。
    #>
    param($Object, [switch]$PreferFull)

    $order = @($DaclOnly)
    if ($PreferFull) { $order = @($DaclFull, $DaclOnly) }

    foreach ($f in $order) {
        try {
            $s = [string]$Object.GetSecurityDescriptor($f)
            if ($s) { return [pscustomobject]@{ Sddl = $s; Flags = $f } }
        } catch { }
    }
    return [pscustomobject]@{ Sddl = ''; Flags = 0 }
}

function Get-TestTaskSddl {
    param($Service, [switch]$PreferFull)
    $t = Get-TestTask -Service $Service
    if ($null -eq $t) { return [pscustomobject]@{ Sddl = ''; Flags = 0; Exists = $false } }
    $r = Get-SddlWithFlags -Object $t -PreferFull:$PreferFull
    return [pscustomobject]@{ Sddl = $r.Sddl; Flags = $r.Flags; Exists = $true }
}

function Get-TestFolderSddl {
    param($Service, [switch]$PreferFull)
    $f = Get-TestFolder -Service $Service
    if ($null -eq $f) { return [pscustomobject]@{ Sddl = ''; Flags = 0; Exists = $false } }
    $r = Get-SddlWithFlags -Object $f -PreferFull:$PreferFull
    return [pscustomobject]@{ Sddl = $r.Sddl; Flags = $r.Flags; Exists = $true }
}

function Get-TestTaskFilePath {
    $leaf = (Get-ExpectedFolderPath).TrimStart('\')
    return (Join-Path (Join-Path $script:TaskRoot $leaf) $script:TestTaskName)
}

function Get-FileAclSddl {
    param([string]$Path)
    if ([string]::IsNullOrEmpty($Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $acl = Get-Acl -LiteralPath $Path
        return [string]$acl.GetSecurityDescriptorSddlForm('Access')
    } catch {
        return $null
    }
}

function Set-FileAclFromSddl {
    <#
        按 SDDL 恢复文件 DACL。只设 Access 段（DACL），不动 owner / SACL ——
        测试改的只有 DACL，恢复也只在 DACL 范围内，不越界。
    #>
    param([string]$Path, [string]$Sddl)
    if ([string]::IsNullOrWhiteSpace($Sddl)) { throw '恢复用的 SDDL 为空' }
    $sec = New-Object System.Security.AccessControl.FileSecurity
    $sec.SetSecurityDescriptorSddlForm($Sddl, 'Access')
    Set-Acl -LiteralPath $Path -AclObject $sec
}

# ---------------------------------------------------------------- 夹具

function New-TestFixture {
    <#
        建一次性测试文件夹 + 测试任务。

        初始 SDDL 显式写成「只有 Administrators 与 SYSTEM 全控」，两条理由：
          · 让夹具从「Users 没有读权限」这个状态开始，Apply 才有事可做；
          · 不依赖服务默认 SDDL 的具体形态 —— 那玩意儿随系统版本变，
            拿它当基线会让测试在别的机器上莫名其妙 AlreadyCompliant。
        建完立刻回读，确认服务确实按我们给的 SDDL 建了对象，不然后面全是空转。
    #>
    param($Service)

    $folderPath = Get-ExpectedFolderPath
    [void](Assert-TestScoped -TaskFolder $folderPath)
    $folderName = $folderPath.TrimStart('\')

    $fixtureSddl = 'D:PAI(A;;FA;;;BA)(A;;FA;;;SY)'

    $root = $Service.GetFolder('\')

    # 已存在就报错：残留说明上一次没清干净，不能踩着重来
    $existing = $null
    try { $existing = $Service.GetFolder($folderPath) } catch { $existing = $null }
    if ($null -ne $existing) {
        throw ('测试文件夹已存在，拒绝在残留上继续：' + $folderPath + '（先跑 -Rollback 或 -Cleanup）')
    }

    Write-Step ('创建测试文件夹 ' + $folderPath)
    $folder = $root.CreateFolder($folderName, $fixtureSddl)

    Write-Step ('注册测试任务 ' + $script:TestTaskName + '（Disabled / 无触发器 / cmd /c exit 0）')
    $def = $Service.NewTask(0)                                  # TASK_VALIDATE_ONLY 关，正常建
    $def.RegistrationInfo.Author = 'QuietMode ACL Host Validation'
    $def.RegistrationInfo.Description = 'QuietMode ACL 真机验收用的一次性任务，测试后注销。'
    $def.Settings.Enabled = $false                              # 必须是 Disabled
    $def.Settings.AllowDemandStart = $false
    $def.Settings.DisallowStartIfOnBatteries = $false
    $def.Settings.StopIfGoingOnBatteries = $false
    $def.Settings.StartWhenAvailable = $false
    $def.Principal.UserId = 'SYSTEM'
    $def.Principal.LogonType = 5                                # TASK_LOGON_SERVICE_ACCOUNT
    $def.Principal.RunLevel = 1                                 # TASK_RUNLEVEL_HIGHEST

    $action = $def.Actions.Create(0)                            # TASK_ACTION_EXEC
    $action.Path = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $action.Arguments = '/c exit 0'
    $action.WorkingDirectory = $env:SystemRoot

    # 6 = TASK_CREATE_OR_UPDATE；第 7 参是初始 SDDL
    $task = $folder.RegisterTaskDefinition($script:TestTaskName, $def, 6, 'SYSTEM', $null, 5, $fixtureSddl)

    # 回读确认夹具真的是「Users 无读权限」状态
    $folderSd = Get-TestFolderSddl -Service $Service
    $taskSd = Get-TestTaskSddl -Service $Service
    if (-not $folderSd.Exists) { throw '夹具校验失败：刚建的测试文件夹读不到' }
    if (-not $taskSd.Exists) { throw '夹具校验失败：刚注册的测试任务读不到' }
    if (Test-UsersReadAce -Sddl $folderSd.Sddl) {
        throw ('夹具校验失败：测试文件夹的 DACL 里已经有 Users 读，无法验证写入（' + $folderSd.Sddl + '）')
    }
    if (Test-UsersReadAce -Sddl $taskSd.Sddl) {
        throw ('夹具校验失败：测试任务的 SD 里已经有 Users 读，无法验证写入（' + $taskSd.Sddl + '）')
    }

    $filePath = Get-TestTaskFilePath
    if (-not (Test-Path -LiteralPath $filePath)) {
        throw ('夹具校验失败：任务文件没有生成：' + $filePath)
    }

    Write-Ok '夹具就绪：文件夹与任务对象都不含 Users 读 ACE'
    Write-Note ('文件夹 SD : ' + $folderSd.Sddl)
    Write-Note ('任务   SD : ' + $taskSd.Sddl)

    return [pscustomobject]@{ Folder = $folder; Task = $task; FilePath = $filePath }
}

# ---------------------------------------------------------------- 恢复目录

function Get-RecoveryBase {
    <#
        恢复资料一律落在完全独立的 ProgramData\QuietMode-AclTestRecovery。

        为什么不放在生产配置目录的 AclTestRecovery 子目录：
        那个目录由安装器创建并做过权限加固（Users 只读），于是会出现
          · 非提权跑 -Preflight 写不进去；
          · 提权跑 -Apply 能写进去；
          · 之后非提权跑 -Rollback 又读不回来。
        恢复通道不能依赖「你现在是不是管理员」。放独立目录，两个身份都能访问，
        也从根上满足「不为跑测试去碰生产配置目录」这一条。
        -RecoveryRoot 仍可显式覆盖。
    #>
    if (-not [string]::IsNullOrWhiteSpace($RecoveryRoot)) { return $RecoveryRoot }
    return (Join-Path $env:ProgramData 'QuietMode-AclTestRecovery')
}

function Get-RecoveryDir {
    param([string]$Id)
    return (Join-Path (Get-RecoveryBase) $Id)
}

function Get-StatePath {
    param([string]$Id)
    return (Join-Path (Get-RecoveryDir $Id) 'state.json')
}

function Get-CurrentPointerPath {
    return (Join-Path (Get-RecoveryBase) 'current.json')
}

function Save-CurrentPointer {
    param([string]$Id)
    $base = Get-RecoveryBase
    if (-not (Test-Path -LiteralPath $base)) { New-Item -ItemType Directory -Path $base -Force | Out-Null }
    $obj = [pscustomobject]@{
        testId     = $Id
        updatedUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        host       = $env:COMPUTERNAME
    }
    $json = $obj | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Get-CurrentPointerPath), $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Resolve-TestId {
    <#
        -TestId 显式给了就用；没给就从 current.json 读；
        再没有就新生成一个（只有 Preflight / Apply 会真的用到它）。
    #>
    param([switch]$AllowNew)

    if (-not [string]::IsNullOrWhiteSpace($TestId)) { return $TestId }

    $ptr = Get-CurrentPointerPath
    if (Test-Path -LiteralPath $ptr) {
        try {
            $o = Get-Content -LiteralPath $ptr -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($o.testId) { return [string]$o.testId }
        } catch { }
    }

    if ($AllowNew) { return ([guid]::NewGuid().ToString()) }
    return $null
}

function Save-State {
    param([object]$State)
    $dir = Get-RecoveryDir $script:TestId
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $State | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Get-StatePath $script:TestId), $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-State {
    $p = Get-StatePath $script:TestId
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# ---------------------------------------------------------------- 生产快照

function Get-ProductionSnapshot {
    <#
        取 \QuietMode 与 \QuietHDD 下每个任务的 SDDL 与每个任务文件的 ACL。
        这份快照只用于「事后再取一次，逐字节比对」，是「生产未被修改」的证据本身。
        任何一处读不到都记进 Unreadable —— 读不全的快照不能当证据，要如实报出来。
    #>
    param($Service)

    $snap = [ordered]@{}
    $unreadable = New-Object System.Collections.ArrayList

    foreach ($path in @('\QuietMode', '\QuietHDD')) {
        $folder = $null
        try { $folder = $Service.GetFolder($path) } catch { $folder = $null }
        if ($null -eq $folder) { continue }

        $tasks = @()
        try { $tasks = @($folder.GetTasks(1)) } catch { [void]$unreadable.Add($path + '（枚举失败）'); continue }

        foreach ($t in $tasks) {
            $full = $path + '\' + [string]$t.Name
            $r = Get-SddlWithFlags -Object $t
            if (-not $r.Sddl) { [void]$unreadable.Add($full); continue }
            $snap[$full] = $r.Sddl

            $fileLeaf = $path.TrimStart('\')
            $filePath = Join-Path (Join-Path $script:TaskRoot $fileLeaf) ([string]$t.Name)
            $aclSddl = Get-FileAclSddl -Path $filePath
            if ($aclSddl) { $snap[$full + ' [file]'] = $aclSddl }
            else { [void]$unreadable.Add($full + ' [file]') }
        }
    }

    return [pscustomobject]@{ Items = $snap; Unreadable = @($unreadable) }
}

# ---------------------------------------------------------------- 调用生产授权实现

function Invoke-GrantScript {
    <#
        以子进程跑 Grant-TaskRead.ps1 —— 就是安装器 [15] 步调用的那一个，
        参数形态也一样（-Folders / -Apply / -IncludeTasks / -TaskNames）。
        子进程是必须的：脚本内部有 exit，直接 & 调用会把本进程一起带走。
    #>
    param([ValidateSet('Apply', 'Check')][string]$GrantMode)

    $folderPath = Get-ExpectedFolderPath
    [void](Assert-TestScoped -TaskFolder $folderPath)

    $psExe = (Get-Process -Id $PID).Path
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:GrantScript,
              '-Folders', $folderPath)
    if ($GrantMode -eq 'Apply') {
        $argv += @('-Apply', '-IncludeTasks', '-TaskNames', $script:TestTaskName)
    } else {
        $argv += @('-Check')
    }

    $raw = & $psExe @argv 2>&1
    $code = $LASTEXITCODE
    $text = ''
    if ($null -ne $raw) { $text = (@($raw) | ForEach-Object { [string]$_ }) -join "`n" }

    return [pscustomobject]@{ Code = $code; Text = $text; Argv = ($argv -join ' ') }
}

# ---------------------------------------------------------------- 权限越界判据

function Test-UsersRightsMinimal {
    <#
        判断 SDDL 里 BUILTIN\Users 的 Allow ACE 是否「只读不写」。

        返回 Over 列表：任何一条越界都说明授权把权限放大了 ——
        ACL 写入最坏的失败形态不是「没加上」，是「加多了」。
        判据按权限位算，不看别名拼写：
          · FR / GR / GRGX / GA 这些含读的别名里，只有 FR 是本项目实际会写的
          · 其余任何含写/删/改权限语义的都算越界
    #>
    param([string]$Sddl)

    $result = [pscustomobject]@{ Count = 0; Rights = @(); Over = @() }
    if ([string]::IsNullOrWhiteSpace($Sddl)) { return $result }

    $rightsSeen = New-Object System.Collections.ArrayList

    foreach ($m in [regex]::Matches($Sddl, '\(A;([^;]*);([^;]*);[^;]*;[^;]*;BU\)')) {
        $rights = $m.Groups[2].Value
        if ([string]::IsNullOrWhiteSpace($rights)) { continue }
        [void]$rightsSeen.Add($rights)

        $bad = $false
        if ($rights.StartsWith('0x')) {
            $v = 0L
            try { $v = [Convert]::ToInt64($rights.Substring(2), 16) } catch { $bad = $true }
            # 0x1 = FILE_READ_DATA；只要这一个读位，其余一律算越界
            if (-not $bad -and $v -ne 0x1) { $bad = $true }
        } else {
            if ($rights -ne 'FR' -and $rights -ne 'GR') { $bad = $true }
        }

        if ($bad) { $result.Over += $rights }
    }

    $result.Count = $rightsSeen.Count
    $result.Rights = @($rightsSeen)
    return $result
}

function Test-FileUsersRightsMinimal {
    <#
        文件侧同一套语义：Users 的 Allow 规则只能是 Read / ReadAndExecute，
        出现 Write / Modify / FullControl / TakeOwnership / ChangePermissions 都算越界。
    #>
    param($Acl)

    $result = [pscustomobject]@{ Count = 0; Rights = @(); Over = @() }
    if ($null -eq $Acl) { return $result }

    $seen = New-Object System.Collections.ArrayList
    $over = New-Object System.Collections.ArrayList

    $readOnlyBits = [int][System.Security.AccessControl.FileSystemRights]::ReadData -bor
                    [int][System.Security.AccessControl.FileSystemRights]::ReadAttributes -bor
                    [int][System.Security.AccessControl.FileSystemRights]::ReadExtendedAttributes -bor
                    [int][System.Security.AccessControl.FileSystemRights]::ReadPermissions -bor
                    [int][System.Security.AccessControl.FileSystemRights]::Synchronize

    foreach ($r in @($Acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        if ($r.IdentityReference.Value -ne $UsersSid) { continue }
        $v = [int]$r.FileSystemRights
        [void]$seen.Add([string]$r.FileSystemRights)
        # 去掉只读位之后还有剩余 → 越界
        if (($v -band (-bnot $readOnlyBits)) -ne 0) { [void]$over.Add([string]$r.FileSystemRights) }
    }

    $result.Count = $seen.Count
    $result.Rights = @($seen)
    $result.Over = @($over)
    return $result
}

function Get-MissingAces {
    <# 原 SDDL 里的每条 AD ACE 在 after 里还必须在 —— 防「覆盖式写入」把旧 ACE 抹掉 #>
    param([string]$Before, [string]$After)
    $lost = New-Object System.Collections.ArrayList
    if ([string]::IsNullOrWhiteSpace($Before)) { return @() }
    foreach ($m in [regex]::Matches($Before, '\([AD];[^()]*\)')) {
        if ([string]::IsNullOrEmpty($After)) { [void]$lost.Add($m.Value); continue }
        if ($After.IndexOf($m.Value) -lt 0) { [void]$lost.Add($m.Value) }
    }
    return @($lost)
}

function Split-DaclSddl {
    <#
        SACL-free SDDL -> 控制标志 + ACE 列表。

        为什么非得拆开：任务对象的 SE_DACL_AUTO_INHERITED 位（SDDL 里的 AI）
        不由 SetSecurityDescriptor 决定 —— 一经 Schedule 服务写入，读回来必然带 AI。
        夹具注册时传的是 D:(...)，所以基线没有 AI；恢复写回同一条串，读回来是 D:AI(...)。
        三条 ACE 一个字符都没变，但拿完整串做全等比较就永远差这一位。

        2026-11-15 本机真机 Apply 第一次跑到 -Rollback 就撞上这个：
            FAIL 任务对象恢复后与原始值不一致：D:AI(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;SY)
        原始值是 D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;SY)，差的只是 AI。
        判据改成「ACE 集合一致 + 控制标志差异单独记录」——
        控制标志不是权限，不影响任何访问判定；ACE 少了才是真失败。
    #>
    param([string]$Sddl)

    $out = [pscustomobject]@{ Flags = ''; Aces = @(); Raw = [string]$Sddl }
    if ([string]::IsNullOrWhiteSpace($Sddl)) { return $out }

    $m = [regex]::Match($Sddl, '^\s*D:([A-Za-z]*)(.*)$')
    if ($m.Success) {
        $out.Flags = [string]$m.Groups[1].Value
        $body = [string]$m.Groups[2].Value
    } else {
        # 少数 API 会直接给裸 ACE 列表，按无前缀处理
        $out.Flags = ''
        $body = $Sddl
    }
    $out.Aces = @([regex]::Matches($body, '\([^()]*\)') | ForEach-Object { $_.Value })
    return $out
}

function Compare-DaclSddl {
    <#
        两个 DACL-only SDDL 的差异，分三个层次：

          SddlEqual — 字符串全等。最严，文件夹与文件仍然是这个判据。
          AcesEqual — 按「ACE 值 + 出现次数」比较，顺序无关。给任务对象用。
          Missing   — before 有、after 不够的 ACE（真丢了权限）
          Extra     — after 有、before 没有的 ACE（多给了权限）

        用计数而不是「包含」：同一张 DACL 里理论上可能出现两条一样的 ACE，
        只判断「存在」会把 2 条变 1 条当成通过。
    #>
    param([string]$Before, [string]$After)

    $b = Split-DaclSddl -Sddl $Before
    $a = Split-DaclSddl -Sddl $After

    $missing = New-Object System.Collections.ArrayList
    foreach ($ace in @($b.Aces | Select-Object -Unique)) {
        $need = @($b.Aces | Where-Object { $_ -eq $ace }).Count
        $have = @($a.Aces | Where-Object { $_ -eq $ace }).Count
        if ($have -lt $need) { [void]$missing.Add($ace) }
    }

    $extra = New-Object System.Collections.ArrayList
    foreach ($ace in @($a.Aces | Select-Object -Unique)) {
        $need = @($a.Aces | Where-Object { $_ -eq $ace }).Count
        $had  = @($b.Aces | Where-Object { $_ -eq $ace }).Count
        if ($had -lt $need) { [void]$extra.Add($ace) }
    }

    return [pscustomobject]@{
        SddlEqual   = ($Before -eq $After)
        AcesEqual   = ($missing.Count -eq 0 -and $extra.Count -eq 0)
        FlagsBefore = $b.Flags
        FlagsAfter  = $a.Flags
        Missing     = @($missing)
        Extra       = @($extra)
        BeforeCount = @($b.Aces).Count
        AfterCount  = @($a.Aces).Count
    }
}

# ---------------------------------------------------------------- 快照三元组

function Get-FixtureSnapshot {
    <#
        一次取齐三个面：任务对象 SD、任务文件夹 SD、任务文件 ACL。

        这里有个容易踩的次序问题：**基线一律存 DACL-only 的串**。
        0xF 读出来的串含 SACL 段（S:...），拿它去 SetSecurityDescriptor 需要
        SeSecurityPrivilege，而恢复阶段未必有 —— 存了个自己写不回去的基线，
        等于没有基线。所以：
          · TaskSddl / FolderSddl  ← GetSecurityDescriptor(4)，比较与恢复都用它
          · TaskSddlFull           ← 额外试 0xF，只作证据记录（读不到就空着）
        测试本来也只改 DACL，恢复同样只在 DACL 范围内，不越界。
    #>
    param($Service)

    $taskObj   = Get-TestTask -Service $Service
    $folderObj = Get-TestFolder -Service $Service

    $taskSddl = ''
    $taskSddlFull = ''
    $taskFlags = 0
    if ($null -ne $taskObj) {
        $r = Get-SddlWithFlags -Object $taskObj
        $taskSddl = $r.Sddl
        $taskFlags = $r.Flags
        $rf = Get-SddlWithFlags -Object $taskObj -PreferFull
        $taskSddlFull = $rf.Sddl
    }

    $folderSddl = ''
    $folderSddlFull = ''
    $folderFlags = 0
    if ($null -ne $folderObj) {
        $r = Get-SddlWithFlags -Object $folderObj
        $folderSddl = $r.Sddl
        $folderFlags = $r.Flags
        $rf = Get-SddlWithFlags -Object $folderObj -PreferFull
        $folderSddlFull = $rf.Sddl
    }

    $filePath = Get-TestTaskFilePath
    $fileSddl = Get-FileAclSddl -Path $filePath
    $fileAcl = $null
    if (Test-Path -LiteralPath $filePath) {
        try { $fileAcl = Get-Acl -LiteralPath $filePath } catch { $fileAcl = $null }
    }

    return [pscustomobject]@{
        TaskExists     = ($null -ne $taskObj)
        TaskSddl       = $taskSddl
        TaskSddlFull   = $taskSddlFull
        TaskFlags      = $taskFlags
        FolderExists   = ($null -ne $folderObj)
        FolderSddl     = $folderSddl
        FolderSddlFull = $folderSddlFull
        FolderFlags    = $folderFlags
        FilePath       = $filePath
        FileExists     = (Test-Path -LiteralPath $filePath)
        FileSddl       = $fileSddl
        FileAcl        = $fileAcl
    }
}

function Show-Snapshot {
    param($Snap, [string]$Label)
    Write-Note ($Label + ' 任务 SD   : ' + $(if ($Snap.TaskSddl) { $Snap.TaskSddl } else { '<读不到>' }))
    Write-Note ($Label + ' 文件夹 SD : ' + $(if ($Snap.FolderSddl) { $Snap.FolderSddl } else { '<读不到>' }))
    Write-Note ($Label + ' 文件 ACL  : ' + $(if ($Snap.FileSddl) { $Snap.FileSddl } else { '<读不到>' }))
    if ($Snap.TaskSddlFull) {
        Write-Note ($Label + ' 任务 SD(0xF) : ' + $Snap.TaskSddlFull + '   ← 含 SACL，仅作证据')
    } else {
        Write-Note ($Label + ' 任务 SD(0xF) : <读不到 SACL，通常是非提权或缺少 SeSecurityPrivilege>')
    }
}

# ---------------------------------------------------------------- 报告

function Save-Report {
    param([string]$Path, [string]$Body)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Body, (New-Object System.Text.UTF8Encoding($false)))
    Write-Note ('报告已写入：' + $Path)
}

function New-ReportBody {
    param([string]$Title, [hashtable]$Extra)

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('# ' + $Title)
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('> 由 `powershell/Test-QuietModeAclApply.ps1` 生成。这是脚本自动记录，')
    [void]$sb.AppendLine('> 与人写的验收报告（docs/ACL_APPLY_HOST_VALIDATION.md）互为佐证。')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| 项 | 值 |')
    [void]$sb.AppendLine('|---|---|')
    [void]$sb.AppendLine('| 生成时间 (UTC) | ' + [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss') + ' |')
    [void]$sb.AppendLine('| 模式 | ' + $Mode + ' |')
    [void]$sb.AppendLine('| TestId | ' + $script:TestId + ' |')
    [void]$sb.AppendLine('| 测试文件夹 | ' + (Get-ExpectedFolderPath) + ' |')
    [void]$sb.AppendLine('| 测试任务 | ' + $script:TestTaskName + ' |')
    [void]$sb.AppendLine('| 任务文件 | ' + (Get-TestTaskFilePath) + ' |')
    [void]$sb.AppendLine('| 操作系统 | ' + $script:OsVersion + ' (' + $env:OS + ') |')
    [void]$sb.AppendLine('| PowerShell | ' + $script:PsVersion + ' (' + $PSVersionTable.PSEdition + ') |')
    [void]$sb.AppendLine('| 管理员 | ' + $script:IsAdmin + ' |')
    [void]$sb.AppendLine('| 计算机 | ' + $env:COMPUTERNAME + ' |')
    [void]$sb.AppendLine('')

    if ($Extra) {
        [void]$sb.AppendLine('## 本次动作')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('| 项 | 值 |')
        [void]$sb.AppendLine('|---|---|')
        foreach ($k in $Extra.Keys) {
            $v = [string]$Extra[$k]
            $v = $v -replace '\|', '\|'
            [void]$sb.AppendLine('| ' + $k + ' | ' + $v + ' |')
        }
        [void]$sb.AppendLine('')
    }

    [void]$sb.AppendLine('## 结果')
    [void]$sb.AppendLine('')
    if ($script:Failures.Count -eq 0) {
        [void]$sb.AppendLine('全部通过（失败 0 项）。')
    } else {
        [void]$sb.AppendLine('失败 ' + $script:Failures.Count + ' 项：')
        [void]$sb.AppendLine('')
        foreach ($f in $script:Failures) { [void]$sb.AppendLine('- ' + ($f -replace "`n", ' ')) }
    }
    [void]$sb.AppendLine('')
    if ($script:Warnings.Count -gt 0) {
        [void]$sb.AppendLine('警告 ' + $script:Warnings.Count + ' 项：')
        [void]$sb.AppendLine('')
        foreach ($w in $script:Warnings) { [void]$sb.AppendLine('- ' + ($w -replace "`n", ' ')) }
        [void]$sb.AppendLine('')
    }

    if ($script:Results.Count -gt 0) {
        [void]$sb.AppendLine('## 明细')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('```')
        foreach ($k in $script:Results.Keys) {
            [void]$sb.AppendLine(('{0} = {1}' -f $k, $script:Results[$k]))
        }
        [void]$sb.AppendLine('```')
        [void]$sb.AppendLine('')
    }

    return $sb.ToString()
}

# ---------------------------------------------------------------- 前置条件

function Test-Preconditions {
    <#
        返回 $true 表示可以进入写路径。任何一项不过都会记 FAIL。
        PowerShell 版本不符时不在第一项就 return —— 一次把问题全列出来更有用。
    #>
    param()

    $ok = $true

    Write-Head '前置条件'

    # 1 Windows
    if ($script:OnWindows) { Write-Ok ('操作系统 Windows：' + $script:OsVersion) }
    else { Write-Bad '不是 Windows'; $ok = $false }

    # 2 PowerShell 7.7.0
    #
    # $PSVersionTable.PSVersion 在 7.x 下是 SemanticVersion，只有 Major/Minor/Patch，
    # **没有 Build 属性** —— 拿 $v.Build 去和 0 比永远不成立，于是这条检查会把
    # 一个 7.7.0 的会话判成「版本不符」。第一版就是那么把自己判红的。
    # 用版本字符串精确比对最稳：预览版（7.7.0-preview1）自然落选，正是想要的结果。
    $is77 = ($script:PsVersion -eq '7.7.0')
    if ($is77) {
        Write-Ok ('PowerShell 7.7.0：' + $script:PsVersion)
    } elseif ($script:RequirePsExact) {
        Write-Bad ('PowerShell 版本不符：需要 7.7.0，实际 ' + $script:PsVersion +
                   '（这条测试要验证的就是 7.7.0 上的行为，换版本跑出来的结论不能用）')
        $ok = $false
    } else {
        Write-Warn2 ('PowerShell ' + $script:PsVersion + '（恢复通道不强制 7.7.0）')
    }

    # 3 管理员
    # 任务书把管理员列为硬前置条件之一，所以未提权一律判 FAIL ——
    # 预检的职责是回答「现在能不能跑」，答案是不能，就该红。
    if ($script:IsAdmin) {
        Write-Ok '当前会话已提权'
    } else {
        Write-Bad '未提权：写任务对象 SDDL 与 System32\Tasks 下的文件 ACL 都需要管理员'
        Write-Note '请在「以管理员身份运行」的 PowerShell 7.7.0 窗口里重跑本命令。'
        $ok = $false
    }

    # 4 授权脚本在位
    if (Test-Path -LiteralPath $script:GrantScript) {
        Write-Ok ('生产授权脚本在位：' + $script:GrantScript)
    } else {
        Write-Bad ('找不到生产授权脚本：' + $script:GrantScript)
        $ok = $false
    }

    # 5 Task Scheduler 服务
    $svcState = ''
    try {
        $s = Get-Service -Name 'Schedule' -ErrorAction Stop
        $svcState = [string]$s.Status
        if ($s.Status -eq 'Running') { Write-Ok 'Task Scheduler 服务运行中' }
        else { Write-Bad ('Task Scheduler 服务状态：' + $svcState); $ok = $false }
    } catch {
        Write-Bad ('读不到 Task Scheduler 服务：' + $_.Exception.Message)
        $ok = $false
    }

    # 6 COM 可用 + 根文件夹可读
    $svc = $null
    try {
        $svc = Connect-Scheduler
        $rootSd = Get-SddlWithFlags -Object $svc.GetFolder('\') -PreferFull
        if ($rootSd.Sddl) {
            Write-Ok ('任务计划器 COM 可用（根文件夹 SD 可读，flags=' + $rootSd.Flags + '）')
            $script:Results['root_sddl_flags'] = $rootSd.Flags
        } else {
            Write-Bad 'COM 连上了但读不到根文件夹安全描述符'
            $ok = $false
        }
    } catch {
        Write-Bad ('COM 连接失败：' + $_.Exception.Message)
        $ok = $false
    }

    # 7 QuietMode 安装状态可识别
    $qmRoot = Join-Path $env:ProgramData 'QuietMode'
    $qmConfig = Join-Path $qmRoot 'config.json'
    $qmInstalled = (Test-Path -LiteralPath $qmConfig)
    if ($qmInstalled) {
        Write-Ok ('识别到 QuietMode 配置：' + $qmConfig + '（只读识别，不写它）')
    } else {
        Write-Warn2 ('没有识别到 ' + $qmConfig + '（本机可能还没装 v2；测试照常进行）')
    }
    $script:Results['quietmode_installed'] = $qmInstalled

    # 8 恢复根可写
    $base = Get-RecoveryBase
    try {
        if (-not (Test-Path -LiteralPath $base)) { New-Item -ItemType Directory -Path $base -Force | Out-Null }
        $probe = Join-Path $base ('.w-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        [IO.File]::WriteAllText($probe, 'probe', (New-Object System.Text.UTF8Encoding($false)))
        Remove-Item -LiteralPath $probe -Force -Confirm:$false
        Write-Ok ('恢复根可写：' + $base)
    } catch {
        Write-Bad ('恢复根不可写：' + $base + ' -> ' + $_.Exception.Message)
        $ok = $false
    }

    # 9 测试命名空间空闲
    if ($svc) {
        $folderPath = Get-ExpectedFolderPath
        $exists = $true
        try { [void]$svc.GetFolder($folderPath); $exists = $true } catch { $exists = $false }
        if ($exists) {
            Write-Bad ('测试文件夹已存在（上次残留）：' + $folderPath + ' —— 先 -Rollback 或 -Cleanup')
            $ok = $false
        } else {
            Write-Ok ('测试命名空间空闲：' + $folderPath)
        }

        $filePath = Get-TestTaskFilePath
        if (Test-Path -LiteralPath $filePath) {
            Write-Bad ('任务文件已存在（上次残留）：' + $filePath)
            $ok = $false
        } else {
            Write-Ok ('任务文件路径未被占用：' + $filePath)
        }
    }

    # 10 生产对象现状
    if ($svc) {
        $snap = Get-ProductionSnapshot -Service $svc
        $script:Results['production_objects'] = $snap.Items.Count
        $script:Results['production_unreadable'] = $snap.Unreadable.Count
        if ($snap.Unreadable.Count -gt 0) {
            Write-Warn2 ('生产对象有 ' + $snap.Unreadable.Count + ' 项读不到：' + ($snap.Unreadable -join ', '))
        }
        Write-Ok ('已记录生产现状：' + $snap.Items.Count + ' 项（用来证明本次没碰它们）')
        foreach ($k in $snap.Items.Keys) { Write-Note ($k + ' = ' + $snap.Items[$k]) }
    }

    if ($svc) { Close-Scheduler $svc }

    return $ok
}

# ================================================================ 模式实现

function Invoke-Preflight {
    Write-Host ' '
    Write-Host 'QuietMode ACL 真机验收 — 预检（只读）' -ForegroundColor White
    Write-Host ('  TestId : ' + $script:TestId)
    Write-Host ('  模式   : Preflight')

    $ok = Test-Preconditions

    Write-Head '预检结论'
    if ($ok) {
        Write-Ok '全部前置条件满足，可以执行 -Apply'
        Write-Note 'Apply 需要管理员 PowerShell 7.7.0。'
        Write-Note ('  pwsh -NoProfile -File ' + $PSCommandPath + ' -Apply')
        return 0
    }
    Write-Bad '前置条件不满足，不允许进入 -Apply'
    return 3
}

function Invoke-Apply {
    Write-Host ' '
    Write-Host 'QuietMode ACL 真机验收 — 真实写入' -ForegroundColor White
    Write-Host ('  TestId : ' + $script:TestId)

    $ok = Test-Preconditions
    if (-not $ok) {
        Write-Head '中止'
        Write-Bad '前置条件不满足，未做任何写操作'
        return 3
    }

    $svc = $null
    $fixture = $null
    $state = $null
    $created = $false

    try {
        Write-Head '生产基线快照'
        $svc = Connect-Scheduler
        $prodBefore = Get-ProductionSnapshot -Service $svc
        Write-Ok ('记录生产对象 ' + $prodBefore.Items.Count + ' 项')

        Write-Head '建立测试夹具'
        $fixture = New-TestFixture -Service $svc
        $created = $true

        Write-Head '保存原始状态'
        $snap0 = Get-FixtureSnapshot -Service $svc
        Show-Snapshot -Snap $snap0 -Label '原始'

        if (-not $snap0.FolderSddl) { throw '原始状态保存失败：读不到测试文件夹 SD' }
        if (-not $snap0.TaskSddl) { throw '原始状态保存失败：读不到测试任务 SD' }
        if (-not $snap0.FileSddl) { throw '原始状态保存失败：读不到任务文件 ACL' }

        $state = [pscustomobject]@{
            testId             = $script:TestId
            createdUtc         = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            host               = $env:COMPUTERNAME
            osVersion          = $script:OsVersion
            psVersion          = $script:PsVersion
            isAdmin            = $script:IsAdmin
            taskFolder         = (Get-ExpectedFolderPath)
            taskName           = $script:TestTaskName
            taskFile           = (Get-TestTaskFilePath)
            folderExistedBefore = $false
            taskExistedBefore   = $false
            fileExistedBefore   = $snap0.FileExists
            folderSddlBefore    = $snap0.FolderSddl
            taskSddlBefore      = $snap0.TaskSddl
            fileAclSddlBefore   = $snap0.FileSddl
            taskSddlFlags       = $snap0.TaskFlags
            folderSddlFlags     = $snap0.FolderFlags
            phase               = 'FixtureCreated'
            productionBefore    = @{}
        }
        foreach ($k in $prodBefore.Items.Keys) { $state.productionBefore[$k] = [string]$prodBefore.Items[$k] }

        Save-State -State $state
        Save-CurrentPointer -Id $script:TestId
        Write-Ok ('原始状态已落盘：' + (Get-StatePath $script:TestId))
        Write-Note '注意：先落盘再写 ACL。中途被杀也不会丢恢复资料。'

        # ---------------------------------------------------------- 第一次 Apply
        Write-Head '执行真实授权（生产实现：Grant-TaskRead.ps1）'
        $run1 = Invoke-GrantScript -GrantMode 'Apply'
        Write-Note ('命令：' + $run1.Argv)
        foreach ($line in ($run1.Text -split "`n")) {
            if ($line.Trim()) { Write-Note ('  | ' + $line.TrimEnd()) }
        }
        $script:Results['apply_exit_1'] = $run1.Code
        if ($run1.Code -ne 0) {
            Write-Bad ('授权脚本退出码 ' + $run1.Code + '（0=成功，2=有授权失败，3=未提权未执行）')
        } else {
            Write-Ok '授权脚本退出码 0'
        }

        # ---------------------------------------------------------- 回读三个面
        Write-Head '回读验证'
        $snap1 = Get-FixtureSnapshot -Service $svc
        Show-Snapshot -Snap $snap1 -Label 'Apply 后'

        $taskReadOk = Test-UsersReadAce -Sddl $snap1.TaskSddl
        $folderReadOk = Test-UsersReadAce -Sddl $snap1.FolderSddl
        $fileReadOk = Test-FileHasUsersRead -Acl $snap1.FileAcl

        if ($snap1.TaskSddlFull) {
            Write-Ok '任务对象安全描述符通过 GetSecurityDescriptor(0xF) 完整读取（含 SACL）'
        } else {
            Write-Warn2 '任务对象只能读到 DACL（0xF 读不到 SACL，通常是缺少 SeSecurityPrivilege）'
        }
        if (-not $snap1.TaskSddl) {
            Write-Bad '任务对象 DACL 完全读不到'
        }

        $script:Results['task_users_read'] = $taskReadOk
        $script:Results['folder_users_read'] = $folderReadOk
        $script:Results['file_users_read'] = $fileReadOk

        if ($taskReadOk) { Write-Ok '任务对象：Users 有读权限（Task Scheduler API 回读）' }
        else { Write-Bad ('任务对象：回读不到 Users 读 ACE —— ' + $snap1.TaskSddl) }

        if ($folderReadOk) { Write-Ok '测试文件夹：Users 有读权限' }
        else { Write-Bad ('测试文件夹：回读不到 Users 读 ACE —— ' + $snap1.FolderSddl) }

        if ($snap1.FileExists) {
            if ($fileReadOk) { Write-Ok '任务文件：Users 有读权限（Get-Acl 回读）' }
            else { Write-Bad ('任务文件：回读不到 Users 读 ACE —— ' + $snap1.FileSddl) }
        } else {
            Write-Bad ('任务文件不见了：' + $snap1.FilePath)
        }

        # ---------------------------------------------------------- 权限不越界
        Write-Head '权限边界（只能读，不能写/删/改）'

        $taskRights = Test-UsersRightsMinimal -Sddl $snap1.TaskSddl
        $script:Results['task_users_ace_count'] = $taskRights.Count
        $script:Results['task_users_rights'] = ($taskRights.Rights -join ',')
        if ($taskRights.Over.Count -gt 0) {
            Write-Bad ('任务对象：Users 拿到越界权限 ' + ($taskRights.Over -join ', '))
        } else {
            Write-Ok ('任务对象：Users 的 ' + $taskRights.Count + ' 条 ACE 全为只读（' + ($taskRights.Rights -join ', ') + '）')
        }

        $fileRights = Test-FileUsersRightsMinimal -Acl $snap1.FileAcl
        $script:Results['file_users_ace_count'] = $fileRights.Count
        $script:Results['file_users_rights'] = ($fileRights.Rights -join ',')
        if ($fileRights.Over.Count -gt 0) {
            Write-Bad ('任务文件：Users 拿到越界权限 ' + ($fileRights.Over -join ', '))
        } else {
            Write-Ok ('任务文件：Users 的 ' + $fileRights.Count + ' 条规则全为只读（' + ($fileRights.Rights -join ', ') + '）')
        }

        # 明确点出「启用/禁用/修改任务」这几项不能被 Users 拿到
        $forbidden = @('FA', 'FW', 'GA', 'WD', 'WO', 'SD')
        $hit = @()
        foreach ($f in $forbidden) {
            if ($taskRights.Rights -contains $f) { $hit += $f }
        }
        if ($hit.Count -gt 0) { Write-Bad ('任务对象：出现写/改权限别名 ' + ($hit -join ', ')) }
        else { Write-Ok '任务对象：没有 FA/FW/GA/WD/WO/SD 任何一个写或改权限别名' }

        # ---------------------------------------------------------- 原有 ACE 保留
        Write-Head '原有 ACE 保留'
        $lostTask = Get-MissingAces -Before $snap0.TaskSddl -After $snap1.TaskSddl
        if ($lostTask.Count -gt 0) {
            Write-Bad ('任务对象写回后丢了 ' + $lostTask.Count + ' 条原有 ACE：' + ($lostTask -join ' '))
        } else {
            Write-Ok '任务对象：原有 ACE 一条不少'
        }

        $lostFolder = Get-MissingAces -Before $snap0.FolderSddl -After $snap1.FolderSddl
        if ($lostFolder.Count -gt 0) {
            Write-Bad ('测试文件夹写回后丢了 ' + $lostFolder.Count + ' 条原有 ACE：' + ($lostFolder -join ' '))
        } else {
            Write-Ok '测试文件夹：原有 ACE 一条不少'
        }

        if ($snap0.FileSddl -and $snap1.FileSddl) {
            $lostFile = Get-MissingAces -Before $snap0.FileSddl -After $snap1.FileSddl
            if ($lostFile.Count -gt 0) {
                Write-Bad ('任务文件写回后丢了 ' + $lostFile.Count + ' 条原有 ACE：' + ($lostFile -join ' '))
            } else {
                Write-Ok '任务文件：原有 ACE 一条不少'
            }
        }

        # ---------------------------------------------------------- 幂等
        Write-Head '幂等性（第二次相同 Apply）'
        $run2 = Invoke-GrantScript -GrantMode 'Apply'
        $script:Results['apply_exit_2'] = $run2.Code
        if ($run2.Code -ne 0) {
            Write-Bad ('第二次 Apply 退出码 ' + $run2.Code)
        } else {
            Write-Ok '第二次 Apply 退出码 0'
        }
        if ($run2.Text -match '已有 Users 读') {
            Write-Ok '第二次 Apply 明确报告「已有 Users 读」，走的是 Skipped 分支'
        } else {
            Write-Warn2 '第二次 Apply 输出里没有「已有 Users 读」字样，人工确认一下'
        }

        $snap2 = Get-FixtureSnapshot -Service $svc
        if ($snap2.TaskSddl -eq $snap1.TaskSddl) { Write-Ok '任务对象 SD 两次一致' }
        else { Write-Bad ('任务对象 SD 变了：' + $snap1.TaskSddl + ' -> ' + $snap2.TaskSddl) }

        if ($snap2.FolderSddl -eq $snap1.FolderSddl) { Write-Ok '文件夹 SD 两次一致' }
        else { Write-Bad ('文件夹 SD 变了：' + $snap1.FolderSddl + ' -> ' + $snap2.FolderSddl) }

        if ($snap2.FileSddl -eq $snap1.FileSddl) { Write-Ok '任务文件 ACL 两次一致' }
        else { Write-Bad ('任务文件 ACL 变了：' + $snap1.FileSddl + ' -> ' + $snap2.FileSddl) }

        $dup = ([regex]::Matches($snap2.TaskSddl, '\(A;[^()]*;;;BU\)')).Count
        Write-Note ('任务对象里 Users Allow ACE 条数：' + $dup + '（重复执行不应增加）')

        # ---------------------------------------------------------- Check 只读
        Write-Head 'Check 只读验证（检查动作本身不许改状态）'
        $c0 = Get-FixtureSnapshot -Service $svc
        $runC = Invoke-GrantScript -GrantMode 'Check'
        $script:Results['check_exit'] = $runC.Code
        $c1 = Get-FixtureSnapshot -Service $svc

        if ($c0.TaskSddl -eq $c1.TaskSddl) { Write-Ok 'Check 前后任务对象 SD 一致' }
        else { Write-Bad 'Check 改了任务对象 SD' }
        if ($c0.FolderSddl -eq $c1.FolderSddl) { Write-Ok 'Check 前后文件夹 SD 一致' }
        else { Write-Bad 'Check 改了文件夹 SD' }
        if ($c0.FileSddl -eq $c1.FileSddl) { Write-Ok 'Check 前后任务文件 ACL 一致' }
        else { Write-Bad 'Check 改了任务文件 ACL' }

        # ---------------------------------------------------------- 生产未变
        Write-Head '生产对象未修改'
        $prodAfter = Get-ProductionSnapshot -Service $svc
        $prodSame = $true
        foreach ($k in $prodBefore.Items.Keys) {
            if (-not $prodAfter.Items.Contains($k)) { Write-Bad ('生产对象消失：' + $k); $prodSame = $false; continue }
            if ([string]$prodBefore.Items[$k] -ne [string]$prodAfter.Items[$k]) {
                Write-Bad ('生产对象被改动：' + $k + "`n        before: " + $prodBefore.Items[$k] + "`n        after : " + $prodAfter.Items[$k])
                $prodSame = $false
            }
        }
        if ($prodSame) { Write-Ok ('生产对象 ' + $prodBefore.Items.Count + ' 项逐个一致') }
        $script:Results['production_unchanged'] = $prodSame

        # ---------------------------------------------------------- 落盘
        $state.phase = 'Applied'
        $state | Add-Member -NotePropertyName taskSddlAfterApply -NotePropertyValue $snap1.TaskSddl -Force
        $state | Add-Member -NotePropertyName fileAclSddlAfterApply -NotePropertyValue $snap1.FileSddl -Force
        $state | Add-Member -NotePropertyName folderSddlAfterApply -NotePropertyValue $snap1.FolderSddl -Force
        Save-State -State $state

    } catch {
        Write-Bad ('Apply 过程异常：' + $_.Exception.Message)
        Write-Note ('脚本位置：' + $_.InvocationInfo.PositionMessage)
        if ($created) {
            Write-Warn2 '测试夹具已经建立，可能含已写入的 ACL。'
            Write-Note ('恢复：pwsh -NoProfile -File ' + $PSCommandPath + ' -Rollback -TestId ' + $script:TestId)
            Write-Note ('清理：pwsh -NoProfile -File ' + $PSCommandPath + ' -Cleanup -TestId ' + $script:TestId)
        }
    } finally {
        if ($svc) { Close-Scheduler $svc }
    }

    Write-Head 'Apply 结论'
    $code = 0
    if ($script:Failures.Count -gt 0) {
        Write-Bad ('失败 ' + $script:Failures.Count + ' 项，见上')
        $code = 2
    } else {
        Write-Ok '全部检查通过'
    }
    Write-Note '测试对象默认保留，方便再跑 -Check / -Rollback。'
    Write-Note ('  pwsh -NoProfile -File ' + $PSCommandPath + ' -Check')
    Write-Note ('  pwsh -NoProfile -File ' + $PSCommandPath + ' -Rollback')
    return $code
}

function Invoke-Check {
    Write-Host ' '
    Write-Host 'QuietMode ACL 真机验收 — 只读核对' -ForegroundColor White
    Write-Host ('  TestId : ' + $script:TestId)

    $svc = $null
    try {
        $svc = Connect-Scheduler
        $folder = Get-TestFolder -Service $svc
        if ($null -eq $folder) {
            Write-Bad ('测试文件夹不存在：' + (Get-ExpectedFolderPath) + '（先 -Apply，或这不是当前 TestId）')
            return 2
        }
        $task = Get-TestTask -Service $svc
        if ($null -eq $task) {
            Write-Bad ('测试任务不存在：' + $script:TestTaskName)
            return 2
        }

        Write-Head '核对前快照'
        $before = Get-FixtureSnapshot -Service $svc
        Show-Snapshot -Snap $before -Label 'Check 前'

        Write-Head '调用生产只读路径'
        $run = Invoke-GrantScript -GrantMode 'Check'
        Write-Note ('命令：' + $run.Argv)
        foreach ($line in ($run.Text -split "`n")) {
            if ($line.Trim()) { Write-Note ('  | ' + $line.TrimEnd()) }
        }
        $script:Results['check_exit'] = $run.Code
        if ($run.Code -eq 0 -or $run.Code -eq 2) { Write-Ok ('只读自检退出码 ' + $run.Code + '（0=可读，2=有文件夹不可读）') }
        else { Write-Bad ('只读自检退出码 ' + $run.Code) }

        Write-Head '核对后快照'
        $after = Get-FixtureSnapshot -Service $svc
        Show-Snapshot -Snap $after -Label 'Check 后'

        Write-Head '状态未改变'
        $same = $true
        if ($before.TaskSddl -ne $after.TaskSddl) { Write-Bad '任务对象 SD 变了'; $same = $false }
        else { Write-Ok '任务对象 SD 一致' }
        if ($before.FolderSddl -ne $after.FolderSddl) { Write-Bad '文件夹 SD 变了'; $same = $false }
        else { Write-Ok '文件夹 SD 一致' }
        if ($before.FileSddl -ne $after.FileSddl) { Write-Bad '任务文件 ACL 变了'; $same = $false }
        else { Write-Ok '任务文件 ACL 一致' }
        $script:Results['check_readonly'] = $same

        Write-Head '当前授权状态'
        $tr = Test-UsersReadAce -Sddl $after.TaskSddl
        $fr = Test-UsersReadAce -Sddl $after.FolderSddl
        $fl = Test-FileHasUsersRead -Acl $after.FileAcl
        if ($tr) { Write-Ok '任务对象：有 Users 读' } else { Write-Warn2 '任务对象：没有 Users 读' }
        if ($fr) { Write-Ok '测试文件夹：有 Users 读' } else { Write-Warn2 '测试文件夹：没有 Users 读' }
        if ($fl) { Write-Ok '任务文件：有 Users 读' } else { Write-Warn2 '任务文件：没有 Users 读' }
        $script:Results['task_users_read'] = $tr
        $script:Results['folder_users_read'] = $fr
        $script:Results['file_users_read'] = $fl

    } catch {
        Write-Bad ('核对过程异常：' + $_.Exception.Message)
    } finally {
        if ($svc) { Close-Scheduler $svc }
    }

    Write-Head 'Check 结论'
    if ($script:Failures.Count -gt 0) { Write-Bad ('失败 ' + $script:Failures.Count + ' 项'); return 2 }
    Write-Ok '只读核对完成，全程未修改任何状态'
    return 0
}

function Invoke-Rollback {
    Write-Host ' '
    Write-Host 'QuietMode ACL 真机验收 — 回滚' -ForegroundColor White
    Write-Host ('  TestId : ' + $script:TestId)

    if ([string]::IsNullOrEmpty($script:TestId)) {
        Write-Bad '找不到任何测试记录（current.json 为空且没给 -TestId）'
        return 3
    }

    $state = Read-State
    if ($null -eq $state) {
        Write-Bad ('恢复资料不存在：' + (Get-StatePath $script:TestId))
        Write-Note '没有原始 SDDL 就无法安全恢复，拒绝在猜的基础上删东西。'
        return 3
    }

    Write-Ok ('读到恢复资料，记录阶段：' + $state.phase)
    Write-Note ('原始文件夹 SD : ' + $state.folderSddlBefore)
    Write-Note ('原始任务   SD : ' + $state.taskSddlBefore)
    Write-Note ('原始文件 ACL  : ' + $state.fileAclSddlBefore)

    if (-not $script:IsAdmin) {
        Write-Bad '未提权：写 SDDL 与 Set-Acl 都需要管理员，本次没有做任何修改'
        return 3
    }

    $svc = $null
    try {
        $svc = Connect-Scheduler

        $folder = Get-TestFolder -Service $svc
        $task = Get-TestTask -Service $svc
        $filePath = [string]$state.taskFile
        if ([string]::IsNullOrEmpty($filePath)) { $filePath = Get-TestTaskFilePath }

        # ---------------- 任务对象
        Write-Head '恢复任务对象 SDDL'
        if ($null -eq $task) {
            Write-Warn2 '测试任务不存在（可能已被注销），跳过任务对象恢复'
            $script:Results['rollback_task'] = 'absent'
        } else {
            $cur = Get-SddlWithFlags -Object $task
            Write-Note ('当前 : ' + $cur.Sddl)
            $cmpPre = Compare-DaclSddl -Before ([string]$state.taskSddlBefore) -After ([string]$cur.Sddl)
            if ($cmpPre.AcesEqual) {
                # ACE 已与基线逐条一致：可能是压根没改过，也可能是上次恢复到一半只差 AI 位。
                Write-Ok '任务对象 ACE 已与原始值一致，无需改写'
                if (-not $cmpPre.SddlEqual) {
                    Write-Warn2 ('控制标志：D:' + $cmpPre.FlagsBefore + ' -> D:' + $cmpPre.FlagsAfter + '（SE_DACL_AUTO_INHERITED，不影响任何 ACE）')
                }
                $script:Results['rollback_task'] = 'already'
            } else {
                try {
                    $task.SetSecurityDescriptor([string]$state.taskSddlBefore, 0)
                    # SetSecurityDescriptor 之后原对象作废，必须重新 GetTask 再回读
                    $last = ''
                    for ($i = 0; $i -lt 8; $i++) {
                        Start-Sleep -Milliseconds 150
                        try {
                            $t2 = $folder.GetTask($script:TestTaskName)
                            $last = [string]$t2.GetSecurityDescriptor($DaclOnly)
                            if ($last) { break }
                        } catch { }
                    }
                    if (-not $last) {
                        Write-Bad '任务对象写入后回读不到安全描述符'
                        $script:Results['rollback_task'] = 'readback-empty'
                    } else {
                        $cmp = Compare-DaclSddl -Before ([string]$state.taskSddlBefore) -After $last
                        if ($cmp.AcesEqual) {
                            Write-Ok ('任务对象已恢复：' + $cmp.AfterCount + ' 条 ACE 与原始值逐条一致')
                            if (-not $cmp.SddlEqual) {
                                Write-Warn2 ('控制标志：D:' + $cmp.FlagsBefore + ' -> D:' + $cmp.FlagsAfter + '（SE_DACL_AUTO_INHERITED，不影响任何 ACE）')
                            }
                            $script:Results['rollback_task'] = 'restored'
                        } else {
                            Write-Bad ('任务对象恢复后 ACE 与原始值不一致：' + $last)
                            if ($cmp.Missing.Count -gt 0) { Write-Note ('缺失 ACE：' + ($cmp.Missing -join ' ')) }
                            if ($cmp.Extra.Count -gt 0) { Write-Note ('多出 ACE：' + ($cmp.Extra -join ' ')) }
                            $script:Results['rollback_task'] = 'mismatch'
                        }
                    }
                } catch {
                    Write-Bad ('任务对象恢复失败：' + $_.Exception.Message)
                    $script:Results['rollback_task'] = 'error'
                }
            }
            # 原有 ACE 必须一条不少
            if ($script:Results['rollback_task'] -in @('restored', 'already')) {
                $now = (Get-SddlWithFlags -Object (Get-TestTask -Service $svc)).Sddl
                $lost = Get-MissingAces -Before ([string]$state.taskSddlBefore) -After $now
                if ($lost.Count -gt 0) { Write-Bad ('恢复后仍缺 ' + $lost.Count + ' 条原始 ACE：' + ($lost -join ' ')) }
                else { Write-Ok '恢复后原始 ACE 逐条在位' }
                # 新增的 Users ACE 必须已消失（原始里没有的话）
                if (-not (Test-UsersReadAce -Sddl ([string]$state.taskSddlBefore))) {
                    if (Test-UsersReadAce -Sddl $now) { Write-Bad '恢复后仍带着新增的 Users 读 ACE' }
                    else { Write-Ok '恢复后新增的 Users 读 ACE 已按原始状态清除' }
                }
            }
        }

        # ---------------- 任务文件
        Write-Head '恢复任务文件 ACL'
        if (-not (Test-Path -LiteralPath $filePath)) {
            Write-Warn2 ('任务文件不存在，跳过：' + $filePath)
            $script:Results['rollback_file'] = 'absent'
        } elseif ([string]::IsNullOrWhiteSpace([string]$state.fileAclSddlBefore)) {
            Write-Warn2 '恢复资料里没有原始文件 ACL，跳过'
            $script:Results['rollback_file'] = 'no-baseline'
        } else {
            $curFile = Get-FileAclSddl -Path $filePath
            Write-Note ('当前 : ' + $curFile)
            if ($curFile -eq $state.fileAclSddlBefore) {
                Write-Ok '文件 ACL 已是原始状态，无需改写'
                $script:Results['rollback_file'] = 'already'
            } else {
                try {
                    Set-FileAclFromSddl -Path $filePath -Sddl ([string]$state.fileAclSddlBefore)
                    $after = Get-FileAclSddl -Path $filePath
                    if ($after -eq $state.fileAclSddlBefore) {
                        Write-Ok '文件 ACL 已恢复并回读一致'
                        $script:Results['rollback_file'] = 'restored'
                    } else {
                        Write-Bad ('文件 ACL 恢复后不一致：' + $after)
                        $script:Results['rollback_file'] = 'mismatch'
                    }
                } catch {
                    Write-Bad ('文件 ACL 恢复失败：' + $_.Exception.Message)
                    $script:Results['rollback_file'] = 'error'
                }
            }
        }

        # ---------------- 任务文件夹
        Write-Head '恢复测试文件夹 SDDL'
        if ($null -eq $folder) {
            Write-Warn2 '测试文件夹不存在，跳过'
            $script:Results['rollback_folder'] = 'absent'
        } elseif ([string]::IsNullOrWhiteSpace([string]$state.folderSddlBefore)) {
            Write-Warn2 '恢复资料里没有原始文件夹 SDDL，跳过'
            $script:Results['rollback_folder'] = 'no-baseline'
        } else {
            $curF = Get-SddlWithFlags -Object $folder
            Write-Note ('当前 : ' + $curF.Sddl)
            if ($curF.Sddl -eq $state.folderSddlBefore) {
                Write-Ok '文件夹 SD 已是原始状态'
                $script:Results['rollback_folder'] = 'already'
            } else {
                try {
                    $folder.SetSecurityDescriptor([string]$state.folderSddlBefore, 0)
                    $lastF = ''
                    for ($i = 0; $i -lt 8; $i++) {
                        Start-Sleep -Milliseconds 150
                        try {
                            $f2 = $svc.GetFolder((Get-ExpectedFolderPath))
                            $lastF = [string]$f2.GetSecurityDescriptor($DaclOnly)
                            if ($lastF) { break }
                        } catch { }
                    }
                    if ($lastF -eq $state.folderSddlBefore) {
                        Write-Ok '文件夹 SD 已恢复并回读一致'
                        $script:Results['rollback_folder'] = 'restored'
                    } elseif (-not $lastF) {
                        Write-Bad '文件夹写入后回读不到 SD'
                        $script:Results['rollback_folder'] = 'readback-empty'
                    } else {
                        Write-Bad ('文件夹恢复后不一致：' + $lastF)
                        $script:Results['rollback_folder'] = 'mismatch'
                    }
                } catch {
                    Write-Bad ('文件夹恢复失败：' + $_.Exception.Message)
                    $script:Results['rollback_folder'] = 'error'
                }
            }
        }

    } catch {
        Write-Bad ('回滚过程异常：' + $_.Exception.Message)
    } finally {
        if ($svc) { Close-Scheduler $svc }
    }

    Write-Head 'Rollback 结论'
    if ($script:Failures.Count -gt 0) {
        Write-Bad ('失败 ' + $script:Failures.Count + ' 项 —— 恢复资料保留，未删除')
        Write-Note ('恢复资料：' + (Get-RecoveryDir $script:TestId))
        Write-Note '按上面的原始 SDDL 可以手工复原；处理完再重跑 -Rollback。'
        return 2
    }
    Write-Ok '测试对象已恢复到 Apply 之前的状态'
    if ($null -ne $state) {
        try {
            $state.phase = 'Restored'
            $state | Add-Member -NotePropertyName restoredUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')) -Force
            Save-State -State $state
        } catch { }
    }
    Write-Note ('下一步：pwsh -NoProfile -File ' + $PSCommandPath + ' -Cleanup')
    return 0
}

function Invoke-Cleanup {
    Write-Host ' '
    Write-Host 'QuietMode ACL 真机验收 — 清理' -ForegroundColor White
    Write-Host ('  TestId : ' + $script:TestId)

    if ([string]::IsNullOrEmpty($script:TestId)) {
        Write-Bad '找不到任何测试记录'
        return 3
    }

    $state = Read-State
    if ($null -eq $state) {
        Write-Bad ('恢复资料不存在：' + (Get-StatePath $script:TestId))
        Write-Note '不知道原始状态就不删对象 —— 先把 -Rollback 的证据找回来。'
        return 3
    }

    if ([string]$state.phase -ne 'Restored') {
        Write-Bad ('当前阶段是 ' + $state.phase + '，不是 Restored')
        Write-Note '先跑 -Rollback 把对象恢复原状，再清理。硬删会把「改过 ACL 的残留」变成「消失了」，'
        Write-Note '两条都不好查；按顺序走才有可审计的轨迹。'
        return 2
    }

    if (-not $script:IsAdmin) {
        Write-Bad '未提权：注销任务与删文件夹都需要管理员，本次没有做任何修改'
        return 3
    }

    $svc = $null
    try {
        $svc = Connect-Scheduler
        $prodBefore = Get-ProductionSnapshot -Service $svc

        $folderPath = Get-ExpectedFolderPath
        [void](Assert-TestScoped -TaskFolder $folderPath)
        $folderName = $folderPath.TrimStart('\')

        $folder = Get-TestFolder -Service $svc

        # 删之前再做一次恢复态断言：不信任 phase 字段，现场再看一眼
        if ($null -ne $folder) {
            $task = Get-TestTask -Service $svc
            if ($null -ne $task) {
                $sd = (Get-SddlWithFlags -Object $task).Sddl
                $cmpClean = Compare-DaclSddl -Before ([string]$state.taskSddlBefore) -After ([string]$sd)
                if (-not $cmpClean.AcesEqual) {
                    Write-Warn2 ('任务对象 ACE 与记录的原始值不一致（缺 ' + $cmpClean.Missing.Count + ' 条 / 多 ' + $cmpClean.Extra.Count + ' 条），但阶段是 Restored —— 继续删除（对象本身是测试对象）')
                }
            }
        }

        Write-Head '注销测试任务'
        if ($null -eq $folder) {
            Write-Warn2 '测试文件夹已不存在'
        } else {
            try {
                $folder.DeleteTask($script:TestTaskName, 0)
                Write-Ok ('已注销 ' + $script:TestTaskName)
            } catch {
                Write-Warn2 ('注销任务时报错（可能本来就不存在）：' + $_.Exception.Message)
            }

            Write-Head '删除测试文件夹'
            try {
                $svc.GetFolder('\').DeleteFolder($folderName, 0)
                Write-Ok ('已删除 ' + $folderPath)
            } catch {
                Write-Bad ('删除文件夹失败：' + $_.Exception.Message)
            }
        }

        Write-Head '验证测试对象已消失'
        $still = $true
        try { [void]$svc.GetFolder($folderPath) } catch { $still = $false }
        if ($still) { Write-Bad ('测试文件夹仍然存在：' + $folderPath) }
        else { Write-Ok '测试文件夹不存在' }

        $filePath = Get-TestTaskFilePath
        if (Test-Path -LiteralPath $filePath) {
            Write-Bad ('任务文件仍然存在：' + $filePath)
        } else {
            Write-Ok '任务文件不存在'
        }
        # 文件夹本身在 System32\Tasks 下的目录也该没了
        $leafDir = Join-Path $script:TaskRoot $folderName
        if (Test-Path -LiteralPath $leafDir) {
            Write-Warn2 ('System32\Tasks 下仍有目录（服务通常会自动清，留着也算残留）：' + $leafDir)
        } else {
            Write-Ok 'System32\Tasks 下无残留目录'
        }

        Write-Head '生产对象未修改'
        $prodAfter = Get-ProductionSnapshot -Service $svc
        $same = $true
        foreach ($k in $prodBefore.Items.Keys) {
            if (-not $prodAfter.Items.Contains($k)) { Write-Bad ('生产对象消失：' + $k); $same = $false; continue }
            if ([string]$prodBefore.Items[$k] -ne [string]$prodAfter.Items[$k]) {
                Write-Bad ('生产对象被改动：' + $k)
                $same = $false
            }
        }
        if ($same) { Write-Ok ('生产对象 ' + $prodBefore.Items.Count + ' 项逐个一致') }
        $script:Results['production_unchanged'] = $same

    } catch {
        Write-Bad ('清理过程异常：' + $_.Exception.Message)
    } finally {
        if ($svc) { Close-Scheduler $svc }
    }

    Write-Head 'Cleanup 结论'
    if ($script:Failures.Count -gt 0) {
        Write-Bad ('失败 ' + $script:Failures.Count + ' 项；恢复资料保留在 ' + (Get-RecoveryDir $script:TestId))
        return 2
    }

    # 归档报告后删掉本次恢复目录（保留历史留档）
    try {
        $dir = Get-RecoveryDir $script:TestId
        $hist = Join-Path (Get-RecoveryBase) 'history'
        if (-not (Test-Path -LiteralPath $hist)) { New-Item -ItemType Directory -Path $hist -Force | Out-Null }
        foreach ($n in @('state.json', 'report.md')) {
            $src = Join-Path $dir $n
            if (Test-Path -LiteralPath $src) {
                Copy-Item -LiteralPath $src -Destination (Join-Path $hist ($script:TestId + '-' + $n)) -Force
            }
        }
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -Confirm:$false }
        Write-Ok ('恢复资料已归档到 ' + $hist + ' 并清掉本次目录')
    } catch {
        Write-Warn2 ('归档/清理恢复目录时出错（不影响生产）：' + $_.Exception.Message)
    }

    Write-Ok '清理完成'
    return 0
}

# ================================================================ 主流程

New-Item -ItemType Directory -Path (Get-RecoveryBase) -Force -ErrorAction SilentlyContinue | Out-Null

# Preflight 与 Apply 必须指向同一个测试对象，所以两个模式都落 current.json：
#   · 第一次 -Preflight 生成 ID 并记录；
#   · 之后 -Preflight / -Apply 不带 -TestId 时都沿用它；
#   · 上次测试还没清干净时，Preflight 会因为「命名空间被占用」判红 —— 正是想要的。
if ($Mode -eq 'Preflight' -or $Mode -eq 'Apply') {
    $script:TestId = Resolve-TestId -AllowNew
} else {
    $script:TestId = Resolve-TestId
}

if (-not $script:TestId) {
    Write-Host '无法确定 TestId：current.json 里没有记录，也没有显式给 -TestId。' -ForegroundColor Red
    Write-Host '先跑一次 -Preflight —— 它会生成并记录一个测试 ID。' -ForegroundColor Yellow
    exit 3
}

# 先校验再落盘：非法 TestId 不该被写进 current.json，否则后面每个模式都会带着它跑。
if ($script:TestId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
    Write-Host ('TestId 不是合法 GUID：' + $script:TestId) -ForegroundColor Red
    Write-Host '它会被拼进任务文件夹名，只接受标准 GUID（8-4-4-4-12，含连字符）。' -ForegroundColor Yellow
    exit 3
}

if ($Mode -eq 'Preflight' -or $Mode -eq 'Apply') { Save-CurrentPointer -Id $script:TestId }

Write-Host ' '
Write-Host 'QuietMode ACL Apply — 本机隔离验收' -ForegroundColor White
Write-Host ('  PowerShell : ' + $script:PsVersion + ' (' + $PSVersionTable.PSEdition + ')')
Write-Host ('  管理员     : ' + $script:IsAdmin)
Write-Host ('  模式       : ' + $Mode)
Write-Host ('  TestId     : ' + $(if ($script:TestId) { $script:TestId } else { '<无>' }))
Write-Host ('  测试对象   : ' + $(if ($script:TestId) { (Get-ExpectedFolderPath) + '\' + $script:TestTaskName } else { '<无>' }))
Write-Host ('  恢复根     : ' + (Get-RecoveryBase))

$code = 2
switch ($Mode) {
    'Preflight' { $code = Invoke-Preflight }
    'Apply'     { $code = Invoke-Apply }
    'Check'     { $code = Invoke-Check }
    'Rollback'  { $code = Invoke-Rollback }
    'Cleanup'   { $code = Invoke-Cleanup }
    default     { $code = 3 }
}

# 报告：Rollback / Cleanup 也写，构成完整轨迹
$title = 'QuietMode ACL Apply 验收记录 — ' + $Mode
$extra = @{
    '退出码' = [string]$code
    '失败项' = [string]$script:Failures.Count
    '警告项' = [string]$script:Warnings.Count
}
$body = New-ReportBody -Title $title -Extra $extra

$reportOut = $ReportPath
if ([string]::IsNullOrWhiteSpace($reportOut)) {
    if ($script:TestId) { $reportOut = Join-Path (Get-RecoveryDir $script:TestId) 'report.md' }
    else { $reportOut = Join-Path (Get-RecoveryBase) ('report-' + $Mode + '.md') }
}
try { Save-Report -Path $reportOut -Body $body } catch { Write-Warn2 ('写报告失败：' + $_.Exception.Message) }

exit $code
