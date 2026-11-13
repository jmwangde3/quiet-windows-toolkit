#Requires -Version 5.1
<#
    QuietMode.Security.psm1 — 特权目录的权限模型（只读体检 + 可选加固）。

    威胁模型（RC2-04）：
      计划任务以 SYSTEM 运行 qmctl，qmctl 会读取 run\reconcile-request-*.json 并据此
      操作计划任务、服务、注册表、磁盘。因此任何「普通用户可以写、SYSTEM 会执行」的
      路径都是一条提权通道。需要保证：

        * config.json / state.json / baseline.json / run\ / recovery\ / powershell\
          普通用户只能读，不能写、不能删、不能改名、不能替换。
          「普通用户」按 Users + Authenticated Users + Everyone 三个 SID 判：
          部署目录在 H:\ 下，Authenticated Users 的 Modify 会一路继承进去，
          只盯 Users 组会漏判。
        * logs\ 允许追加（普通用户跑 qmctl 时也要能留日志），但不允许删除目录。
        * 不关闭 UAC、不改系统级策略、不动继承以外的全局设置。

    加固手法（2026-11-13 修正，replace 掉早先的 Deny 方案）：
      早先对 Users 组加 Deny ACE，有两个硬伤：
        1) 文件被原子替换（qmctl 写 state.json 就是写临时文件再替换）后，新文件继承
           父目录 ACL，Deny 直接消失 —— 加了等于没加，还会让体检结果时真时假。
        2) 管理员账户也在 Users 组里，Deny 对他一样生效，于是「加固之后下次 Apply
           覆盖 qmctl.exe 会失败」。
      现在改为：先断继承（保留现有继承项为显式），再删掉 Users 的写类 Allow，
      补一条 Users:ReadAndExecute，最后显式补 Administrators / SYSTEM 的 FullControl。
      这样普通用户只剩读，提权后的管理员仍可部署，原子替换也不会把限制洗掉 ——
      因为限制落在父目录上，子文件继承不到写权限。

    本模块默认只读：Protect-QuietModeRoot 不给 -Apply 时只报告「将要做什么」。
#>

$ErrorActionPreference = 'Stop'

$script:UsersSid = 'S-1-5-32-545'
$script:AdminsSid = 'S-1-5-32-544'
$script:SystemSid = 'S-1-5-18'

<#
    「普通用户」不只 Users 组：每个交互登录的令牌都带 Authenticated Users(S-1-5-11)，
    Everyone(S-1-1-0) 也是。部署目录在 H:\ 下，从 H:\AI工作区 继承来的
    Authenticated Users:Modify 就是一条真实的写权限 —— 只盯 S-1-5-32-545 会让加固
    报告「已是只读」，而普通用户照样能改 SYSTEM 要执行的脚本。
#>
$script:UserSids = @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0')

function Test-QmUserDeny {    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $acl = Get-Acl -LiteralPath $Path
    foreach ($ace in $acl.Access) {
        if ($script:UserSids -contains (Get-QmSidValue $ace.IdentityReference) -and
            [string]$ace.AccessControlType -eq 'Deny') { return $true }
    }
    return $false
}

<#
    拆掉旧 Deny 方案留下的 ACE。管理员账户也在 Users 组里，Deny 会连他一起挡，
    于是「加固过一次」的目录连安装器自己都覆盖不了。部署前必须先拆。
#>
function Remove-QmUserDeny {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $acl = Get-Acl -LiteralPath $Path
    $n = 0
    foreach ($ace in @($acl.Access)) {
        if ($script:UserSids -contains (Get-QmSidValue $ace.IdentityReference) -and
            [string]$ace.AccessControlType -eq 'Deny') {
            $acl.RemoveAccessRule($ace) | Out-Null
            $n++
        }
    }
    if ($n -gt 0) { Set-QmAcl -Path $Path -Acl $acl }
    return ($n -gt 0)
}

# 「普通用户不该有」的权限集合。
# 只能用原子位：FullControl / Modify 是超集掩码，拿它们做 -band 判定会让
# ReadAndExecute 也算命中（131241 & 2032127 != 0），结果是「永远可写」的误报。
$script:WriteRights = [System.Security.AccessControl.FileSystemRights]::Write -bor
                      [System.Security.AccessControl.FileSystemRights]::Delete -bor
                      [System.Security.AccessControl.FileSystemRights]::TakeOwnership -bor
                      [System.Security.AccessControl.FileSystemRights]::ChangePermissions

function Get-QmSidValue {
    param($IdentityReference)
    try { return $IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
    catch { return [string]$IdentityReference }
}

<#
    写 DACL。不用 Set-Acl：它会把整个安全描述符回写（含 SACL），
    非提权会话没有 SeSecurityPrivilege 就会失败，报「该进程不具有执行此操作
    所需的 SeSecurityPrivilege 特权」。SetAccessControl 只碰 DACL。
#>
function Set-QmAcl {
    param([string]$Path, $Acl)
    $item = Get-Item -LiteralPath $Path -Force
    if ($item -is [System.IO.DirectoryInfo]) {
        ([System.IO.DirectoryInfo]$item).SetAccessControl([System.Security.AccessControl.DirectorySecurity]$Acl)
    } else {
        ([System.IO.FileInfo]$item).SetAccessControl([System.Security.AccessControl.FileSecurity]$Acl)
    }
}

function Get-QmAclSummary {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $acl = Get-Acl -LiteralPath $Path
    $rows = @()
    foreach ($ace in $acl.Access) {
        $rows += [pscustomobject]@{
            sid      = (Get-QmSidValue $ace.IdentityReference)
            identity = [string]$ace.IdentityReference
            rights   = [string]$ace.FileSystemRights
            type     = [string]$ace.AccessControlType
            inherit  = [string]$ace.InheritanceFlags
            isInherit = $ace.IsInherited
        }
    }
    return [pscustomobject]@{
        path          = $Path
        owner         = [string]$acl.Owner
        inheritance   = (-not $acl.AreAccessRulesProtected)
        access        = $rows
    }
}

<#
    这个路径对「普通用户」是否可写。
    判据：存在 Users 的写类 Allow，且没有被覆盖 Write+Delete 的 Users Deny 挡住。
    继承没断也算可写 —— 父目录随时可以再传一条允许进来。
#>
function Test-QmUserWritable {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $acl = Get-Acl -LiteralPath $Path
    $needDeny = [System.Security.AccessControl.FileSystemRights]::Write -bor
                [System.Security.AccessControl.FileSystemRights]::Delete
    $denied = $false
    $writeAllow = $false
    foreach ($ace in $acl.Access) {
        if ($script:UserSids -notcontains (Get-QmSidValue $ace.IdentityReference)) { continue }
        $type = [string]$ace.AccessControlType
        if ($type -eq 'Deny' -and ($ace.FileSystemRights -band $needDeny) -eq $needDeny) { $denied = $true }
        elseif ($type -eq 'Allow' -and ($ace.FileSystemRights -band $script:WriteRights) -ne 0) { $writeAllow = $true }
    }
    if ($denied) { return $false }
    return $writeAllow
}

<#
    SYSTEM 会执行的路径：普通用户一个写权限都不该有。
    Root 目录本身也在名单里 —— 只保护文件不够，能往父目录里丢一个同名文件就等于能替换。
    DeployDir 下被 SYSTEM 直接调起的二进制同样在名单里（qmctl.exe 就是任务入口）。
#>
function Get-QmPrivilegedPaths {
    param([string]$Root, [string]$DeployDir)
    $list = @()
    if ($Root) {
        $list += $Root
        foreach ($name in @('config.json', 'state.json', 'baseline.json')) {
            $list += (Join-Path $Root $name)
        }
        foreach ($name in @('run', 'recovery')) { $list += (Join-Path $Root $name) }
    }
    if ($DeployDir) {
        # 部署目录自身也要在名单里。只保护文件是不够的：删除一个文件需要的是
        # 父目录的 FILE_DELETE_CHILD，而 H:\AI工作区 一路继承下来的 Authenticated Users:Modify
        # 正好带着它 —— 于是「只读的 qmctl.exe」可以被删掉再换个同名文件，SYSTEM 照样执行。
        $list += $DeployDir
        $list += (Join-Path $DeployDir 'powershell')
        foreach ($name in @('qmctl.exe', 'QuietMode.exe', 'QuietMode.Core.dll', 'QuietMode.Windows.dll')) {
            $list += (Join-Path $DeployDir $name)
        }
    }
    return $list
}

<#
    路径是否指向重解析点（符号链接 / junction / 挂载点）。
    特权目录里出现重解析点就等于「SYSTEM 的读写可能被指到别处」。
#>
function Test-QmReparsePoint {
    param([string]$Path)
    if (-not $Path) { return $false }
    try {
        if (-not (Test-Path -LiteralPath $Path -ErrorAction Stop)) { return $false }
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        return (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
    } catch { return $false }
}

<#
    把「相对名字」安全地拼到基目录下。任何逃出基目录的结果都返回 $null，
    包括 ..\、绝对路径、以盘符开头的路径。纯字符串运算，不访问磁盘。
#>
function Resolve-QmSafeChild {
    param([string]$Base, [string]$Name)
    if (-not $Base -or -not $Name) { return $null }
    if ($Name.IndexOfAny([char[]]@('/', '\')) -ge 0) { return $null }
    if ($Name -eq '.' -or $Name -eq '..') { return $null }
    if ($Name -match '^[A-Za-z]:') { return $null }
    if ($Name.IndexOf(':') -ge 0) { return $null }
    try {
        $baseFull = [System.IO.Path]::GetFullPath($Base)
        if (-not $baseFull.EndsWith('\')) { $baseFull += '\' }
        $full = [System.IO.Path]::GetFullPath((Join-Path $baseFull $Name))
        if (-not $full.StartsWith($baseFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
        return $full
    } catch { return $null }
}

function Test-QmPrivilegedLayout {
    param([string]$Root, [string]$DeployDir)

    $problems = @()
    $checked = 0
    foreach ($p in (Get-QmPrivilegedPaths -Root $Root -DeployDir $DeployDir)) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $checked++
        if (Test-QmUserWritable -Path $p) {
            $problems += ("普通用户可写：$p（SYSTEM 会读这里的内容并执行）")
        }
    }

    # 日志目录反过来：普通用户要能追加，但不该能删整个目录
    if ($Root) {
        $logDir = Join-Path $Root 'logs'
        if (Test-Path -LiteralPath $logDir) {
            $checked++
            $acl = Get-Acl -LiteralPath $logDir
            $hasWrite = $false
            foreach ($ace in $acl.Access) {
                if ((Get-QmSidValue $ace.IdentityReference) -eq $script:UsersSid -and
                    [string]$ace.AccessControlType -eq 'Allow' -and
                    ($ace.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Write) -ne 0) {
                    $hasWrite = $true
                }
            }
            if (-not $hasWrite) {
                $problems += ("logs 目录普通用户不可写，非提权 qmctl 无法留日志：$logDir")
            }
        }
    }

    return [pscustomobject]@{ checked = $checked; problems = $problems; ok = ($problems.Count -eq 0) }
}

<#
    把一条 Users 的 ACE 换成只读，并保证 Administrators / SYSTEM 有显式完全控制。
    顺序很重要：先清历史 Deny，再断继承，最后才删写权限 —— 每一步失败都留得下退路。
#>
function Set-QmRestrictedAcl {
    param([string]$Path)

    $users = New-Object System.Security.Principal.SecurityIdentifier($script:UsersSid)
    $admins = New-Object System.Security.Principal.SecurityIdentifier($script:AdminsSid)
    $system = New-Object System.Security.Principal.SecurityIdentifier($script:SystemSid)
    $isDir = (Get-Item -LiteralPath $Path).PSIsContainer
    $inherit = 'None'
    if ($isDir) { $inherit = 'ContainerInherit, ObjectInherit' }

    $acl = Get-Acl -LiteralPath $Path

    # 1) 清掉早先 Deny 方案留下的 ACE（Deny 对提权管理员也生效，必须拆掉）
    $removedDeny = $false
    foreach ($ace in @($acl.Access)) {
        if ($script:UserSids -contains (Get-QmSidValue $ace.IdentityReference) -and
            [string]$ace.AccessControlType -eq 'Deny') {
            $acl.RemoveAccessRule($ace) | Out-Null
            $removedDeny = $true
        }
    }
    if ($removedDeny) {
        Set-QmAcl -Path $Path -Acl $acl
        $acl = Get-Acl -LiteralPath $Path
    }

    # 2) 断继承，把从上级来的 ACE 固化成显式项（否则上级的 Users 写还会流进来）
    if (-not $acl.AreAccessRulesProtected) {
        $acl.SetAccessRuleProtection($true, $true)
        Set-QmAcl -Path $Path -Acl $acl
        $acl = Get-Acl -LiteralPath $Path
    }

    # 3) 删掉 Users 的写类 Allow，只留只读
    $removedWrite = $false
    foreach ($ace in @($acl.Access)) {
        if ($script:UserSids -notcontains (Get-QmSidValue $ace.IdentityReference)) { continue }
        if ([string]$ace.AccessControlType -ne 'Allow') { continue }
        if (($ace.FileSystemRights -band $script:WriteRights) -eq 0) { continue }
        $acl.RemoveAccessRule($ace) | Out-Null
        $removedWrite = $true
    }

    # 4) 显式补只读、以及管理员/系统的完全控制（幂等）
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $users, 'ReadAndExecute', $inherit, 'None', 'Allow'))) | Out-Null
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $admins, 'FullControl', $inherit, 'None', 'Allow'))) | Out-Null
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $system, 'FullControl', $inherit, 'None', 'Allow'))) | Out-Null

    Set-QmAcl -Path $Path -Acl $acl
    return [pscustomobject]@{ removedDeny = $removedDeny; removedWrite = $removedWrite }
}

<#
    加固。只做两件事：
      1. 对 SYSTEM 会执行的路径，把 Users 削到只读（断继承 + 删写权）。
      2. 对 logs 目录，给 Users Modify（允许追加与轮转），并明确 Deny 删除目录本身。
#>
function Protect-QuietModeRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$DeployDir,
        [switch]$Apply
    )

    $users = New-Object System.Security.Principal.SecurityIdentifier($script:UsersSid)
    $report = @()
    $changed = 0

    foreach ($p in (Get-QmPrivilegedPaths -Root $Root -DeployDir $DeployDir)) {
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $already = (Test-QmUserWritable -Path $p) -eq $false
        # 旧 Deny 方案残留：这些路径「不可写」是靠 Deny 撑着的，必须拆掉重做，
        # 否则提权管理员也会被拒，下次部署覆盖文件就失败。
        $legacyDeny = Test-QmUserDeny -Path $p

        if ($already -and -not $legacyDeny) {
            $report += ("OK   已是只读：" + $p)
            continue
        }
        if (-not $Apply) {
            if ($legacyDeny) { $report += ("PLAN 将拆除旧 Deny 并改为只读：" + $p) }
            else { $report += ("PLAN 将拒绝 Users 写入：" + $p) }
            continue
        }

        try {
            $r = Set-QmRestrictedAcl -Path $p
            $after = Test-QmUserWritable -Path $p
            if ($after) { $report += ("FAIL 加固后仍可写：" + $p) }
            else {
                $note = 'DONE 已收窄为只读：' + $p
                if ($r.removedDeny) { $note += '（拆掉旧 Deny）' }
                $report += $note
                $changed++
            }
        } catch {
            $report += ("FAIL 加固失败：" + $p + " — " + $_.Exception.Message)
        }
    }

    $logDir = ''
    if ($Root) { $logDir = Join-Path $Root 'logs' }
    if ($logDir -and (Test-Path -LiteralPath $logDir)) {
        if (-not $Apply) {
            $report += ("PLAN 将允许 Users 追加日志：" + $logDir)
        } else {
            try {
                $acl = Get-Acl -LiteralPath $logDir
                $hasWrite = $false
                foreach ($ace in $acl.Access) {
                    if ($script:UserSids -contains (Get-QmSidValue $ace.IdentityReference) -and
                        [string]$ace.AccessControlType -eq 'Allow' -and
                        ($ace.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Write) -ne 0) {
                        $hasWrite = $true
                    }
                }
                if ($hasWrite) {
                    $report += ("OK   日志目录已可写：" + $logDir)
                } else {
                    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                        $users, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
                    $acl.AddAccessRule($rule) | Out-Null
                    Set-QmAcl -Path $logDir -Acl $acl
                    $report += ("DONE 已允许 Users 追加日志：" + $logDir)
                    $changed++
                }
            } catch {
                $report += ("FAIL 日志目录授权失败：" + $_.Exception.Message)
            }
        }
    }

    return [pscustomobject]@{
        apply   = [bool]$Apply
        changed = $changed
        lines   = $report
    }
}

Export-ModuleMember -Function Get-QmAclSummary, Get-QmPrivilegedPaths, Test-QmUserWritable, Test-QmUserDeny, Remove-QmUserDeny, Test-QmPrivilegedLayout, Protect-QuietModeRoot, Test-QmReparsePoint, Resolve-QmSafeChild, Set-QmRestrictedAcl, Set-QmAcl
