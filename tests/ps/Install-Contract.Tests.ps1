#Requires -Version 5.1
<#
    安装器与仓库级契约测试。

    这里不测业务逻辑（那在各模块的用例里），只钉三类「一旦破坏就很难发现」的东西：

      1. 语法 —— powershell/ 与 tests/ 下每个 .ps1 / .psm1 都能被当前引擎解析。
         PS 5.1 与 PS 7.7.0 各跑一次同一批用例（CI 双 job），
         等价于一次覆盖两个版本解析器的静态检查。
      2. 编码 —— 含中文的脚本必须是 UTF-8 with BOM，
         否则 PS 5.1 会按 936 解码，中文字符串与正则一起坏掉。
      3. 版本 —— 产品版本只有一个来源，代码常量与程序集属性必须一致。
#>

BeforeAll {
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $script:PsRoot   = Join-Path $script:RepoRoot 'powershell'
    $script:BuildRoot = Join-Path $script:RepoRoot 'build'
    $script:Install  = Join-Path $script:PsRoot 'Install-QuietMode.ps1'
    $script:CfgCs    = Join-Path $script:RepoRoot 'src\QuietMode.Core\Models\QuietModeConfig.cs'
    $script:TrayProj = Join-Path $script:RepoRoot 'QuietMode\build\QuietMode.csproj'
}

Describe '语法：当前 PowerShell 引擎能解析全部脚本' {

    It 'powershell\ 、tests\ps\ 与 build\ 下的每个 .ps1 / .psm1 都无语法错误' {
        # 注意：-LiteralPath 与 -Include 组合在 PowerShell 5.1 里 -Include 会被忽略，
        # 会把 .cmd 也一起解析进来。所以这里显式按扩展名过滤。
        $files = @()
        $files += Get-ChildItem -LiteralPath $script:PsRoot -Recurse -File |
            Where-Object { $_.Extension -in '.ps1', '.psm1' }
        $files += Get-ChildItem -LiteralPath (Join-Path $script:RepoRoot 'tests\ps') -File |
            Where-Object { $_.Extension -eq '.ps1' }
        # build\ 也要扫：pack-release.ps1 第一版就是因为没 BOM 被 PS 5.1 按 936 解码、
        # 中文字符串把语法撕坏了。构建脚本坏了同样是发版事故。
        $files += Get-ChildItem -LiteralPath $script:BuildRoot -File |
            Where-Object { $_.Extension -eq '.ps1' }

        $files.Count | Should -BeGreaterThan 10

        $bad = @()
        foreach ($f in $files) {
            $errs = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errs)
            if ($errs -and $errs.Count -gt 0) {
                foreach ($e in $errs) {
                    $bad += ('{0}:{1} {2}' -f $f.Name, $e.Extent.StartLineNumber, $e.Message)
                }
            }
        }
        $bad -join "`n" | Should -BeNullOrEmpty -Because ("引擎 " + $PSVersionTable.PSVersion + " 解析失败")
    }

    It '仓库根下的 v1 遗留脚本也能被解析（它们是历史兼容资产，不是垃圾）' {
        $v1 = Get-ChildItem -LiteralPath $script:RepoRoot -File -Filter 'quiet-hdd-*.ps1'
        $v1.Count | Should -BeGreaterThan 0
        foreach ($f in $v1) {
            $errs = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errs)
            ($errs -eq $null -or $errs.Count -eq 0) | Should -BeTrue -Because ($f.Name + ' 解析失败')
        }
    }
}

Describe '编码：含中文的脚本必须是 UTF-8 with BOM' {

    It 'powershell\ 与 build\ 下每个 .ps1 / .psm1 前三字节是 EF BB BF' {
        $files = @()
        $files += Get-ChildItem -LiteralPath $script:PsRoot -Recurse -File |
            Where-Object { $_.Extension -in '.ps1', '.psm1' }
        $files += Get-ChildItem -LiteralPath $script:BuildRoot -File |
            Where-Object { $_.Extension -in '.ps1', '.psm1' }
        $bad = @()
        foreach ($f in $files) {
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
                $bad += $f.Name
            }
        }
        $bad -join ', ' | Should -BeNullOrEmpty -Because 'PS 5.1 没有 BOM 会按 936 解码，中文字符串与正则一起坏'
    }

    It 'tests\ps 下每个 .ps1 也带 BOM' {
        $files = Get-ChildItem -LiteralPath (Join-Path $script:RepoRoot 'tests\ps') -File |
            Where-Object { $_.Extension -eq '.ps1' }
        $bad = @()
        foreach ($f in $files) {
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
                $bad += $f.Name
            }
        }
        $bad -join ', ' | Should -BeNullOrEmpty
    }

    It '.cmd 文件不带 BOM（cmd.exe 会把 BOM 当成命令的一部分）' {
        $files = Get-ChildItem -LiteralPath $script:PsRoot -Recurse -File |
            Where-Object { $_.Extension -in '.cmd', '.bat' }
        foreach ($f in $files) {
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) |
                Should -BeFalse -Because ($f.Name + ' 不该带 BOM')
        }
    }
}

Describe '版本号只有一个来源' {

    It 'QuietModeConfig.Version 是语义化三段版本' {
        $text = Get-Content -LiteralPath $script:CfgCs -Raw -Encoding UTF8
        $m = [regex]::Match($text, 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
        $m.Success | Should -BeTrue
        $m.Groups[1].Value | Should -Match '^\d+\.\d+\.\d+$'
    }

    It '托盘 csproj 的 FileVersion 主次版本与代码常量一致' {
        $text = Get-Content -LiteralPath $script:TrayProj -Raw -Encoding UTF8
        $mv = [regex]::Match((Get-Content -LiteralPath $script:CfgCs -Raw -Encoding UTF8), 'Version\s*=\s*"([^"]+)"')
        $mf = [regex]::Match($text, '<FileVersion>([^<]+)</FileVersion>')
        $mfs = [regex]::Match($text, '<FileVersionSuffix>([^<]*)</FileVersionSuffix>')

        $productVersion = $mv.Groups[1].Value
        $fileVersion = $mf.Groups[1].Value
        $fileVersion | Should -Not -BeNullOrEmpty

        $parts = $productVersion.Split('.')
        $fileVersion.StartsWith(($parts[0] + '.' + $parts[1] + '.' + $parts[2])) |
            Should -BeTrue -Because ("FileVersion " + $fileVersion + " 应与产品版本 " + $productVersion + " 同主次修订")
    }

    It 'AssemblyVersion 不跟着产品版本走（程序集标识只在破坏兼容时才改）' {
        $text = Get-Content -LiteralPath $script:TrayProj -Raw -Encoding UTF8
        $m = [regex]::Match($text, '<AssemblyVersion>([^<]+)</AssemblyVersion>')
        $m.Success | Should -BeTrue
        $m.Groups[1].Value | Should -Match '^\d+\.\d+\.\d+\.\d+$'
    }
}

Describe '安装器契约' {

    BeforeAll {
        $script:InstallText = Get-Content -LiteralPath $script:Install -Raw -Encoding UTF8
    }

    It '任务状态判据只有一个实现：安装器通过模块取状态' {
        $script:InstallText | Should -Match 'QuietMode\.TaskState\.psm1'
        $script:InstallText | Should -Match 'Get-QmTaskState'
    }

    It '安装器不再自带「找不到」这种宽泛文本判据（判据已迁到模块）' {
        # 2026-11-15 的判据是 `$msg -match '...找不到|does not exist|不存在'`，
        # 只要这句形态回来，就意味着判据重新变得不可靠。
        # 注释里提到 FQID 是允许的（那是在解释为什么迁走），所以只查代码形态。
        $script:InstallText | Should -Not -Match '\$msg -match'
        # 只要出现「拿异常文本去 match 这些字样」的代码形态就说明判据回来了。
        # 自检样本里出现这些字样是正常的（那是被测数据），所以必须带上 -match 前缀。
        $script:InstallText | Should -Not -Match "-match 'No MSFT_ScheduledTask"
        $script:InstallText | Should -Not -Match "-match '.*does not exist"
        $script:InstallText | Should -Not -Match "-match 'CmdletizationQuery_NotFound'"
    }

    It '判据自检必须在停用 v1 之前跑，且判错时以非零码退出' {
        $i = $script:InstallText.IndexOf("Write-Step '现有安装'")
        $j = $script:InstallText.IndexOf("Write-Step '停用 v1 闸门'")
        $i | Should -BeGreaterThan 0
        $j | Should -BeGreaterThan $i
        # 自检失败必须 exit，且不是 0
        $script:InstallText | Should -Match "自检失败[\s\S]{0,400}?exit 3"
    }

    It '单调度不变量仍然在位：v1 停不掉就回滚 v2' {
        $script:InstallText | Should -Match 'legacyStillOn'
        $script:InstallText | Should -Match "task', 'uninstall', '--apply'"
    }

    It '回滚失败必须明确告警「可能两套并存」，不能静默' {
        $script:InstallText | Should -Match '可能两套并存'
    }

    It '授权脚本的退出码被当成硬判据（不能只看输出文本）' {
        $script:InstallText | Should -Match '\$LASTEXITCODE'
        $script:InstallText | Should -Match '授权脚本退出码'
    }

    It '未提权时不允许执行 Apply（回滚演练除外）' {
        $script:InstallText | Should -Match "else \{ Write-Bad '未提权' \}"
        # 单引号字符串：双引号里 $DryRun 会被 PowerShell 先展开，正则就断了
        $script:InstallText | Should -Match 'Rollback.*-and.*DryRun'
    }

    It '所有备份/部署步骤都受 $apply 守卫（DryRun 只读）' {
        # 抽查三处最危险的写操作：备份、复制脚本树、导出任务 XML
        $script:InstallText | Should -Match 'if \(-not \$apply\) \{ Write-Note \("将备份 '
        $script:InstallText | Should -Match 'elseif \(-not \$apply\) \{\s*\r?\n\s*Write-Note \("将复制 '
        $script:InstallText | Should -Match 'Write-Note \("将导出任务 XML '
    }
}

Describe '安装器 DryRun（只读契约）' {

    It 'DryRun 退出码为 0，且明确说系统未改变' {
        $qmctl = Join-Path $script:RepoRoot 'src\QuietMode.Cli\bin\Release\net481\qmctl.exe'
        if (-not (Test-Path -LiteralPath $qmctl)) {
            Set-ItResult -Skipped -Because '尚未编译（缺 qmctl.exe），语法与契约用例已覆盖静态部分'
            return
        }
        # 安装器结尾有 exit，必须走子进程，否则会把 Pester 一起终止
        $hostExe = (Get-Process -Id $PID).Path
        $out = & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script:Install -Mode DryRun 2>&1 | Out-String
        $code = $LASTEXITCODE
        $tail = $out.Substring([Math]::Max(0, $out.Length - 500))
        $code | Should -Be 0 -Because ('DryRun 输出尾部：' + $tail)
        $out | Should -Match '演练完成：系统状态未改变'
        $out | Should -Not -Match 'FAIL'
    }

    It '非提权回滚演练也能跑，且不改系统状态' {
        $qmctl = Join-Path $script:RepoRoot 'src\QuietMode.Cli\bin\Release\net481\qmctl.exe'
        if (-not (Test-Path -LiteralPath $qmctl)) {
            Set-ItResult -Skipped -Because '尚未编译'
            return
        }
        $hostExe = (Get-Process -Id $PID).Path
        $out = & $hostExe -NoProfile -ExecutionPolicy Bypass -File $script:Install -Mode Rollback -DryRun 2>&1 | Out-String
        $code = $LASTEXITCODE
        $code | Should -Be 0 -Because ('回滚演练输出尾部：' + $out.Substring([Math]::Max(0, $out.Length - 400)))
        $out | Should -Match '演练完成：系统状态未改变'
    }
}
