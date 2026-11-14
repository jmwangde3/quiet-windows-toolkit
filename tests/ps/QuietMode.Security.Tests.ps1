#Requires -Version 5.1
<#
    QuietMode.Security 路径安全测试。

    Resolve-QmSafeChild 是唯一挡住「相对名字写出基目录」的函数，它的返回 $null
    就等于拒绝。下面把每一种逃逸形态都钉死，避免以后有人为了「支持子目录」放宽它。
#>

BeforeAll {
    $script:ModulesRoot = Join-Path $PSScriptRoot '..\..\powershell\Modules'
    Import-Module (Join-Path $script:ModulesRoot 'QuietMode.Security.psm1') -Force
}

Describe 'Resolve-QmSafeChild' {

    It '普通文件名拼到基目录下' {
        Resolve-QmSafeChild 'C:\base' 'file.txt' | Should -Be 'C:\base\file.txt'
    }

    It '基目录带尾部反斜杠时不多出一个反斜杠' {
        Resolve-QmSafeChild 'C:\base\' 'file.txt' | Should -Be 'C:\base\file.txt'
    }

    It '拒绝 .. 与 .' {
        Resolve-QmSafeChild 'C:\base' '..' | Should -BeNullOrEmpty
        Resolve-QmSafeChild 'C:\base' '.' | Should -BeNullOrEmpty
    }

    It '拒绝任何含分隔符的名字（含 ..\evil）' {
        Resolve-QmSafeChild 'C:\base' '..\evil.txt' | Should -BeNullOrEmpty
        Resolve-QmSafeChild 'C:\base' 'sub/file.txt' | Should -BeNullOrEmpty
        Resolve-QmSafeChild 'C:\base' 'sub\file.txt' | Should -BeNullOrEmpty
    }

    It '拒绝绝对路径与盘符前缀' {
        Resolve-QmSafeChild 'C:\base' 'C:\Windows\System32\cmd.exe' | Should -BeNullOrEmpty
        Resolve-QmSafeChild 'C:\base' 'D:file.txt' | Should -BeNullOrEmpty
    }

    It '拒绝任何含冒号的名字（NTFS 备用数据流）' {
        Resolve-QmSafeChild 'C:\base' 'file.txt:hidden' | Should -BeNullOrEmpty
    }

    It '基目录或名字为空时返回 $null' {
        Resolve-QmSafeChild '' 'file.txt' | Should -BeNullOrEmpty
        Resolve-QmSafeChild 'C:\base' '' | Should -BeNullOrEmpty
        Resolve-QmSafeChild $null $null | Should -BeNullOrEmpty
    }

    It '结果必须落在基目录内（前缀校验，不受大小写影响）' {
        $r = Resolve-QmSafeChild 'C:\base' 'note.md'
        $r.StartsWith('C:\base\', [System.StringComparison]::OrdinalIgnoreCase) | Should -BeTrue
    }
}

Describe 'Test-QmReparsePoint' {

    It '路径不存在时返回 false，不抛异常' {
        Test-QmReparsePoint (Join-Path $TestDrive 'never-existed') | Should -BeFalse
    }

    It '空路径返回 false' {
        Test-QmReparsePoint '' | Should -BeFalse
        Test-QmReparsePoint $null | Should -BeFalse
    }

    It '普通目录返回 false' {
        $d = Join-Path $TestDrive 'plain'
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Test-QmReparsePoint $d | Should -BeFalse
    }

    It '普通文件返回 false' {
        $f = Join-Path $TestDrive 'plain.txt'
        Set-Content -LiteralPath $f -Value 'x' -Encoding ASCII
        Test-QmReparsePoint $f | Should -BeFalse
    }

    It '真实 junction 返回 true' {
        $target = Join-Path $TestDrive 'jt-target'
        $link = Join-Path $TestDrive 'jt-link'
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        New-Item -ItemType Junction -Path $link -Target $target -Force | Out-Null
        Test-QmReparsePoint $link | Should -BeTrue
    }
}

Describe 'Get-QmPrivilegedPaths' {

    It '只给 Root 时覆盖 Root 本体、三个配置文件与两个子目录' {
        $paths = @(Get-QmPrivilegedPaths -Root 'C:\qd')
        $paths.Count | Should -Be 6
        $paths | Should -Contain 'C:\qd'
        $paths | Should -Contain 'C:\qd\config.json'
        $paths | Should -Contain 'C:\qd\state.json'
        $paths | Should -Contain 'C:\qd\baseline.json'
        $paths | Should -Contain 'C:\qd\run'
        $paths | Should -Contain 'C:\qd\recovery'
    }

    It '只给 DeployDir 时包含部署目录自身（删文件靠的是父目录权限）' {
        $paths = @(Get-QmPrivilegedPaths -DeployDir 'C:\dep')
        $paths.Count | Should -Be 6
        $paths | Should -Contain 'C:\dep'
        $paths | Should -Contain 'C:\dep\powershell'
        $paths | Should -Contain 'C:\dep\qmctl.exe'
        $paths | Should -Contain 'C:\dep\QuietMode.exe'
        $paths | Should -Contain 'C:\dep\QuietMode.Core.dll'
        $paths | Should -Contain 'C:\dep\QuietMode.Windows.dll'
    }

    It '两者都给时不重复也不遗漏（6 + 6）' {
        $paths = @(Get-QmPrivilegedPaths -Root 'C:\qd' -DeployDir 'C:\dep')
        $paths.Count | Should -Be 12
        ($paths | Sort-Object -Unique).Count | Should -Be 12
    }

    It '都不给时返回空集合' {
        @(Get-QmPrivilegedPaths).Count | Should -Be 0
    }
}
