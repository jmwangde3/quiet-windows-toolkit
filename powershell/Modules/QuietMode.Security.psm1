#Requires -Version 5.1
<#
    QuietMode.Security.psm1 — 特权目录的权限模型（只读体检 + 可选加固）。

    威胁模型（RC2-04）：
      计划任务以 SYSTEM 运行 qmctl，qmctl 会读取 run\reconcile-request-*.json 并据此
      操作计划任务、服务、注册表、磁盘。因此任何「普通用户可以写、SYSTEM 会执行」的
      路径都是一条提权通道。需要保证：

        * config.json / state.json / baseline.json / run\ / recovery\ / powershell\
          普通用户只能读，不能写、不能删、不能改名、不能替换。
        * logs\ 允许追加（普通用户跑 qmctl 时也要能留日志），但不允许删除目录。
        * 不关闭 UAC、不改系统级策略、不动继承以外的全局设置。

    本模块默认只读：Protect-QuietModeRoot 不给 -Apply 时只报告「将要做什么」。
#>

$ErrorActionPreference = 'Stop'

$script:UsersSid = 'S-1-5-32-545'
$script:AdminsSid = 'S-1-5-32-544'
$script:SystemSid = 'S-1-5-18'

function Get-QmSidValue {
    param($IdentityReference)
    try { return $IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
    catch { return [string]$IdentityReference }
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
        }
    }
    return [pscustomobject]@{
        path          = $Path
        owner         = [string]$acl.Owner
        inheritance   = (-not $acl.AreAccessRulesProtected)
        access        = $rows
    }
}

function Test-QmUserWritable {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $acl = Get-Acl -LiteralPath $Path
    foreach ($ace in $acl.Access) {
        if ((Get-QmSidValue $ace.IdentityReference) -ne $script:UsersSid) { continue }
        if ([string]$ace.AccessControlType -ne 'Allow') { continue }
        $rights = $ace.FileSystemRights
        $bad = [System.Security.AccessControl.FileSystemRights]::Write -bor
               [System.Security.AccessControl.FileSystemRights]::Modify -bor
               [System.Security.AccessControl.FileSystemRights]::FullControl -bor
               [System.Security.AccessControl.FileSystemRights]::Delete -bor
               [System.Security.AccessControl.FileSystemRights]::TakeOwnership -bor
               [System.Security.AccessControl.FileSystemRights]::ChangePermissions
        if (($rights -band $bad) -ne 0) { return $true }
    }
    return $false
}

<#
    SYSTEM 会执行的路径：普通用户一个写权限都不该有。
    DeployDir 里是 Worker 脚本与 qmctl.exe；Root 里是配置、请求、恢复资料。
#>
function Get-QmPrivilegedPaths {
    param([string]$Root, [string]$DeployDir)
    $list = @()
    if ($Root) {
        foreach ($name in @('config.json', 'state.json', 'baseline.json')) {
            $list += (Join-Path $Root $name)
        }
        foreach ($name in @('run', 'recovery')) { $list += (Join-Path $Root $name) }
    }
    if ($DeployDir) { $list += (Join-Path $DeployDir 'powershell') }
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
    加固。只做两件事：
      1. 对 SYSTEM 会执行的路径，把 Users 的写/改权限显式 Deny（读保留）。
      2. 对 logs 目录，给 Users Modify（允许追加与轮转），并明确 Deny 删除目录本身。
    Deny 优先于 Allow，因此不会因为上级目录继承了一个 Allow 就破功。
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
        $isDir = (Get-Item -LiteralPath $p).PSIsContainer
        $already = (Test-QmUserWritable -Path $p) -eq $false

        if ($already) {
            $report += ("OK   已是只读：" + $p)
            continue
        }
        if (-not $Apply) {
            $report += ("PLAN 将拒绝 Users 写入：" + $p)
            continue
        }

        try {
            $acl = Get-Acl -LiteralPath $p
            # 先清掉我们之前加过的同类 Deny，避免重复堆积
            foreach ($ace in @($acl.Access)) {
                if ((Get-QmSidValue $ace.IdentityReference) -eq $script:UsersSid -and
                    [string]$ace.AccessControlType -eq 'Deny') {
                    $acl.RemoveAccessRule($ace) | Out-Null
                }
            }
            $inherit = 'None'
            if ($isDir) { $inherit = 'ContainerInherit, ObjectInherit' }
            $deny = New-Object System.Security.AccessControl.FileSystemAccessRule(
                $users, 'Write, Delete, ChangePermissions, TakeOwnership', $inherit, 'None', 'Deny')
            $acl.AddAccessRule($deny) | Out-Null
            Set-Acl -LiteralPath $p -AclObject $acl

            $after = Get-QmUserWritable -Path $p
            if ($after) { $report += ("FAIL 加固后仍可写：" + $p) }
            else { $report += ("DONE 已拒绝 Users 写入：" + $p); $changed++ }
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
                $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                    $users, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
                $acl.AddAccessRule($rule) | Out-Null
                Set-Acl -LiteralPath $logDir -AclObject $acl
                $report += ("DONE 已允许 Users 追加日志：" + $logDir)
                $changed++
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

Export-ModuleMember -Function Get-QmAclSummary, Get-QmPrivilegedPaths, Test-QmUserWritable, Test-QmPrivilegedLayout, Protect-QuietModeRoot, Test-QmReparsePoint, Resolve-QmSafeChild
