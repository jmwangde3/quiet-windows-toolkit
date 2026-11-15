#Requires -Version 5.1
<#
    QuietMode.TaskState — 计划任务状态的唯一判据。

    为什么单独成模块
    ----------------
    这个判据判错一次的代价不是一行 WARN，而是「整台机器失去调度器」：
    安装器把「v1 闸门停不掉」当成不变量违规，于是删掉刚注册的 v2 任务。

    2026-11-15 就这么中过一次。Get-TaskState 靠异常文本判「不存在」，
    PowerShell 5.1 说「找不到任何匹配的 MSFT_ScheduledTask 对象」，
    PowerShell 7.7.0 说「未找到匹配的 MSFT_ScheduledTask 对象」——
    正则只写了前者，PS 7 下「不存在」被判成「无法确认」，回滚被触发。

    修法不是把正则补一个字，而是把判据抽成纯函数：
    Resolve-QmTaskQueryOutcome 只接受（FQID / Category / Message / TaskPath / IsAdmin），
    不碰系统；Get-QmTaskState 只是它的 IO 外壳。于是九种异常形态可以在任何 shell、
    任何机器上断言，不需要真的制造 Access Denied，也不需要停掉任务计划服务。

    铁律
    ----
    只有**明确证明**任务不存在，才返回 Exists = $false。
    权限错误、RPC 失败、服务不可用、名称无法解析、未知异常 —— 一律 $null（无法确认）。
    未提权时连「查不到」都只能算无法确认：文件夹 ACL 的访问检查先于任务对象，
    看不见不等于不存在。

    本模块不写任何系统状态，全部函数只读或纯计算。
#>

$script:UsersReadSid = 'S-1-5-32-545'

# 「明确不是不存在」的异常类别。命中即无法确认，不再看文本。
$script:UnconfirmedCategories = @(
    'PermissionDenied',
    'SecurityError',
    'ResourceUnavailable',
    'ResourceBusy',
    'ConnectionError',
    'AuthenticationError',
    'OperationTimeout',
    'InvalidOperation',
    'MetadataError',
    'NotEnabled',
    'NotInstalled',
    'DeviceNotReady',
    'WriteError'
)

# 拒绝访问：出现这些字样时，即使同时出现「找不到」也不能判不存在
$script:DenialPattern = 'Access\s+is\s+denied|拒绝访问|0x80070005|UnauthorizedAccess|权限不足|无权限'

# 服务/连接不可用：任务计划服务停掉、RPC 断了，都会长得像「查不到」
$script:UnavailablePattern = 'RPC\s+server\s+is\s+unavailable|RPC\s*服务器不可用|0x800706BA|0x80070424|服务尚未启动|service\s+is\s+not\s+available|无法启动服务|The\s+service\s+cannot\s+be\s+started'

# not-found 白名单：**必须**带 MSFT_ScheduledTask 这个对象类型名。
# 只写「找不到」/「未找到」会把「找不到网络路径」这类错误也算进来。
#   PS 5.1: ... 找不到任何匹配的 MSFT_ScheduledTask 对象。请验证查询参数，然后重试。
#   PS 7.7: ... 未找到匹配的 MSFT_ScheduledTask 对象。请验证查询参数，然后重试。
#   英文：  No MSFT_ScheduledTask objects found ... / No matching MSFT_ScheduledTask ...
# 四个分支各自独立：英文分支 `No MSFT_ScheduledTask` 已经把对象名写进去了，
# 不能再要求后面重复一次（写这条时就是这么绊了一跤，被安装器的判据自检抓住）。
$script:NotFoundPattern = '找不到任何匹配的\s*MSFT_ScheduledTask|未找到匹配的\s*MSFT_ScheduledTask|No\s+matching\s+MSFT_ScheduledTask|No\s+MSFT_ScheduledTask'

function Split-QmTaskPath {
    <#
    .SYNOPSIS
        把任务全名拆成 TaskPath + TaskName。
    .DESCRIPTION
        任务全名一律以 '\' 开头；'\' 本身和尾部反斜杠是文件夹写法，不是任务名。
        传入文件夹路径时 Valid = $false，调用方必须按「无法确认」处理，
        **不能**把它当成某个名字诡异的任务去查。
    #>
    [CmdletBinding()]
    param([string]$FullName)

    if ([string]::IsNullOrWhiteSpace($FullName)) {
        return [pscustomobject]@{ Path = $null; Name = $null; Valid = $false }
    }

    $text = $FullName.Trim()
    if (-not $text.StartsWith('\')) { $text = '\' + $text }

    # 尾部反斜杠只能出现在文件夹写法里：'\QuietHDD\' 不是任务
    if ($text.Length -gt 1 -and $text.EndsWith('\')) {
        return [pscustomobject]@{ Path = $text; Name = ''; Valid = $false }
    }

    $idx = $text.LastIndexOf('\')
    if ($idx -lt 0) {
        return [pscustomobject]@{ Path = $null; Name = $null; Valid = $false }
    }

    $name = $text.Substring($idx + 1)
    if ([string]::IsNullOrEmpty($name)) {
        # 传入的就是根 '\'
        return [pscustomobject]@{ Path = '\'; Name = ''; Valid = $false }
    }

    $path = '\'
    if ($idx -gt 0) { $path = $text.Substring(0, $idx + 1) }

    return [pscustomobject]@{ Path = $path; Name = $name; Valid = $true }
}

function Test-QmAdmin {
    <#
    .SYNOPSIS
        当前会话是否提权。失败一律 false（宁可认为没提权，走保守分支）。
    #>
    [CmdletBinding()]
    param()

    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($id)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Test-QmUsersReadAce {
    <#
    .SYNOPSIS
        判断一段 SDDL 里是否已有 **带读权限** 的 BUILTIN\Users Allow ACE。
    .DESCRIPTION
        只看「有没有 BU」是不够的：一条只有写权限的 ACE 也会被旧正则命中，
        于是补授权被跳过，普通用户照样读不到任务。

        权限串支持两种写法：
          - 别名：FR / FA / GR / GRGX / GA 视为含读
          - 十六进制：含 FILE_READ_DATA(0x1) 或 GENERIC_READ(0x80000000) 视为含读
        其余别名（FW / FX / SD / WD ...）不含读，判 false。
    #>
    [CmdletBinding()]
    param([string]$Sddl)

    if ([string]::IsNullOrWhiteSpace($Sddl)) { return $false }
    if ($Sddl -eq 'D:NO_ACCESS_CONTROL') { return $false }

    $readAliases = @('FR', 'FA', 'GR', 'GRGX', 'GA')

    # (AceType;AceFlags;Rights;ObjectGuid;InheritGuid;Sid) —— 本场景里 SID 位就是 BU 别名
    foreach ($m in [regex]::Matches($Sddl, '\(([AD]);([^;]*);([^;]*);[^;]*;[^;]*;([^)]*)\)')) {
        if ($m.Groups[1].Value -ne 'A') { continue }
        if ($m.Groups[4].Value -ne 'BU') { continue }

        $rights = $m.Groups[3].Value
        if ([string]::IsNullOrWhiteSpace($rights)) { continue }

        if ($rights.StartsWith('0x')) {
            $value = 0L
            try { $value = [Convert]::ToInt64($rights.Substring(2), 16) } catch { continue }
            # FILE_READ_DATA = 0x1，GENERIC_READ = 0x80000000
            if (($value -band 0x1) -ne 0) { return $true }
            if (($value -band 0x80000000) -ne 0) { return $true }
            continue
        }

        # 别名可能是连写（例如 FRFW）；逐段比对
        foreach ($alias in $readAliases) {
            if ($rights.Contains($alias)) { return $true }
        }
    }

    return $false
}

function Test-QmFileHasUsersRead {
    <#
    .SYNOPSIS
        文件/目录 ACL 对象上是否已有「带读权限」的 BUILTIN\Users Allow 规则。
    .DESCRIPTION
        与 Test-QmUsersReadAce 同一套语义，只是输入换成 System.Security.AccessControl
        对象。必须看 FileSystemRights 的位，不能只比对 SID ——
        一条 `Users:Write` 的 ACE 会让「只比对 SID」的旧实现直接 continue，
        补授权被跳过，普通用户照样读不到任务，脚本却报 OK。
    #>
    [CmdletBinding()]
    param($Acl)

    if ($null -eq $Acl) { return $false }

    foreach ($r in @($Acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        if ($r.IdentityReference.Value -ne $script:UsersReadSid) { continue }
        # FILE_READ_DATA = 0x1；ReadAndExecute / Modify / FullControl 都含它，
        # Write / ExecuteFile / Synchronize 不含。
        $rights = [int]$r.FileSystemRights
        if (($rights -band [int][System.Security.AccessControl.FileSystemRights]::ReadData) -ne 0) { return $true }
    }
    return $false
}

function Resolve-QmTaskQueryOutcome {
    <#
    .SYNOPSIS
        根据一次任务查询的异常信息，判定 Exists 的三态（$true / $false / $null）。
    .DESCRIPTION
        纯函数，不碰系统。这是整个安装器里唯一允许说「任务不存在」的地方。

        判定顺序：
          1. 异常类别属于权限/连接/服务/超时/元数据 → 无法确认
          2. 文本含拒绝访问 / 服务不可用 → 无法确认（即使同时含「找不到」）
          3. FQID 含 CmdletizationQuery_NotFound → 确定不存在（两个 PS 版本一致）
          4. 文本命中 not-found 白名单（必须带 MSFT_ScheduledTask）→ 确定不存在
          5. 其他 → 无法确认
        另外：未提权时即便命中 3/4 也只算无法确认 —— 文件夹 ACL 会先挡住，
        「看不见」与「不存在」在非提权会话里无法区分。
    #>
    [CmdletBinding()]
    param(
        [string]$FullyQualifiedErrorId = '',
        [string]$Category = '',
        [string]$Message = '',
        [string]$TaskPath = '\',
        [object]$IsAdmin = $null
    )

    function New-Outcome {
        param([object]$Exists, [string]$Reason, [string]$ErrorText)
        return [pscustomobject]@{
            Exists  = $Exists
            Enabled = $null
            State   = ''
            Error   = $ErrorText
            Reason  = $Reason
        }
    }

    $admin = $true
    if ($null -ne $IsAdmin) { $admin = [bool]$IsAdmin }

    if ($Category -and ($script:UnconfirmedCategories -contains $Category)) {
        return New-Outcome $null ('Unconfirmed:' + $Category) $Message
    }

    if ($Message -and ($Message -match $script:DenialPattern)) {
        return New-Outcome $null 'Unconfirmed:Denied' $Message
    }

    if ($Message -and ($Message -match $script:UnavailablePattern)) {
        return New-Outcome $null 'Unconfirmed:Unavailable' $Message
    }

    $byFqid = $false
    if ($FullyQualifiedErrorId) {
        $byFqid = [bool]($FullyQualifiedErrorId -match 'CmdletizationQuery_NotFound')
    }

    $byText = $false
    if ($Message) {
        $byText = [bool]($Message -match $script:NotFoundPattern)
    }

    if ($byFqid -or $byText) {
        if (-not $admin) {
            return New-Outcome $null 'Unconfirmed:NotElevated' ('未提权，『查不到』可能只是读不到（读不到 ≠ 不存在）')
        }
        return New-Outcome $false 'NotFound' ''
    }

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = '查询失败且没有异常文本'
    }
    return New-Outcome $null 'Unconfirmed:Other' $Message
}

function Get-QmTaskState {
    <#
    .SYNOPSIS
        查询一个计划任务的三态状态。只读。
    .OUTPUTS
        pscustomobject: Exists($true/$false/$null) Enabled State Error Reason TaskPath TaskName
    #>
    [CmdletBinding()]
    param(
        # 空串/空值不抛绑定异常：那会让调用方在 try/catch 之外被打断，
        # 而「名字给错了」的正确结论是「无法确认」，不是让整个安装器崩掉。
        [Parameter(Mandatory = $true)][AllowEmptyString()][AllowNull()][string]$FullName,
        [object]$IsAdmin = $null
    )

    $tp = Split-QmTaskPath -FullName $FullName
    if (-not $tp.Valid) {
        return [pscustomobject]@{
            Exists   = $null
            Enabled  = $null
            State    = ''
            Error    = ('任务全名无法解析（不能是文件夹或空名）：' + $FullName)
            Reason   = 'Unconfirmed:BadName'
            TaskPath = $tp.Path
            TaskName = $tp.Name
        }
    }

    $admin = Test-QmAdmin
    if ($null -ne $IsAdmin) { $admin = [bool]$IsAdmin }

    try {
        $t = Get-ScheduledTask -TaskPath $tp.Path -TaskName $tp.Name -ErrorAction Stop
        return [pscustomobject]@{
            Exists   = $true
            Enabled  = ($t.State -ne 'Disabled')
            State    = [string]$t.State
            Error    = ''
            Reason   = 'Found'
            TaskPath = $tp.Path
            TaskName = $tp.Name
        }
    } catch {
        $fqid = ''
        $cat = ''
        $msg = ''
        try { $fqid = [string]$_.FullyQualifiedErrorId } catch { }
        try { $cat = [string]$_.CategoryInfo.Category } catch { }
        try { $msg = [string]$_.Exception.Message } catch { }

        $out = Resolve-QmTaskQueryOutcome -FullyQualifiedErrorId $fqid -Category $cat `
            -Message $msg -TaskPath $tp.Path -IsAdmin $admin
        return [pscustomobject]@{
            Exists   = $out.Exists
            Enabled  = $out.Enabled
            State    = $out.State
            Error    = $out.Error
            Reason   = $out.Reason
            TaskPath = $tp.Path
            TaskName = $tp.Name
        }
    }
}

Export-ModuleMember -Function Split-QmTaskPath, Test-QmAdmin, Test-QmUsersReadAce, `
    Test-QmFileHasUsersRead, Resolve-QmTaskQueryOutcome, Get-QmTaskState
