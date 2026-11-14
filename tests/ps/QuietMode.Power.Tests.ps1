#Requires -Version 5.1
<#
    QuietMode.Power 的 DISKIDLE 只读探测测试。

    Get-QmDiskIdleSeconds 有两条读路径（powercfg → 注册表兜底），顺序不能颠倒，
    取到的也必须是「当前 AC / 当前 DC」这两个尾部数值而不是最小/最大/增量。
    下面用 powercfg 的原始输出做交叉验证，钉住解析顺序。
    这是环境相关测试：任何一台有电源方案的 Windows 都能跑，但不断言具体秒数。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Power.psm1') -Force
    $script:Reading = Get-QmDiskIdleSeconds
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

        $text = (& powercfg.exe /query SCHEME_CURRENT SUB_DISK DISKIDLE 2>$null | Out-String)
        $values = @()
        foreach ($m in [regex]::Matches($text, '0x([0-9a-fA-F]{1,8})')) {
            $values += [Convert]::ToInt32($m.Groups[1].Value, 16)
        }
        $values.Count | Should -BeGreaterOrEqual 2

        $script:Reading.ac | Should -Be $values[$values.Count - 2]
        $script:Reading.dc | Should -Be $values[$values.Count - 1]
    }

    It '两次调用结果稳定（只读，无副作用）' {
        $again = Get-QmDiskIdleSeconds
        $again.ac | Should -Be $script:Reading.ac
        $again.dc | Should -Be $script:Reading.dc
        $again.captured | Should -Be $script:Reading.captured
    }
}
