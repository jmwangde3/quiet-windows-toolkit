#Requires -Version 5.1
<#
    QuietMode.Maintenance 纯函数测试。

    这里放的是回归锚点：ConvertFrom-QmTaskHResult 的三态判定曾经被语言相关的错误
    文案猜法坑过，267011（0x41303）也曾经显示成天书。下面每条断言都对应一个具体
    的历史故障点。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Maintenance.psm1') -Force
}

Describe 'ConvertFrom-QmTaskHResult' {

    It '低位 2 / 3 判为 NotPresent（任务不存在）' {
        ConvertFrom-QmTaskHResult 2 | Should -Be 'NotPresent'
        ConvertFrom-QmTaskHResult 3 | Should -Be 'NotPresent'
    }

    It '完整 HRESULT 形态也只看低位码（0x80070002 / 0x80070003）' {
        ConvertFrom-QmTaskHResult 0x80070002 | Should -Be 'NotPresent'
        ConvertFrom-QmTaskHResult 0x80070003 | Should -Be 'NotPresent'
    }

    It '拒绝访问（5 / 0x80070005）判为 Unknown，绝不能当成不存在' {
        ConvertFrom-QmTaskHResult 5 | Should -Be 'Unknown'
        ConvertFrom-QmTaskHResult 0x80070005 | Should -Be 'Unknown'
    }

    It '从未运行过的 267011（0x41303）判为 Unknown，不是 NotPresent' {
        ConvertFrom-QmTaskHResult 267011 | Should -Be 'Unknown'
    }

    It '成功码 0 判为 Unknown（本函数只解释失败码）' {
        ConvertFrom-QmTaskHResult 0 | Should -Be 'Unknown'
    }
}

Describe 'ConvertFrom-QmTaskStateCode' {

    It '1..4 按 TaskState 枚举翻译' {
        ConvertFrom-QmTaskStateCode 1 | Should -Be 'Disabled'
        ConvertFrom-QmTaskStateCode 2 | Should -Be 'Queued'
        ConvertFrom-QmTaskStateCode 3 | Should -Be 'Ready'
        ConvertFrom-QmTaskStateCode 4 | Should -Be 'Running'
    }

    It '越界值一律 Unknown，不抛异常' {
        ConvertFrom-QmTaskStateCode 0 | Should -Be 'Unknown'
        ConvertFrom-QmTaskStateCode 5 | Should -Be 'Unknown'
        ConvertFrom-QmTaskStateCode 99 | Should -Be 'Unknown'
    }

    It '接受字符串形态（qmctl JSON 里的 state 有时是字符串）' {
        ConvertFrom-QmTaskStateCode '3' | Should -Be 'Ready'
    }
}

Describe 'ConvertTo-QmRegistryValue' {

    It 'DWord 转 int，QWord 转 long' {
        InModuleScope QuietMode.Maintenance {
            $d = ConvertTo-QmRegistryValue '2' 'DWord'
            $d | Should -Be 2
            $d.GetType().Name | Should -Be 'Int32'

            $q = ConvertTo-QmRegistryValue '10' 'QWord'
            $q | Should -Be 10
            $q.GetType().Name | Should -Be 'Int64'
        }
    }

    It 'MultiString 按换行拆分，空串得到空数组' {
        InModuleScope QuietMode.Maintenance {
            @(ConvertTo-QmRegistryValue "a`nb" 'MultiString').Count | Should -Be 2
            @(ConvertTo-QmRegistryValue "a`nb" 'MultiString')[1] | Should -Be 'b'
            @(ConvertTo-QmRegistryValue '' 'MultiString').Count | Should -Be 0
        }
    }

    It 'Binary 按十六进制成对解析' {
        InModuleScope QuietMode.Maintenance {
            $b = ConvertTo-QmRegistryValue '4b000000' 'Binary'
            ($b -join ',') | Should -Be '75,0,0,0'
            $b.Length | Should -Be 4
        }
    }

    It 'Binary 空串得到空 byte[]' {
        InModuleScope QuietMode.Maintenance {
            $b = ConvertTo-QmRegistryValue '' 'Binary'
            $b.Length | Should -Be 0
        }
    }

    It '未知 Kind 退回字符串' {
        InModuleScope QuietMode.Maintenance {
            ConvertTo-QmRegistryValue 'hello' 'String' | Should -Be 'hello'
            ConvertTo-QmRegistryValue 'hello' '' | Should -Be 'hello'
        }
    }
}

Describe 'Format-QmRegistryValue' {

    It '$null 一律成空串' {
        InModuleScope QuietMode.Maintenance {
            Format-QmRegistryValue $null 'DWord' | Should -Be ''
            Format-QmRegistryValue $null 'Binary' | Should -Be ''
        }
    }

    It 'Binary 输出小写十六进制且无分隔符' {
        InModuleScope QuietMode.Maintenance {
            Format-QmRegistryValue ([byte[]](1, 2, 255)) 'Binary' | Should -Be '0102ff'
        }
    }

    It 'MultiString 用换行拼回' {
        InModuleScope QuietMode.Maintenance {
            Format-QmRegistryValue @('x', 'y') 'MultiString' | Should -Be "x`ny"
        }
    }

    It '数字按十进制字符串输出' {
        InModuleScope QuietMode.Maintenance {
            Format-QmRegistryValue 1200 'DWord' | Should -Be '1200'
        }
    }
}

Describe 'Get-QmRegistryPsPath' {

    It '裸路径补 HKLM: 前缀' {
        InModuleScope QuietMode.Maintenance {
            Get-QmRegistryPsPath 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' |
                Should -Be 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'
        }
    }

    It '以反斜杠开头的路径只剥一个反斜杠' {
        InModuleScope QuietMode.Maintenance {
            Get-QmRegistryPsPath '\SOFTWARE\X' | Should -Be 'HKLM:\SOFTWARE\X'
        }
    }

    It '已带 PSDrive 或 HKEY 全名的路径原样返回' {
        InModuleScope QuietMode.Maintenance {
            Get-QmRegistryPsPath 'HKLM:\SOFTWARE\X' | Should -Be 'HKLM:\SOFTWARE\X'
            Get-QmRegistryPsPath 'HKCU:\X' | Should -Be 'HKCU:\X'
            Get-QmRegistryPsPath 'HKEY_LOCAL_MACHINE\X' | Should -Be 'HKEY_LOCAL_MACHINE\X'
            Get-QmRegistryPsPath 'HKEY_CURRENT_USER\X' | Should -Be 'HKEY_CURRENT_USER\X'
        }
    }
}

Describe 'Test-QmElevated' {

    It '返回布尔值，且与当前进程身份一致' {
        $r = Test-QmElevated
        $r | Should -BeOfType [bool]

        $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object System.Security.Principal.WindowsPrincipal($id)
        $expect = $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
        $r | Should -Be $expect
    }
}
