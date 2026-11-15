#Requires -Version 5.1
<#
    QuietMode.TaskState 判据测试。

    这是整个仓库里唯一允许说「任务不存在」的地方，判错一次的代价是
    「机器上不再有任何调度器」。2026-11-15 用 PowerShell 7 跑 Apply 时，
    异常文本从「找不到任何匹配」变成「未找到匹配」，旧正则漏掉后者，
    「不存在」被判成「无法确认」→ 不变量回滚把刚装好的 v2 删了。

    下面把九种异常形态逐条钉死。原则只有一句：
    只有**明确证明**任务不存在才允许 Exists = $false，
    权限错误、RPC 失败、服务不可用、名称非法、未知异常一律 $null。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.TaskState.psm1') -Force

    # 真实的两种官方措辞，直接从实测日志里抄下来的
    $script:TextPs51 = 'CIM 服务器上的 Root/Microsoft/Windows/TaskScheduler/MSFT_ScheduledTask 类实例的 CIM 查询: SELECT * FROM MSFT_ScheduledTask  WHERE ((TaskName LIKE ''QuietHDD[_]NightGate'')) AND ((TaskPath LIKE ''\\'')) 找不到任何匹配的 MSFT_ScheduledTask 对象。请验证查询参数，然后重试。'
    $script:TextPs7  = '用于获取位于  CIM 服务器上 Root/Microsoft/Windows/TaskScheduler/MSFT_ScheduledTask 类实例的 CIM 查询: SELECT * FROM MSFT_ScheduledTask  WHERE ((TaskName LIKE ''QuietHDD[_]NightGate'')) AND ((TaskPath LIKE ''\\'')) 未找到匹配的 MSFT_ScheduledTask 对象。请验证查询参数，然后重试。'
}

Describe 'Split-QmTaskPath' {

    It '两层文件夹：路径带首尾反斜杠，名字是最后一段' {
        $r = Split-QmTaskPath -FullName '\QuietHDD\QuietHDD_NightGate'
        $r.Valid | Should -BeTrue
        $r.Path  | Should -Be '\QuietHDD\'
        $r.Name  | Should -Be 'QuietHDD_NightGate'
    }

    It '根路径任务：路径就是单个反斜杠' {
        $r = Split-QmTaskPath -FullName '\QuietHDD_NightGate'
        $r.Valid | Should -BeTrue
        $r.Path  | Should -Be '\'
        $r.Name  | Should -Be 'QuietHDD_NightGate'
    }

    It '三层文件夹也对' {
        $r = Split-QmTaskPath -FullName '\A\B\Task'
        $r.Path | Should -Be '\A\B\'
        $r.Name | Should -Be 'Task'
    }

    It '没有前导反斜杠时自动补上' {
        $r = Split-QmTaskPath -FullName 'QuietHDD\QuietHDD_DiskGate'
        $r.Valid | Should -BeTrue
        $r.Path  | Should -Be '\QuietHDD\'
        $r.Name  | Should -Be 'QuietHDD_DiskGate'
    }

    It '文件夹写法（尾部反斜杠）必须判为非法，不能当成任务名' {
        (Split-QmTaskPath -FullName '\QuietHDD\').Valid | Should -BeFalse
    }

    It '空串与纯空白判为非法' {
        (Split-QmTaskPath -FullName '').Valid    | Should -BeFalse
        (Split-QmTaskPath -FullName '   ').Valid | Should -BeFalse
    }

    It '根反斜杠本身不是任务' {
        (Split-QmTaskPath -FullName '\').Valid | Should -BeFalse
    }
}

Describe 'Resolve-QmTaskQueryOutcome — 不存在' {

    It 'FQID CmdletizationQuery_NotFound 判为不存在（两个 PS 版本一致）' {
        $r = Resolve-QmTaskQueryOutcome -FullyQualifiedErrorId 'CmdletizationQuery_NotFound,Get-ScheduledTask' `
            -Category 'ObjectNotFound' -Message '' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeFalse
        $r.Reason | Should -Be 'NotFound'
    }

    It 'PS 5.1 措辞「找不到任何匹配」判为不存在' {
        $r = Resolve-QmTaskQueryOutcome -Category 'ObjectNotFound' -Message $script:TextPs51 -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeFalse
    }

    It 'PS 7 措辞「未找到匹配」判为不存在 —— 这就是 2026-11-15 事故的那一行' {
        $r = Resolve-QmTaskQueryOutcome -Category 'ObjectNotFound' -Message $script:TextPs7 -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeFalse
    }

    It '英文 No MSFT_ScheduledTask 判为不存在' {
        $r = Resolve-QmTaskQueryOutcome -Message 'No MSFT_ScheduledTask objects found with property values matching' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeFalse
    }

    It '英文 No matching MSFT_ScheduledTask 判为不存在' {
        $r = Resolve-QmTaskQueryOutcome -Message 'No matching MSFT_ScheduledTask objects were found' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeFalse
    }

    It '只有措辞、没有 FQID 也够（文本兜底）' {
        $r = Resolve-QmTaskQueryOutcome -Category 'ObjectNotFound' -Message $script:TextPs7 -TaskPath '\QuietHDD\' -IsAdmin $true
        $r.Reason | Should -Be 'NotFound'
    }
}

Describe 'Resolve-QmTaskQueryOutcome — 无法确认（必须返回 null）' {

    It 'Access Denied（中文）→ null' {
        $r = Resolve-QmTaskQueryOutcome -Category 'PermissionDenied' -Message '拒绝访问。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
        $r.Reason | Should -Be 'Unconfirmed:PermissionDenied'
    }

    It 'Access Denied（英文）→ null' {
        $r = Resolve-QmTaskQueryOutcome -Category 'PermissionDenied' -Message 'Access is denied' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
    }

    It 'HRESULT 0x80070005 出现在文本里 → null' {
        $r = Resolve-QmTaskQueryOutcome -Message '操作失败，错误 0x80070005。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
    }

    It 'RPC 服务器不可用 → null' {
        $r = Resolve-QmTaskQueryOutcome -Category 'ResourceUnavailable' -Message 'RPC 服务器不可用。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
        $r.Reason | Should -Be 'Unconfirmed:ResourceUnavailable'
    }

    It '任务计划服务未启动 → null' {
        $r = Resolve-QmTaskQueryOutcome -Category 'ResourceUnavailable' -Message '服务尚未启动。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
    }

    It '找不到网络路径 —— 含「找不到」但不是任务不存在 → null' {
        $r = Resolve-QmTaskQueryOutcome -Category 'NotSpecified' -Message '找不到网络路径。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
        $r.Reason | Should -Be 'Unconfirmed:Other'
    }

    It '未知异常 → null' {
        $r = Resolve-QmTaskQueryOutcome -Category 'NotSpecified' -Message '对象引用未设置到对象的实例。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
    }

    It '拒绝访问同时含「找不到」→ 仍然是 null（权限优先）' {
        $r = Resolve-QmTaskQueryOutcome -Message '找不到任务：拒绝访问。' -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
        $r.Reason | Should -Be 'Unconfirmed:Denied'
    }

    It '没有任何异常文本 → null，不能当成不存在' {
        $r = Resolve-QmTaskQueryOutcome -TaskPath '\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
    }

    It '未提权时「查不到」只能算无法确认（读不到 ≠ 不存在）' {
        $r = Resolve-QmTaskQueryOutcome -FullyQualifiedErrorId 'CmdletizationQuery_NotFound,Get-ScheduledTask' `
            -Category 'ObjectNotFound' -Message $script:TextPs51 -TaskPath '\QuietHDD\' -IsAdmin $false
        $r.Exists | Should -BeNullOrEmpty
        $r.Reason | Should -Be 'Unconfirmed:NotElevated'
    }
}

Describe 'Test-QmUsersReadAce' {

    It '文件夹 ACE (A;OICI;FR;;;BU) 含读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;FR;;;BU)(A;CIID;FA;;;BA)' | Should -BeTrue
    }

    It '任务对象 ACE (A;;FR;;;BU) 含读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;;FR;;;BU)(A;;FA;;;BA)' | Should -BeTrue
    }

    It 'FullControl 别名 FA 含读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;FA;;;BU)' | Should -BeTrue
    }

    It '只有写权限 (FW) 不算含读 —— 旧正则在这里会误判成已有读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;FW;;;BU)' | Should -BeFalse
    }

    It '只有执行 (FX) 不算含读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;FX;;;BU)' | Should -BeFalse
    }

    It '十六进制读位 0x1200a9 含读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;0x1200a9;;;BU)' | Should -BeTrue
    }

    It '十六进制写位 0x120116 不算含读' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;0x120116;;;BU)' | Should -BeFalse
    }

    It '只有管理员 ACE 时不算' {
        Test-QmUsersReadAce -Sddl 'D:AI(A;OICI;FA;;;BA)' | Should -BeFalse
    }

    It 'Deny 类型的 BU ACE 不算 Allow 读' {
        Test-QmUsersReadAce -Sddl 'D:AI(D;OICI;FR;;;BU)' | Should -BeFalse
    }

    It '空 SDDL 与 NO_ACCESS_CONTROL 都不算' {
        Test-QmUsersReadAce -Sddl '' | Should -BeFalse
        Test-QmUsersReadAce -Sddl 'D:NO_ACCESS_CONTROL' | Should -BeFalse
    }
}

Describe 'Test-QmAdmin' {

    It '返回布尔值，不抛异常' {
        $v = Test-QmAdmin
        $v | Should -BeOfType [bool]
    }
}

Describe 'Get-QmTaskState — 真机只读' {

    It '不存在的任务在提权语义下判为不存在' {
        $r = Get-QmTaskState -FullName '\__QuietModeSelfCheck__\__NoSuchTask__' -IsAdmin $true
        $r.Exists | Should -BeFalse
        $r.Reason | Should -Be 'NotFound'
    }

    It '同一个任务在未提权语义下只能给 null' {
        $r = Get-QmTaskState -FullName '\__QuietModeSelfCheck__\__NoSuchTask__' -IsAdmin $false
        $r.Exists | Should -BeNullOrEmpty
    }

    It '文件夹路径不会去查一个名字为空的任务' {
        $r = Get-QmTaskState -FullName '\QuietHDD\' -IsAdmin $true
        $r.Exists | Should -BeNullOrEmpty
        $r.Reason | Should -Be 'Unconfirmed:BadName'
    }

    It '已注册的 v2 任务若存在必须读到 Exists=true —— 读不到只允许 null，不允许 false' {
        $r = Get-QmTaskState -FullName '\QuietMode\QuietMode-Reconcile'
        if ($null -eq $r.Exists) {
            $r.Exists | Should -BeNullOrEmpty          # 无法确认，可接受
            $r.Reason | Should -Match '^Unconfirmed:'
        } else {
            $r.Exists | Should -BeTrue
            $r.Enabled | Should -BeOfType [bool]
        }
    }
}
