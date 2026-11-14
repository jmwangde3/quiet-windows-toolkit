#Requires -Version 5.1
<#
    QuietMode.Disk 纯函数测试。

    选取原则：只测不碰硬件的确定性逻辑。所有断言都能在任何一台 Windows 上复现，
    不使用真实磁盘编号、不依赖管理员、不读 WMI。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Disk.psm1') -Force
}

Describe 'Format-QmSize' {

    It '把非正数折成 0' {
        Format-QmSize 0 | Should -Be '0'
        Format-QmSize (-1) | Should -Be '0'
        Format-QmSize ([long]::MinValue) | Should -Be '0'
    }

    It '小于 1 KB 时用整数 B，不带小数' {
        Format-QmSize 1 | Should -Be '1 B'
        Format-QmSize 512 | Should -Be '512 B'
        Format-QmSize 1023 | Should -Be '1023 B'
    }

    It '在 1024 处进位到 KB' {
        Format-QmSize 1024 | Should -Be '1 KB'
        Format-QmSize 1536 | Should -Be '1.5 KB'
    }

    It '逐级进位到 MB / GB / TB' {
        Format-QmSize 1048576 | Should -Be '1 MB'
        Format-QmSize 1073741824 | Should -Be '1 GB'
        Format-QmSize 1099511627776 | Should -Be '1 TB'
    }

    It '真实盘容量保留两位小数（WUH721414ALE6L4 = 14000519643136 B）' {
        Format-QmSize 14000519643136 | Should -Be '12.73 TB'
    }

    It '超过 PB 不再往上层跑（单位表封顶）' {
        Format-QmSize 1125899906842624 | Should -Be '1 PB'
        # 1 EB 会停在 PB 层并继续放大数值，而不是抛异常
        { Format-QmSize 1152921504606846976 } | Should -Not -Throw
    }
}

Describe 'ConvertTo-QmId' {

    It '去空白并转大写' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmId '  5000cc2a8fc58a96  ' | Should -Be '5000CC2A8FC58A96'
        }
    }

    It '空值与纯空白返回空串，不返回 $null' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmId '' | Should -Be ''
            ConvertTo-QmId '   ' | Should -Be ''
            ConvertTo-QmId $null | Should -Be ''
        }
    }
}

Describe 'ConvertTo-QmSerial' {

    It '去掉所有空白与 NUL' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmSerial "  ZG Y8`0D5WD " | Should -Be 'ZGY8D5WD'
        }
    }

    It '保留连字符与下划线（NVMe 的 eui.* 形态）' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmSerial 'E823_8FA6_BF53_0001.001B.444A.465B.100C.' |
                Should -Be 'E823_8FA6_BF53_0001.001B.444A.465B.100C.'
        }
    }

    It '空值返回空串' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmSerial '' | Should -Be ''
            ConvertTo-QmSerial $null | Should -Be ''
        }
    }
}

Describe 'ConvertTo-QmName' {

    It '把 NUL 换成空格并压掉重复空白' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmName "WDC  WUH`0L721414ALE6L4" | Should -Be 'WDC WUH L721414ALE6L4'
        }
    }

    It '去掉首尾空白' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmName "  ST4000VX000-2AG166  " | Should -Be 'ST4000VX000-2AG166'
        }
    }

    It '空值返回空串' {
        InModuleScope QuietMode.Disk {
            ConvertTo-QmName '' | Should -Be ''
            ConvertTo-QmName $null | Should -Be ''
        }
    }
}
