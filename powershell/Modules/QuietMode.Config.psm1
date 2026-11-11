<#
.SYNOPSIS
    统一配置访问层。所有 v2 脚本只通过这里读 config.json / state.json / baseline.json。
.DESCRIPTION
    读取直接走文件（UTF-8），写入与校验委托 qmctl，保证与 C# 层同一套原子写与校验规则。
#>

if (-not (Get-Module -Name QuietMode.Common)) {
    # -Global：让调用方也能直接用 Common 的函数（-Force 会把已加载的模块顶掉，别用）
    Import-Module (Join-Path $PSScriptRoot 'QuietMode.Common.psm1') -Global
}

function Get-QuietModePaths {
    [CmdletBinding()]
    param([string]$Root)

    if (-not $Root) { $Root = Join-Path $env:ProgramData 'QuietMode' }
    $r = Invoke-QuietModeToolJson -Arguments @('paths') -Root $Root
    if (-not $r.Ok -or -not $r.Data) {
        # qmctl 不可用时退回本地拼装（只读场景仍可用）
        return [pscustomobject]@{
            Root       = $Root
            Config     = Join-Path $Root 'config.json'
            Backup     = Join-Path $Root 'config.backup.json'
            State      = Join-Path $Root 'state.json'
            Baseline   = Join-Path $Root 'baseline.json'
            Logs       = Join-Path $Root 'logs'
            Recovery   = Join-Path $Root 'recovery'
            LegacyAppData = Join-Path $env:APPDATA 'QuietMode'
        }
    }
    return $r.Data
}

function Get-QuietModeConfig {
    [CmdletBinding()]
    param(
        [string]$Root,
        [switch]$AllowMissing
    )

    $paths = Get-QuietModePaths -Root $Root
    if (-not (Test-Path -LiteralPath $paths.config)) {
        if ($AllowMissing) { return $null }
        throw "config.json 不存在：$($paths.config)。先运行 qmctl config init 或 config migrate。"
    }

    $raw = Get-Content -LiteralPath $paths.config -Raw -Encoding UTF8
    return ($raw | ConvertFrom-Json)
}

function Get-QuietModeState {
    [CmdletBinding()]
    param([string]$Root)

    $paths = Get-QuietModePaths -Root $Root
    if (-not (Test-Path -LiteralPath $paths.state)) { return $null }
    $raw = Get-Content -LiteralPath $paths.state -Raw -Encoding UTF8
    return ($raw | ConvertFrom-Json)
}

function Test-QuietModeConfig {
    <#
    .SYNOPSIS
        校验 config.json（含风扇配置文件存在性）。返回 { Valid, Errors[], Warnings[], ExitCode }。
    #>
    [CmdletBinding()]
    param([string]$Root)

    $r = Invoke-QuietModeToolJson -Arguments @('config', 'validate') -Root $Root
    if (-not $r.Data) {
        return [pscustomobject]@{
            Valid    = $false
            Errors   = @('无法取得校验结果（qmctl 退出码 ' + $r.ExitCode + '）')
            Warnings = @()
            ExitCode = $r.ExitCode
        }
    }
    return [pscustomobject]@{
        Valid    = [bool]$r.Data.valid
        Errors   = @($r.Data.errors)
        Warnings = @($r.Data.warnings)
        ExitCode = $r.ExitCode
    }
}

function Invoke-QuietModeConfigMigration {
    [CmdletBinding()]
    param(
        [string]$Root,
        [switch]$Force
    )

    $argv = @('config', 'migrate')
    if ($Force) { $argv += '--force' }
    $r = Invoke-QuietModeToolJson -Arguments $argv -Root $Root
    return [pscustomobject]@{
        Ok         = [bool]($r.Data -and $r.Data.ok)
        WroteConfig = [bool]($r.Data -and $r.Data.wroteConfig)
        LegacyFound = [bool]($r.Data -and $r.Data.legacyFound)
        Notes      = @($r.Data.notes)
        Warnings   = @($r.Data.warnings)
        Errors     = @($r.Data.errors)
        ExitCode   = $r.ExitCode
    }
}

function New-QuietModeBaseline {
    <#
    .SYNOPSIS
        采集并写入 baseline.json。已存在时必须显式 -Reset（规格 §5.2）。
    #>
    [CmdletBinding()]
    param(
        [string]$Root,
        [switch]$Reset
    )

    $argv = @('snapshot', 'create')
    if ($Reset) { $argv += '--reset' }
    $r = Invoke-QuietModeToolJson -Arguments $argv -Root $Root
    return [pscustomobject]@{
        Ok          = [bool]($r.Data -and $r.Data.valid)
        Complete    = [bool]($r.Data -and $r.Data.complete)
        Integrity   = if ($r.Data) { $r.Data.integritySha256 } else { '' }
        Path        = if ($r.Data) { $r.Data.path } else { '' }
        ExitCode    = $r.ExitCode
    }
}

function Test-QuietModeBaseline {
    [CmdletBinding()]
    param([string]$Root)

    $r = Invoke-QuietModeToolJson -Arguments @('snapshot', 'verify') -Root $Root
    if (-not $r.Data) {
        return [pscustomobject]@{
            Valid    = $false
            Complete = $false
            Errors   = @('无法取得校验结果（qmctl 退出码 ' + $r.ExitCode + '）')
            Warnings = @()
        }
    }
    return [pscustomobject]@{
        Valid    = [bool]$r.Data.valid
        Complete = [bool]$r.Data.complete
        Errors   = @($r.Data.errors)
        Warnings = @($r.Data.warnings)
    }
}

function Get-QuietModeDiskTargets {
    <#
    .SYNOPSIS
        从配置里取启用中的磁盘目标（按稳定身份，不用磁盘编号）。
    #>
    [CmdletBinding()]
    param([string]$Root)

    $cfg = Get-QuietModeConfig -Root $Root -AllowMissing
    if (-not $cfg -or -not $cfg.disks -or -not $cfg.disks.enabled) { return @() }
    return @($cfg.disks.targets | Where-Object { $_.enabled })
}

Export-ModuleMember -Function Get-QuietModePaths, Get-QuietModeConfig, Get-QuietModeState,
    Test-QuietModeConfig, Invoke-QuietModeConfigMigration, New-QuietModeBaseline,
    Test-QuietModeBaseline, Get-QuietModeDiskTargets
