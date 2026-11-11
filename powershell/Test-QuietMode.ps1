<#
.SYNOPSIS
    v2 自检入口：编译 → 单测 → 配置校验 → baseline 校验。
.DESCRIPTION
    退出码 0 表示全部通过。任一环节失败即返回非 0，便于 CI 与计划任务判断。
.EXAMPLE
    .\Test-QuietMode.ps1
    .\Test-QuietMode.ps1 -SkipBuild -Root 'D:\qm-test'
#>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$SkipBuild,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

Import-Module (Join-Path $PSScriptRoot 'Modules\QuietMode.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Modules\QuietMode.Config.psm1') -Force

$repoRoot = Get-QuietModeRepoRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

$failures = New-Object System.Collections.Generic.List[string]
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ("--- {0}" -f $Name) -ForegroundColor Cyan
    try {
        & $Body
        Write-Host ("    PASS") -ForegroundColor Green
    }
    catch {
        $failures.Add("$Name — $($_.Exception.Message)")
        Write-Host ("    FAIL  {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

if (-not $SkipBuild) {
    Step '编译 QuietMode.Core.Tests' {
        & $dotnet build (Join-Path $repoRoot 'tests\QuietMode.Core.Tests\QuietMode.Core.Tests.csproj') -c $Configuration -v quiet --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 退出码 $LASTEXITCODE" }
    }

    Step '编译 qmctl' {
        & $dotnet build (Join-Path $repoRoot 'src\QuietMode.Cli\QuietMode.Cli.csproj') -c $Configuration -v quiet --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 退出码 $LASTEXITCODE" }
    }
}

Step 'Core 单测' {
    $exe = Join-Path $repoRoot ("tests\QuietMode.Core.Tests\bin\{0}\net481\QuietMode.Core.Tests.exe" -f $Configuration)
    if (-not (Test-Path -LiteralPath $exe)) { throw "找不到 $exe" }
    $out = & $exe
    $out | ForEach-Object { Write-Host ("    " + $_) }
    if ($LASTEXITCODE -ne 0) { throw "单测失败（退出码 $LASTEXITCODE）" }
}

Step '配置校验' {
    $check = Test-QuietModeConfig -Root $Root
    foreach ($e in $check.Errors) { Write-Host ("    ERROR " + $e) -ForegroundColor Red }
    foreach ($w in $check.Warnings) { Write-Host ("    WARN  " + $w) -ForegroundColor Yellow }
    if (-not $check.Valid -and $check.Errors.Count -gt 0 -and $check.Errors[0] -like '*config.json*') {
        Write-Host '    SKIP  尚未安装（没有 config.json）' -ForegroundColor DarkGray
        return
    }
    if (-not $check.Valid) { throw "配置校验未通过" }
}

Step 'baseline 校验' {
    $paths = Get-QuietModePaths -Root $Root
    if (-not (Test-Path -LiteralPath $paths.baseline)) {
        Write-Host '    SKIP  尚未建立 baseline' -ForegroundColor DarkGray
        return
    }
    $v = Test-QuietModeBaseline -Root $Root
    if (-not $v.Valid) {
        foreach ($e in $v.Errors) { Write-Host ("    ERROR " + $e) -ForegroundColor Red }
        throw "baseline 校验未通过"
    }
    if (-not $v.Complete) {
        Write-Host '    WARN  baseline 不完整（受保护任务需管理员读取）' -ForegroundColor Yellow
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'ALL PASS' -ForegroundColor Green
    exit 0
}

Write-Host ("FAILED {0} 项：" -f $failures.Count) -ForegroundColor Red
foreach ($f in $failures) { Write-Host ("  " + $f) -ForegroundColor Red }
exit 1
