#Requires -Version 5.1
<#
    QuietMode.Config 路径与校验测试。

    注意配置层有两条分支：qmctl 可用时走 qmctl paths，不可用时本地拼装。两条
    分支的字段名大小写不同（qmctl JSON 是 camelCase，兜底是 PascalCase），但
    PowerShell 属性访问不区分大小写，所以测试统一用 Pascal 写法，顺带守住这个
    隐含契约。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Config.psm1') -Force
}

Describe 'Get-QuietModePaths' {

    It '返回全部八个路径字段' {
        $p = Get-QuietModePaths -Root $TestDrive
        foreach ($name in 'Root', 'Config', 'Backup', 'State', 'Baseline', 'Logs', 'Recovery', 'LegacyAppData') {
            $p.PSObject.Properties.Name | Should -Contain $name
        }
    }

    It '各字段都非空' {
        $p = Get-QuietModePaths -Root $TestDrive
        foreach ($name in 'Root', 'Config', 'Backup', 'State', 'Baseline', 'Logs', 'Recovery', 'LegacyAppData') {
            $p.$name | Should -Not -BeNullOrEmpty
        }
    }

    It 'Config / State / Baseline 都落在 Root 之下' {
        $p = Get-QuietModePaths -Root $TestDrive
        $root = ([System.IO.Path]::GetFullPath($p.Root)).TrimEnd('\')
        foreach ($name in 'Config', 'State', 'Baseline') {
            ([System.IO.Path]::GetFullPath($p.$name)).TrimEnd('\') |
                Should -BeLike "$root\*"
        }
    }

    It '文件名字段与字段语义一致' {
        $p = Get-QuietModePaths -Root $TestDrive
        (Split-Path $p.Config -Leaf) | Should -Be 'config.json'
        (Split-Path $p.State -Leaf) | Should -Be 'state.json'
        (Split-Path $p.Baseline -Leaf) | Should -Be 'baseline.json'
    }

    It 'Root 的大小写与拼写能对上传入值（不区分大小写比较）' {
        $p = Get-QuietModePaths -Root $TestDrive
        $p.Root.TrimEnd('\') | Should -Be $TestDrive.TrimEnd('\')
    }

    It '不传 Root 时退回 ProgramData\QuietMode' {
        $p = Get-QuietModePaths
        $p.Root.TrimEnd('\') | Should -BeLike '*ProgramData\QuietMode'
    }
}

Describe 'Test-QuietModeConfig' {

    It '对不存在的配置根：策略合法但退出码非 0（说明不是从文件读的）' {
        $empty = Join-Path $TestDrive 'no-config-here'
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        $r = Test-QuietModeConfig -Root $empty

        # qmctl validate 允许默认策略：Report 本身合法 → valid=true；
        # 但没有 config.json 落地，Usable=false → 退出码 2。
        # 这两个字段的含义不同，谁都不能单独当「已安装」的证据。
        $r.Valid | Should -BeTrue
        $r.ExitCode | Should -Be 2
        $r.ExitCode | Should -Not -Be 0
    }

    It '返回结构固定（Valid / Errors / Warnings / ExitCode）' {
        $r = Test-QuietModeConfig -Root $TestDrive
        foreach ($name in 'Valid', 'Errors', 'Warnings', 'ExitCode') {
            $r.PSObject.Properties.Name | Should -Contain $name
        }
        ($r.Errors -is [array]) | Should -BeTrue
        ($r.Warnings -is [array]) | Should -BeTrue
        $r.ExitCode | Should -BeOfType [int]
    }

    It '合法配置下 Errors 与 Warnings 都是空集合而不是 $null' {
        $r = Test-QuietModeConfig -Root $TestDrive
        @($r.Errors).Count | Should -Be 0
        @($r.Warnings).Count | Should -Be 0
    }
}
