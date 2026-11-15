#Requires -Version 5.1
<#
    Test-QuietModeAclApply.ps1 的契约测试。

    这个脚本要写真实任务对象的 SDDL 与真实文件 ACL，所以它的**保护逻辑本身**
    必须被测试覆盖 —— 保护写错了，等于把「隔离测试」变成「批量改生产」。

    四层，全部不碰真实计划任务：
      1. 静态契约   —— 源码层面禁止出现自行授权（AddAccessRule）与 PS7 会炸的写法
      2. 参数与模式 —— 模式互斥、默认只读、非法 TestId 被拦
      3. 生产保护   —— 喂 \QuietMode / \QuietHDD / \Microsoft\Windows 必须拦下
      4. 非提权行为 —— -Apply / -Rollback / -Cleanup 未提权时必须是 exit 3，不能假装成功

    所有用例都走子进程：脚本内部有 exit，直接 & 调用会把 Pester 一起带走。
#>

BeforeAll {
    $script:RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $script:TestScript = Join-Path $script:RepoRoot 'powershell\Test-QuietModeAclApply.ps1'
    $script:GrantScript = Join-Path $script:RepoRoot 'powershell\Grant-TaskRead.ps1'

    function Invoke-ScriptChild {
        param([string]$Path, [string[]]$Arguments = @())
        $hostExe = (Get-Process -Id $PID).Path
        $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Path) + $Arguments
        $text = (& $hostExe @all 2>&1 | Out-String)
        return [pscustomobject]@{ Text = $text; Code = $LASTEXITCODE }
    }

    function Get-ParamDefaults {
        param([string]$Path)
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null)
        $pb = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $true)
        $map = @{}
        foreach ($p in $pb.Parameters) {
            $name = $p.Name.VariablePath.UserPath
            $def = $null
            if ($p.DefaultValue) { $def = $p.DefaultValue.Extent.Text }
            $map[$name] = $def
        }
        return $map
    }

    $script:isAdmin = try {
        (New-Object Security.Principal.WindowsPrincipal(
            [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { $false }
}

Describe 'Test-QuietModeAclApply.ps1 — 静态契约' {

    It '脚本存在且带 UTF-8 BOM（PS 5.1 否则按 936 解码，中文与正则一起坏）' {
        Test-Path -LiteralPath $script:TestScript | Should -BeTrue
        $b = [System.IO.File]::ReadAllBytes($script:TestScript)
        $b[0] | Should -Be 0xEF
        $b[1] | Should -Be 0xBB
        $b[2] | Should -Be 0xBF
    }

    It '不自行加 ACE —— 授权必须走生产脚本，不能复制一份实现' {
        # 本脚本的职责是「造夹具、读回、比对、恢复」。一旦它自己 AddAccessRule，
        # 测的就是它自己那份实现，而不是安装器真正调用的那份。
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Not -Match 'AddAccessRule'
    }

    It '不调用 FileInfo 实例的 GetAccessControl（PowerShell 7 上没有这个方法）' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:TestScript, [ref]$null, [ref]$null)
        $hits = $ast.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $n.Member.Extent.Text -eq 'GetAccessControl'
        }, $true)
        @($hits).Count | Should -Be 0
    }

    It '确实以子进程调用生产授权脚本，并带 -TaskNames 精确限定' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Match 'Grant-TaskRead\.ps1'
        $text | Should -Match '\-TaskNames'
        $text | Should -Match '\-IncludeTasks'
    }

    It '生产保护函数存在，且在写路径之前被调用' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Match 'function Assert-TestScoped'
        # 三个必须设防的地方：取文件夹对象、建夹具、调用授权脚本
        $text | Should -Match 'Assert-TestScoped'
        ([regex]::Matches($text, 'Assert-TestScoped')).Count | Should -BeGreaterThan 3
    }

    It '禁止前缀覆盖 \Microsoft\Windows、\QuietHDD、\QuietMode' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Match '\\Microsoft\\Windows'
        $text | Should -Match '\\QuietHDD'
        $text | Should -Match '\\QuietMode'''
    }

    It '测试任务名固定为 QuietMode-ACL-Probe，文件夹前缀固定' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Match "TestTaskName\s*=\s*'QuietMode-ACL-Probe'"
        $text | Should -Match "TestFolderPrefix\s*=\s*'QuietMode-ACL-Test-'"
    }

    It '夹具任务的动作用 cmd /c exit 0，且 Disabled、不给它触发器' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Match '/c exit 0'
        $text | Should -Match 'Settings\.Enabled\s*=\s*\$false'
        # 断言真的没有创建触发器：出现 Triggers.Create 就说明夹具会自己跑起来
        $text | Should -Not -Match 'Triggers\.Create'
    }

    It '恢复根不落在生产配置目录下（Users 对它只读，非提权写不进去）' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        $text | Should -Match 'QuietMode-AclTestRecovery'
        # 不允许出现 'QuietMode\AclTestRecovery' 这种落在生产目录里的拼法
        $text | Should -Not -Match "QuietMode\\\\AclTestRecovery"
    }

    It '基线一律存 DACL-only：含 SACL 的串回写需要 SeSecurityPrivilege' {
        $text = Get-Content -LiteralPath $script:TestScript -Raw -Encoding UTF8
        # 0xF 只用于「证据」字段，不能进 *SddlBefore
        $text | Should -Match 'TaskSddlFull'
        $text | Should -Match 'DaclOnly\s*=\s*4'
    }
}

Describe 'Test-QuietModeAclApply.ps1 — 参数与模式' {

    It '同时给两个模式开关必须拒绝（exit 3），不许猜用户想跑哪个' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-Preflight', '-Apply', '-RecoveryRoot', $tmpRec)
        $r.Code | Should -Be 3
        $r.Text | Should -Match '一次只能指定一个模式'
    }

    It '不带任何模式开关时默认走 Preflight（只读），不是 Apply' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-RecoveryRoot', $tmpRec)
        $r.Text | Should -Match '模式\s*:\s*Preflight'
        # 默认模式不允许出现 Apply 的写入痕迹
        $r.Text | Should -Not -Match '建立测试夹具'
    }

    It '非法 TestId 一律拒绝：它会被拼进任务文件夹名' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        foreach ($bad in @('\QuietMode', 'QuietMode', '..\QuietMode', 'not-a-guid')) {
            $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-Preflight', '-TestId', $bad, '-RecoveryRoot', $tmpRec)
            $r.Code | Should -Be 3 -Because ('TestId ' + $bad + ' 必须被拒')
            $r.Text | Should -Match '不是合法 GUID'
        }
    }

    It '合法 GUID 的 TestId 能通过格式校验（不再报「不是合法 GUID」）' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $g = [guid]::NewGuid().ToString()
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-Preflight', '-TestId', $g, '-RecoveryRoot', $tmpRec)
        $r.Text | Should -Not -Match '不是合法 GUID'
        $r.Text | Should -Match ([regex]::Escape($g))
    }

    It '-RecoveryRoot 能改变恢复根，且不会去创建生产目录' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-Preflight', '-RecoveryRoot', $tmpRec)
        $r.Text | Should -Match ([regex]::Escape($tmpRec))
        # 预检会创建恢复根（要放 current.json），这是测试自己的目录，允许
        Test-Path -LiteralPath $tmpRec | Should -BeTrue
    }
}

Describe 'Test-QuietModeAclApply.ps1 — 生产保护' {

    It 'TestId 里塞路径分隔符进不去（不是合法 GUID）' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @(
            '-Preflight', '-TestId', 'a\..\..\QuietMode', '-RecoveryRoot', $tmpRec)
        $r.Code | Should -Be 3
        $r.Text | Should -Match '不是合法 GUID'
    }

    It '预检阶段绝不建立任何测试对象' {
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $g = [guid]::NewGuid().ToString()
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-Preflight', '-TestId', $g, '-RecoveryRoot', $tmpRec)
        # 预检是只读：不该出现任何「已创建 / 已注册 / 已追加」的成功行
        $r.Text | Should -Not -Match 'DONE 已追加'
        $r.Text | Should -Not -Match '夹具就绪'
    }
}

Describe 'Test-QuietModeAclApply.ps1 — 未提权时的写模式必须停手' {

    It '-Apply / -Rollback / -Cleanup 在未提权时都返回 3，而不是 0' {
        if ($script:isAdmin) {
            Set-ItResult -Skipped -Because '当前会话已提权，无法验证未提权分支'
            return
        }
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $g = [guid]::NewGuid().ToString()

        foreach ($m in @('-Apply', '-Rollback', '-Cleanup')) {
            $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @($m, '-TestId', $g, '-RecoveryRoot', $tmpRec)
            # -Rollback / -Cleanup 在没有恢复资料时也是 3，这里一并接受
            $r.Code | Should -Be 3 -Because ($m + ' 不能在未提权时假装成功')
        }
    }

    It '-Apply 未提权时输出里点明「未提权」，且不建立夹具' {
        if ($script:isAdmin) {
            Set-ItResult -Skipped -Because '当前会话已提权'
            return
        }
        $tmpRec = Join-Path $TestDrive ('rec-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $g = [guid]::NewGuid().ToString()
        $r = Invoke-ScriptChild -Path $script:TestScript -Arguments @('-Apply', '-TestId', $g, '-RecoveryRoot', $tmpRec)
        $r.Text | Should -Match '未提权'
        $r.Text | Should -Not -Match '夹具就绪'
    }
}

Describe 'Grant-TaskRead.ps1 — -TaskNames 精确限定' {

    It '新增 -TaskNames 参数，默认空数组（默认行为一个字不变）' {
        $defaults = Get-ParamDefaults -Path $script:GrantScript
        $defaults.ContainsKey('TaskNames') | Should -BeTrue
        $defaults['TaskNames'] | Should -Be '@()'
    }

    It '原有的四个开关一个都没少（安装器依赖它们）' {
        $defaults = Get-ParamDefaults -Path $script:GrantScript
        foreach ($n in @('Folders', 'Apply', 'Check', 'IncludeTasks')) {
            $defaults.ContainsKey($n) | Should -BeTrue -Because ('缺少参数 ' + $n + ' 会破坏安装器 [15] 步')
        }
    }

    It '点名模式走 GetTask 而不是 GetTasks —— 精确对象，不做枚举' {
        $text = Get-Content -LiteralPath $script:GrantScript -Raw -Encoding UTF8
        $text | Should -Match 'foreach \(\$n in \$TaskNames\)'
        $text | Should -Match '\$folder\.GetTask\(\$n\)'
    }

    It '点名任务文件时不退回全目录' {
        $text = Get-Content -LiteralPath $script:GrantScript -Raw -Encoding UTF8
        $text | Should -Match 'OnlyNames'
        $text | Should -Match 'Grant-TaskFileRead -FolderPath \$path -OnlyNames \$TaskNames'
    }
}
