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

    Step '编译托盘（Phase 5 分层结构）' {
        & $dotnet build (Join-Path $repoRoot 'QuietMode\build\QuietMode.csproj') -c $Configuration -v quiet --nologo | Out-Null
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

Step '托盘契约：status 字段齐全（只读）' {
    $r = Invoke-QuietModeToolJson -Arguments @('status') -Root $Root
    if ($null -eq $r.Data) { throw "status 无 JSON 输出（退出码 $($r.ExitCode)）" }
    $required = @('ok', 'config', 'schedule', 'startup', 'window', 'controller', 'state', 'fan', 'disks', 'spinDown', 'task')
    foreach ($k in $required) {
        if ($null -eq $r.Data.PSObject.Properties[$k]) { throw "status 缺少字段 $k（托盘按名取值）" }
    }
    if ($null -eq $r.Data.controller.PSObject.Properties['state']) { throw 'status 缺少 controller.state' }
    if ($r.Data.PSObject.Properties['config'].Value.PSObject.Properties['exists'] -eq $null) {
        throw 'status 缺少 config.exists（托盘据此决定是否对账自启）'
    }
    Write-Host ("    state={0} action={1} disks={2}" -f $r.Data.controller.state,
                $r.Data.controller.action, @($r.Data.disks.targets).Count)
    if ($null -eq $r.Data.spinDown.PSObject.Properties['acSeconds']) { throw 'status 缺少 spinDown.acSeconds' }
}

Step '磁盘身份解析' {
    $paths = Get-QuietModePaths -Root $Root
    if (-not (Test-Path -LiteralPath $paths.config)) {
        Write-Host '    SKIP  尚未安装（没有 config.json）' -ForegroundColor DarkGray
        return
    }
    $r = Invoke-QuietModeToolJson -Arguments @('disks', 'resolve') -Root $Root
    if ($null -eq $r.Data) { throw "disks resolve 无输出（退出码 $($r.ExitCode)）" }
    foreach ($t in @($r.Data.targets)) {
        $line = "    {0,-16} {1}" -f $t.status, $t.uniqueId
        if ($t.status -eq 'matched') { Write-Host $line } else { Write-Host ($line + "  " + $t.reason) -ForegroundColor Yellow }
    }
    if ($r.ExitCode -ne 0) { throw '存在无法唯一匹配的磁盘目标（不会执行任何磁盘操作）' }
}

Step '磁盘安全闸门（只读集成测试）' {
    $script = Join-Path $PSScriptRoot 'Test-DiskSafety.ps1'
    $out = & $script -Configuration $Configuration
    $out | ForEach-Object { Write-Host ("    " + $_) }
    if ($LASTEXITCODE -ne 0) { throw "磁盘安全测试失败（退出码 $LASTEXITCODE）" }
}

Step '维护管理（只读集成测试）' {
    $script = Join-Path $PSScriptRoot 'Test-MaintenanceSafety.ps1'
    $out = & $script
    $out | ForEach-Object { Write-Host ("    " + $_) }
    if ($LASTEXITCODE -ne 0) { throw "维护管理测试失败（退出码 $LASTEXITCODE）" }
}

Step '统一调度（只读集成测试）' {
    $script = Join-Path $PSScriptRoot 'Test-SchedulingSafety.ps1'
    $out = & $script
    $out | ForEach-Object { Write-Host ("    " + $_) }
    if ($LASTEXITCODE -ne 0) { throw "统一调度测试失败（退出码 $LASTEXITCODE）" }
}

Step '维护计划与验证（只读）' {
    $paths = Get-QuietModePaths -Root $Root
    if (-not (Test-Path -LiteralPath $paths.baseline)) {
        Write-Host '    SKIP  尚未建立 baseline' -ForegroundColor DarkGray
        return
    }
    foreach ($phase in @('night', 'day')) {
        $p = Invoke-QuietModeToolJson -Arguments @('maint', 'plan', $phase) -Root $Root
        if ($null -eq $p.Data) { throw "maint plan $phase 无输出" }
        Write-Host ("    {0,-5} 计划 {1} 步" -f $phase, @($p.Data.steps).Count)
    }
    $v = Invoke-QuietModeToolJson -Arguments @('maint', 'verify', 'night') -Root $Root
    if ($null -eq $v.Data) { throw 'maint verify night 无输出' }
    Write-Host ("    verify night -> " + $v.Data.verdictText) -ForegroundColor DarkGray
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'ALL PASS' -ForegroundColor Green
    exit 0
}

Write-Host ("FAILED {0} 项：" -f $failures.Count) -ForegroundColor Red
foreach ($f in $failures) { Write-Host ("  " + $f) -ForegroundColor Red }
exit 1
