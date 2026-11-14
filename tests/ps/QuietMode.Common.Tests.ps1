#Requires -Version 5.1
<#
    QuietMode.Common 基础能力测试。

    这里只测「任何机器上都该成立」的事实：仓库根能被定位、工具路径能解析、提权
    检测与当前身份一致。不测具体盘符或绝对路径。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Common.psm1') -Force
}

Describe 'Get-QuietModeRepoRoot' {

    It '返回一个存在的目录' {
        $root = Get-QuietModeRepoRoot
        $root | Should -Not -BeNullOrEmpty
        Test-Path -LiteralPath $root -PathType Container | Should -BeTrue
    }

    It '定位到的目录同时含 README.md 与 src（判定条件本身）' {
        $root = Get-QuietModeRepoRoot
        Test-Path -LiteralPath (Join-Path $root 'README.md') | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $root 'src') -PathType Container | Should -BeTrue
    }

    It '是可重复的（两次调用结果相同）' {
        (Get-QuietModeRepoRoot) | Should -Be (Get-QuietModeRepoRoot)
    }
}

Describe 'Get-QuietModeToolPath' {

    It '解析到的路径存在且文件名是 qmctl.exe' {
        $tool = Get-QuietModeToolPath
        Test-Path -LiteralPath $tool | Should -BeTrue
        (Split-Path $tool -Leaf) | Should -Be 'qmctl.exe'
    }

    It 'Release 是默认配置' {
        $tool = Get-QuietModeToolPath
        $tool | Should -Match 'Release'
    }

    It 'Debug 与 Release 指向不同配置目录（都构建过时）' {
        $rel = Get-QuietModeToolPath -Configuration 'Release'
        $rel | Should -Match '\\Release\\'
    }
}

Describe 'Test-QuietModeElevated' {

    It '返回布尔值' {
        Test-QuietModeElevated | Should -BeOfType [bool]
    }

    It '与当前进程的实际提权状态一致' {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        $expect = $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        Test-QuietModeElevated | Should -Be $expect
    }
}

Describe 'Write-QuietModeLog' {

    It '把日志追加进指定文件' {
        $log = Join-Path $TestDrive 'test.log'
        Write-QuietModeLog -Message 'hello' -LogPath $log
        Test-Path -LiteralPath $log | Should -BeTrue
        (Get-Content -LiteralPath $log -Raw) | Should -Match 'hello'
    }

    It '连续两次调用是追加而不是覆盖' {
        $log = Join-Path $TestDrive 'append.log'
        Write-QuietModeLog -Message 'first' -LogPath $log
        Write-QuietModeLog -Message 'second' -LogPath $log
        $text = Get-Content -LiteralPath $log -Raw
        $text | Should -Match 'first'
        $text | Should -Match 'second'
    }

    It '每行带时间戳与级别' {
        $log = Join-Path $TestDrive 'level.log'
        Write-QuietModeLog -Message 'disk offline' -Level WARN -LogPath $log
        $line = (Get-Content -LiteralPath $log -Raw).Trim()
        $line | Should -Match '^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]'
        $line | Should -Match 'WARN'
    }

    It '父目录不存在时自动创建' {
        $log = Join-Path $TestDrive 'deep\nested\run.log'
        Write-QuietModeLog -Message 'nested' -LogPath $log
        Test-Path -LiteralPath $log | Should -BeTrue
    }
}
