#Requires -Version 5.1
# 双版本 Pester 运行器：只做一件事 —— 在当前 PowerShell 里跑 tests\ps 并报告结果。
# CI 的两条 Windows 腿各调一次这个脚本，本地手工验收也用它。
#
# -NUnitXml <路径>：顺带产出 NUnit XML（CI 上传用）。同一次运行既断言又出报告，
# 不为了拿 XML 再跑第二遍 —— Pester 全量跑一次要几十秒，跑两遍纯属浪费。
param(
    [string]$NUnitXml = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$root = Split-Path $PSScriptRoot -Parent
$tests = Join-Path $root 'tests\ps'

Write-Host "PowerShell $($PSVersionTable.PSVersion) / $([System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription)"

$required = [version]'5.7.0'
$pinned = [version]'5.7.1'
$installed = @(Get-Module -ListAvailable Pester | Where-Object { $_.Version -ge $required })
# 「钉版本」必须真的钉住：托管 runner 的镜像自带 5.9.0，只按 >= 取第一个会悄悄用镜像版，
# 把 release.yml 里装的 5.7.1 架空。这里优先取 5.7.1，没有才退到已装的最高版本。
$pester = $installed | Where-Object { $_.Version -eq $pinned } | Select-Object -First 1
if (-not $pester) { $pester = $installed | Sort-Object Version -Descending | Select-Object -First 1 }
if (-not $pester) {
    Write-Host "Pester $required+ 未安装，尝试安装..." -ForegroundColor Yellow
    Set-PSRepository PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
    Install-Module Pester -RequiredVersion $pinned -Force -SkipPublisherCheck -Scope CurrentUser
    Import-Module Pester -RequiredVersion $pinned -Force
    $pester = Get-Module -ListAvailable Pester | Where-Object { $_.Version -eq $pinned } | Select-Object -First 1
} else {
    Import-Module Pester -RequiredVersion $pester.Version -Force
}
if (-not $pester) { Write-Host 'ERROR 找不到可用的 Pester' -ForegroundColor Red; exit 3 }
Write-Host "Pester $($pester.Version)"

$cfg = New-PesterConfiguration
$cfg.Run.Path = $tests
$cfg.Run.PassThru = $true
$cfg.Output.Verbosity = 'None'
if ($NUnitXml) {
    $cfg.TestResult.Enabled = $true
    $cfg.TestResult.OutputPath = $NUnitXml
    $cfg.TestResult.OutputFormat = 'NUnitXml'
}
$r = Invoke-Pester -Configuration $cfg

Write-Host ("RESULT Total={0} Passed={1} Failed={2} Skipped={3}" -f $r.TotalCount, $r.PassedCount, $r.FailedCount, $r.SkippedCount)

foreach ($f in $r.Failed) {
    Write-Host ("  FAIL " + $f.ExpandedName) -ForegroundColor Red
    $m = ($f.ErrorRecord[0].Exception.Message -replace "`r?`n", ' ')
    Write-Host ("       " + $m.Substring(0, [Math]::Min(240, $m.Length))) -ForegroundColor DarkGray
}

if ($r.TotalCount -eq 0) { Write-Host 'ERROR 没有执行任何测试'; exit 2 }
if ($r.FailedCount -gt 0) { exit 1 }
exit 0
