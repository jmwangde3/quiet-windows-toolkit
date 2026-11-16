#Requires -Version 5.1
<#
    QuietMode.Power 的 DISKIDLE 只读探测与参数构造测试。

    Get-QmDiskIdleSeconds 有两条读路径（powercfg → 注册表兜底），顺序不能颠倒，
    取到的也必须是「当前 AC / 当前 DC」这两个尾部数值而不是最小/最大/增量。
    下面用 powercfg 的原始输出做交叉验证，钉住解析顺序。
    这是环境相关测试：任何一台有电源方案的 Windows 都能跑，但不断言具体秒数。

    2026-11-16 新增「参数构造」这一组：v2.0.5 把
    "/setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 1200" 整串当成一个参数传，
    powercfg 一律返回「参数无效」+ 退出码 1，而 2>$null 把证据吞了。
    这组测试的判据只有一条：任何一个参数里都不许出现空白字符。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Power.psm1') -Force
    $script:Mod = Get-Module QuietMode.Power
    $script:Reading = Get-QmDiskIdleSeconds
    $script:Active = Get-QmActivePowerScheme
}

Describe 'powercfg 参数构造' {

    It '每个参数都是独立 token，参数内不含任何空白' {
        foreach ($kind in 'setac', 'setdc', 'setactive', 'query', 'getactive') {
            $args = & $script:Mod {
                param($k, $g)
                New-QmPowerCfgArgumentSet -Kind $k -SchemeGuid $g -Seconds 1200
            } $kind '381b4222-f694-41f0-9685-ff5bb260df2e'

            $flat = @($args)
            $dirty = @($flat | Where-Object { [string]$_ -match '\s' })
            $dirty.Count | Should -Be 0 -Because "$kind 的参数被拼成了一整串（v2.0.5 缺陷回归）"
        }
    }

    It 'setac / setdc 是五个参数，动作与数值各占一格' {
        $ac = @(& $script:Mod { New-QmPowerCfgArgumentSet -Kind setac -SchemeGuid '381b4222-f694-41f0-9685-ff5bb260df2e' -Seconds 1200 })
        $ac.Count | Should -Be 5
        $ac[0] | Should -Be '/setacvalueindex'
        $ac[1] | Should -Be '381b4222-f694-41f0-9685-ff5bb260df2e'
        $ac[4] | Should -Be '1200'

        $dc = @(& $script:Mod { New-QmPowerCfgArgumentSet -Kind setdc -SchemeGuid '381b4222-f694-41f0-9685-ff5bb260df2e' -Seconds 1200 })
        $dc.Count | Should -Be 5
        $dc[0] | Should -Be '/setdcvalueindex'
    }

    It '不再使用 SCHEME_CURRENT 别名，而是真实 GUID' {
        $ac = @(& $script:Mod { New-QmPowerCfgArgumentSet -Kind setac -SchemeGuid '381b4222-f694-41f0-9685-ff5bb260df2e' -Seconds 1200 })
        $ac | Should -Not -Contain 'SCHEME_CURRENT'
        $ac[1] | Should -Match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    }

    It 'setactive 只带一个 GUID' {
        $sa = @(& $script:Mod { New-QmPowerCfgArgumentSet -Kind setactive -SchemeGuid '381b4222-f694-41f0-9685-ff5bb260df2e' })
        $sa.Count | Should -Be 2
        $sa[0] | Should -Be '/setactive'
    }
}

Describe 'Get-QmActivePowerScheme' {

    It '取到一个合法 GUID' {
        $script:Active.guid | Should -Match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    }

    It '与 HKLM 的 ActivePowerScheme 一致' {
        if (-not $script:Active.registryGuid) {
            Set-ItResult -Skipped -Because '注册表里没有 ActivePowerScheme'
            return
        }
        $script:Active.registryGuid | Should -Be $script:Active.guid
    }

    It '带复探时能确认方案稳定' {
        $s = Get-QmActivePowerScheme -ConfirmDelayMs 300
        $s.consistent | Should -BeTrue
        $s.guid | Should -Be $script:Active.guid
    }
}

Describe 'Get-QmDiskIdleSeconds' {

    It '返回固定五个字段' {
        foreach ($name in 'ac', 'dc', 'captured', 'source', 'error') {
            $script:Reading.Keys | Should -Contain $name
        }
    }

    It 'ac / dc 是整数，未读到时为 -1 而不是 $null' {
        $script:Reading.ac | Should -BeOfType [int]
        $script:Reading.dc | Should -BeOfType [int]
        $script:Reading.ac | Should -BeGreaterOrEqual -1
        $script:Reading.dc | Should -BeGreaterOrEqual -1
    }

    It 'captured 是布尔，source 是已知来源' {
        $script:Reading.captured | Should -BeOfType [bool]
        if ($script:Reading.captured) {
            $script:Reading.source | Should -BeIn @('powercfg', 'registry')
        }
    }

    It '捕获成功时 AC / DC 都是非负秒数' {
        if ($script:Reading.captured) {
            $script:Reading.ac | Should -BeGreaterOrEqual 0
            $script:Reading.dc | Should -BeGreaterOrEqual 0
        }
    }

    It '与 powercfg 原始输出的尾部两个数值一致（AC 在倒数第二，DC 在最后）' {
        if ($script:Reading.source -ne 'powercfg') {
            Set-ItResult -Skipped -Because "本次读数来自 $($script:Reading.source)，无法用 powercfg 交叉验证"
            return
        }

        # 这里必须用模块自己的参数构造，而不是手写命令：
        # 手写就等于绕过生产路径，测出来的东西不能证明线上调用是对的。
        $qArgs = @(& $script:Mod {
            param($g)
            New-QmPowerCfgArgumentSet -Kind query -SchemeGuid $g
        } $script:Active.guid)
        $qArgs.Count | Should -BeGreaterOrEqual 3

        $text = (& powercfg.exe @qArgs 2>$null | Out-String)
        $values = @()
        foreach ($m in [regex]::Matches($text, '0x([0-9a-fA-F]{1,8})')) {
            $values += [Convert]::ToInt32($m.Groups[1].Value, 16)
        }
        $values.Count | Should -BeGreaterOrEqual 2

        $script:Reading.ac | Should -Be $values[$values.Count - 2]
        $script:Reading.dc | Should -Be $values[$values.Count - 1]
    }

    It '按 GUID 读与按当前方案读结果相同' {
        if (-not $script:Active.guid) {
            Set-ItResult -Skipped -Because '取不到活动方案 GUID'
            return
        }
        $byGuid = Get-QmDiskIdleSeconds -SchemeGuid $script:Active.guid
        $byGuid.ac | Should -Be $script:Reading.ac
        $byGuid.dc | Should -Be $script:Reading.dc
    }

    It '两次调用结果稳定（只读，无副作用）' {
        $again = Get-QmDiskIdleSeconds
        $again.ac | Should -Be $script:Reading.ac
        $again.dc | Should -Be $script:Reading.dc
        $again.captured | Should -Be $script:Reading.captured
    }

    It 'acCaptured / dcCaptured 逐侧标记，不能拿整体 captured 代替' {
        foreach ($name in 'acCaptured', 'dcCaptured') {
            $script:Reading.Keys | Should -Contain $name
            $script:Reading[$name] | Should -BeOfType [bool]
        }
        # 一个值被标记为 captured，当且仅当它不是 -1 哨兵
        $script:Reading.acCaptured | Should -Be ($script:Reading.ac -ge 0)
        $script:Reading.dcCaptured | Should -Be ($script:Reading.dc -ge 0)
    }
}

Describe 'GUID 规范化' {

    It '接受小写 D 格式' {
        $g = & $script:Mod { param($v) ConvertTo-QmPowerSchemeGuid -Guid $v } '381b4222-f694-41f0-9685-ff5bb260df2e'
        $g | Should -Be '381b4222-f694-41f0-9685-ff5bb260df2e'
    }

    It '剥掉花括号并统一小写' {
        $g = & $script:Mod { param($v) ConvertTo-QmPowerSchemeGuid -Guid $v } '{381B4222-F694-41F0-9685-FF5BB260DF2E}'
        $g | Should -Be '381b4222-f694-41f0-9685-ff5bb260df2e'
    }

    It '容忍首尾空白' {
        $g = & $script:Mod { param($v) ConvertTo-QmPowerSchemeGuid -Guid $v } '  381b4222-f694-41f0-9685-ff5bb260df2e  '
        $g | Should -Be '381b4222-f694-41f0-9685-ff5bb260df2e'
    }

    It '非法输入返回空串（Trim 剥括号不是验证）' {
        foreach ($bad in '', '   ', 'not-a-guid', '{381b4222}', '..\..\evil', '381b4222-f694-41f0-9685-ff5bb260df2e\..\x') {
            $g = & $script:Mod { param($v) ConvertTo-QmPowerSchemeGuid -Guid $v } $bad
            $g | Should -Be '' -Because "「$bad」不是合法 GUID，必须当成没有证据"
        }
    }
}

Describe 'Get-QmActivePowerScheme 的 stability 语义' {

    It '返回 stable 字段' {
        $script:Active.Keys | Should -Contain 'stable'
        $script:Active.stable | Should -BeOfType [bool]
    }

    It '不做复探时 stable 默认为 true（只代表读到了）' {
        $s = Get-QmActivePowerScheme
        if (-not $s.guid) { Set-ItResult -Skipped -Because '取不到活动方案'; return }
        $s.stable | Should -BeTrue
    }

    It '复探一致时 stable 为 true' {
        $s = Get-QmActivePowerScheme -ConfirmDelayMs 300
        if (-not $s.guid) { Set-ItResult -Skipped -Because '取不到活动方案'; return }
        $s.consistent | Should -BeTrue
        $s.stable | Should -BeTrue
    }

    It 'registryMatch 是真实比较结果，不是采而不判' {
        $script:Active.Keys | Should -Contain 'registryMatch'
        if ($script:Active.guid -and $script:Active.registryGuid) {
            $script:Active.registryMatch | Should -Be ($script:Active.guid -eq $script:Active.registryGuid)
        }
    }
}

Describe 'Invoke-QmSpinDown 的入参校验' {

    It '负数秒数直接拒绝，不静默变成 0（0 = 永不停止，语义更危险）' {
        $r = Invoke-QmSpinDown -Seconds -1
        $r.status | Should -Be 'Failed'
        $r.code | Should -Be 'INVALID_SECONDS'
        $r.error | Should -Match '不能为负数'
    }

    It '拒绝发生在提权判定之前，所以普通权限也能得到明确错误' {
        $r = Invoke-QmSpinDown -Seconds -1200
        $r.code | Should -Not -Be 'ELEVATION_REQUIRED'
    }

    It '非管理员调用合法秒数时返回 ELEVATION_REQUIRED 且不写系统' {
        if ([bool]((New-Object Security.Principal.WindowsPrincipal(
                [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator))) {
            Set-ItResult -Skipped -Because '当前会话已提权，无法验证拒绝路径'
            return
        }
        $r = Invoke-QmSpinDown -Seconds 1200
        $r.status | Should -Be 'Failed'
        $r.code | Should -Be 'ELEVATION_REQUIRED'
        $r.changed | Should -BeFalse
    }
}
