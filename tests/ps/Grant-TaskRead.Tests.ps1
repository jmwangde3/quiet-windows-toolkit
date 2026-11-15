#Requires -Version 5.1
<#
    Grant-TaskRead 的 ACL 逻辑测试。

    分三层，越往下越需要真实环境：

      1. 纯判据  —— Test-QmFileHasUsersRead / Test-QmUsersReadAce
                    在**内存中的 FileSecurity 对象**上验证「已有写权限 ≠ 已有读」。
                    不落盘、不改真实 ACL，因此不需要提权，也不需要 SeSecurityPrivilege。
      2. 脚本契约 —— -Check 只读；未提权时 -Apply 必须返回非零而不是 0。
                    两个脚本都会 exit，所以一律走子进程调用，否则会把 Pester 一起带走。
      3. 真实集成 —— 建带唯一前缀的临时任务文件夹，跑真的 SetSecurityDescriptor，
                    回读 SDDL 验证「新增 ACE 在位 + 原有 ACE 没丢 + 重复执行不重复加」。

    第 3 层只在「已提权 + QM_ALLOW_TASK_INTEGRATION=1」时执行。
    本地开发机默认跳过 —— 不允许为了测试去碰日常使用机器上的计划任务；
    CI 的 windows-latest 是一次性 runner，由工作流显式打开这个开关。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.TaskState.psm1') -Force

    $script:GrantScript = Join-Path $PSScriptRoot '..\..\powershell\Grant-TaskRead.ps1'
    $script:UsersSid = 'S-1-5-32-545'
    $script:MeSid = [Security.Principal.WindowsIdentity]::GetCurrent().User

    # 用子进程执行会 exit 的脚本：直接 & 调用会把调用方的 Pester 一起终止
    function Invoke-ScriptChild {
        param([string]$Path, [string[]]$Arguments = @())
        $hostExe = (Get-Process -Id $PID).Path
        $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Path) + $Arguments
        $text = (& $hostExe @all 2>&1 | Out-String)
        return [pscustomobject]@{ Text = $text; Code = $LASTEXITCODE }
    }

    # 内存 ACL：不碰文件系统，所以任何身份都能跑。
    # 注意：SID 必须在函数内部现取。Pester 5 在 PowerShell 7 下把变量复制进 It 的
    # session state，SecurityIdentifier **对象**会在这一步失效（字符串没事），
    # 于是 AddAccessRule 静默加不上，用例会以「Count 应为 1 实际 0」的形式挂掉。
    function New-AclProbe {
        param([string]$UsersRights = $null, [string]$AceType = 'Allow')
        $meSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $sec = New-Object System.Security.AccessControl.FileSecurity
        $sec.SetAccessRuleProtection($true, $false)
        $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $meSid, 'FullControl', 'Allow')))
        if ($UsersRights) {
            $usersSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
            $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                $usersSid, $UsersRights, $AceType)))
        }
        return $sec
    }
}

Describe 'Test-QmFileHasUsersRead — ACL 对象（内存，不落盘）' {

    It '只有当前用户 FullControl、没有 Users ACE 时判 false' {
        $acl = New-AclProbe
        # 前置校验用 SDDL 而不是 Access.Count：后者在 Pester + PowerShell 7 下
        # 会因为对象跨 session state 传递而读成 0，与真实 DACL 无关。
        $sddl = $acl.GetSecurityDescriptorSddlForm('Access')
        $sddl | Should -Not -Match ';;;BU\)'
        Test-QmFileHasUsersRead -Acl $acl | Should -BeFalse
    }

    It 'Users 只有 Write 权限时必须判 false —— 旧实现只看 SID，这里会漏' {
        Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights 'Write') | Should -BeFalse
    }

    It 'Users 只有 ExecuteFile 时必须判 false' {
        Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights 'ExecuteFile') | Should -BeFalse
    }

    It 'Users 有 ReadAndExecute 时判 true' {
        Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights 'ReadAndExecute') | Should -BeTrue
    }

    It 'Users 有 FullControl 时判 true' {
        Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights 'FullControl') | Should -BeTrue
    }

    It 'Users 有 Modify 时判 true（含读位）' {
        Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights 'Modify') | Should -BeTrue
    }

    It 'Deny 类型的 Users 读不算 Allow' {
        Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights 'ReadAndExecute' -AceType 'Deny') |
            Should -BeFalse
    }

    It 'Acl 为 $null 时返回 false，不抛异常' {
        Test-QmFileHasUsersRead -Acl $null | Should -BeFalse
    }

    It '对真实文件的只读访问不抛异常（不修改任何 ACL）' {
        $f = Join-Path $TestDrive 'readonly-probe.txt'
        Set-Content -LiteralPath $f -Value 'probe' -Encoding ASCII
        { Test-QmFileHasUsersRead -Acl (Get-Acl -LiteralPath $f) } | Should -Not -Throw
    }
}

Describe 'SDDL 与 ACL 两套判据结论一致' {

    It '同一组权限形态在两套判据下必须给出相同答案' {
        $pairs = @(
            @{ Rights = 'Write';          Sddl = 'D:AI(A;OICI;FW;;;BU)' },
            @{ Rights = 'ReadAndExecute'; Sddl = 'D:AI(A;OICI;FR;;;BU)' },
            @{ Rights = 'Modify';         Sddl = 'D:AI(A;OICI;0x1301bf;;;BU)' },
            @{ Rights = 'FullControl';    Sddl = 'D:AI(A;OICI;FA;;;BU)' }
        )
        foreach ($p in $pairs) {
            $fromAcl  = Test-QmFileHasUsersRead -Acl (New-AclProbe -UsersRights $p.Rights)
            $fromSddl = Test-QmUsersReadAce -Sddl $p.Sddl
            $fromAcl | Should -Be $fromSddl -Because ("权限 " + $p.Rights + " 在两套判据下结论必须一致")
        }
    }
}

Describe 'Grant-TaskRead 脚本契约' {

    It '脚本文件存在' {
        Test-Path -LiteralPath $script:GrantScript | Should -BeTrue
    }

    It '-Check 是只读自检：退出码 0 或 2，且明确标注「只读」' {
        $r = Invoke-ScriptChild -Path $script:GrantScript -Arguments @('-Check', '-Folders', '\QuietMode\')
        $r.Text | Should -Match '只读'
        $r.Code | Should -BeIn 0, 2
    }

    It '未提权时 -Apply 必须返回 3，不能假装成功' {
        $admin = try {
            (New-Object Security.Principal.WindowsPrincipal(
                [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        } catch { $false }

        if ($admin) {
            Set-ItResult -Skipped -Because '当前会话已提权，无法验证未提权分支'
            return
        }
        $r = Invoke-ScriptChild -Path $script:GrantScript -Arguments @('-Apply', '-Folders', '\QuietMode\')
        $r.Code | Should -Be 3
        $r.Text | Should -Match '退出码 3'
    }

    It '脚本不再调用 FileInfo 的实例 GetAccessControl（PS 7 会抛「不包含名为的方法」）' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:GrantScript, [ref]$null, [ref]$null)
        $hits = $ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $n.Member.Extent.Text -eq 'GetAccessControl'
        }, $true)
        @($hits).Count | Should -Be 0
    }

    It '脚本公开的开关仍然齐全（-Apply / -Check / -IncludeTasks / -Folders）' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:GrantScript, [ref]$null, [ref]$null)
        $paramBlock = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $true)
        $names = @($paramBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
        foreach ($n in @('Folders', 'Apply', 'Check', 'IncludeTasks')) {
            $names | Should -Contain $n
        }
    }
}

Describe 'Grant-TaskRead 真实任务集成（临时文件夹，需要提权 + 显式开关）' {

    BeforeDiscovery {
        # `It ... -Skip:(expr)` 的表达式在 **discovery 阶段**求值，那时本 Describe 的
        # BeforeAll 还没跑 —— 把判断写在 BeforeAll 里，$script:runIntegration 在
        # discovery 时恒为 $null，而 `-not $null` 是 $true，于是这 4 条用例
        # **在任何环境下都被跳过**，包括已经打开开关的提权 CI runner。
        #
        # 这不是推测：CI 设了 QM_ALLOW_TASK_INTEGRATION=1、runner 本身提权，
        # Pester 数字仍是 Total=185 Passed=178 Skipped=7（= 4 条集成 + 3 条
        # 「未提权」用例），多出来的 4 条就是它们。判断必须搬到 BeforeDiscovery。
        $adminNow = $false
        try {
            $adminNow = (New-Object Security.Principal.WindowsPrincipal(
                [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        } catch { $adminNow = $false }
        $script:runIntegration = ([bool]$adminNow -and $env:QM_ALLOW_TASK_INTEGRATION -eq '1')
    }

    BeforeAll {
        $script:admin = try {
            (New-Object Security.Principal.WindowsPrincipal(
                [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        } catch { $false }

        $script:runIntegration = ($script:admin -and $env:QM_ALLOW_TASK_INTEGRATION -eq '1')
        $script:suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
        $script:tFolderName = 'QuietMode-Test-' + $script:suffix
        $script:tFolder = '\' + $script:tFolderName + '\'
        $script:tTask = 'AclProbe-' + $script:suffix

        if ($script:runIntegration) {
            $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit 0'
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(1)
            Register-ScheduledTask -TaskName $script:tTask -TaskPath $script:tFolder `
                -Action $action -Trigger $trigger `
                -Description 'QuietMode ACL 集成测试临时任务（可安全删除）' | Out-Null
        }

        function Get-FolderSddl {
            param([string]$Path)
            $svc = New-Object -ComObject Schedule.Service
            $svc.Connect()
            try { return [string]$svc.GetFolder($Path.TrimEnd('\')).GetSecurityDescriptor(4) }
            finally { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { } }
        }

        function Get-TaskSddl {
            param([string]$Path, [string]$Name)
            $svc = New-Object -ComObject Schedule.Service
            $svc.Connect()
            try {
                $folder = $svc.GetFolder($Path.TrimEnd('\'))
                return [string]$folder.GetTask($Name).GetSecurityDescriptor(4)
            } finally { try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { } }
        }
    }

    AfterAll {
        if ($script:runIntegration) {
            Unregister-ScheduledTask -TaskName $script:tTask -TaskPath $script:tFolder -Confirm:$false -ErrorAction SilentlyContinue
            try {
                $svc = New-Object -ComObject Schedule.Service
                $svc.Connect()
                $svc.GetFolder('\').DeleteFolder($script:tFolderName, 0)
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($svc)
            } catch { }
        }
    }

    It '临时对象名带唯一前缀，不会撞到生产任务' {
        $script:tFolder.StartsWith('\QuietMode-Test-') | Should -BeTrue
        $script:tFolder | Should -Not -Be '\QuietMode\'
        $script:tFolder | Should -Not -Be '\QuietHDD\'
    }

    It 'Apply 后文件夹 SDDL 含 Users 读 ACE，且原有 ACE 一条不少' -Skip:(-not $script:runIntegration) {
        $before = Get-FolderSddl -Path $script:tFolder
        $before | Should -Not -BeNullOrEmpty

        $r = Invoke-ScriptChild -Path $script:GrantScript -Arguments @('-Apply', '-Folders', $script:tFolder)
        $r.Code | Should -Be 0 -Because ('授权脚本输出：' + $r.Text)

        $after = Get-FolderSddl -Path $script:tFolder
        Test-QmUsersReadAce -Sddl $after | Should -BeTrue

        # 原有 ACE 逐条核对：SetSecurityDescriptor 把 DACL 覆盖成只剩新 ACE 是最危险的失败形态
        foreach ($m in [regex]::Matches($before, '\([AD];[^()]*\)')) {
            $after.Contains($m.Value) | Should -BeTrue -Because ('原有 ACE 丢失：' + $m.Value)
        }
    }

    It '任务对象 Apply 后也含 Users 读 ACE' -Skip:(-not $script:runIntegration) {
        $r = Invoke-ScriptChild -Path $script:GrantScript -Arguments @('-Apply', '-Folders', $script:tFolder, '-IncludeTasks')
        $r.Code | Should -Be 0 -Because ('授权脚本输出：' + $r.Text)
        $sd = Get-TaskSddl -Path $script:tFolder -Name $script:tTask
        Test-QmUsersReadAce -Sddl $sd | Should -BeTrue
    }

    It '重复执行不重复添加 ACE（第二次应报「已有 Users 读」）' -Skip:(-not $script:runIntegration) {
        $before = Get-TaskSddl -Path $script:tFolder -Name $script:tTask
        $countBefore = ([regex]::Matches($before, '\(A;[^()]*;;;BU\)')).Count

        $r = Invoke-ScriptChild -Path $script:GrantScript -Arguments @('-Apply', '-Folders', $script:tFolder, '-IncludeTasks')
        $r.Code | Should -Be 0
        $r.Text | Should -Match '已有 Users 读'

        $after = Get-TaskSddl -Path $script:tFolder -Name $script:tTask
        $countAfter = ([regex]::Matches($after, '\(A;[^()]*;;;BU\)')).Count
        $countAfter | Should -Be $countBefore
    }

    It 'Users 拿到的只是读权限，不含修改/删除/启停任务' -Skip:(-not $script:runIntegration) {
        $sd = Get-TaskSddl -Path $script:tFolder -Name $script:tTask
        # FR = FILE_GENERIC_READ。对任务对象而言，FILE_WRITE_DATA / DELETE / WRITE_DAC
        # 才是「能改任务」，这里必须一个都没给。
        $m = [regex]::Match($sd, '\(A;[^;]*;([^;]*);[^;]*;[^;]*;BU\)')
        $m.Success | Should -BeTrue
        $rights = $m.Groups[1].Value
        $rights | Should -Be 'FR' -Because 'Users 的 ACE 只能是只读位，不能扩大'
        $rights | Should -Not -Match 'FA|FW|GA|WDAC|DELETE'
    }
}
