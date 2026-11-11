#Requires -Version 5.1
<#
    Invoke-MaintenanceOperation.ps1 — 夜间/白天维护切换的唯一写入入口。

    用法（C# 调用）：
      powershell -NoProfile -ExecutionPolicy Bypass -File Invoke-MaintenanceOperation.ps1 `
          -RequestFile <request.json> -ResultFile <result.json> -Root C:\ProgramData\QuietMode

    请求 JSON：
      {
        "phase": "night" | "day",
        "steps": [ { "order": 1, "action": "DisableTask", "path": "\\Microsoft\\Windows\\Defrag\\",
                     "name": "ScheduledDefrag", "desiredValue": "", "kind": "DWord" } ]
      }

    结果 JSON：{ host, phase, elevated, steps: [ { order, status, error, observedAfter } ] }
    退出码：0 全部完成/跳过；2 存在失败；3 参数错误或未提权。

    本脚本只按计划执行，不做任何策略判断；成功与否最终由 C# 复观测决定。
#>
[CmdletBinding()]
param(
    [string]$RequestFile,
    [string]$ResultFile,
    [string]$Root = 'C:\ProgramData\QuietMode'
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

$scriptDir  = Split-Path -Parent $PSCommandPath
$repoRoot   = Split-Path -Parent (Split-Path -Parent $scriptDir)
$modulePath = Join-Path $repoRoot 'powershell\Modules\QuietMode.Maintenance.psm1'

function Write-QmResult {
    param($Payload, [string]$Path)
    $json = $Payload | ConvertTo-Json -Depth 10
    if ($Path -and $Path.Trim() -ne '') {
        $dir = Split-Path -Parent $Path
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        Write-Output $json
    }
}

function New-QmErrorResult {
    param([string]$Code, [string]$Message)
    return [ordered]@{
        host     = [ordered]@{ computerName = $env:COMPUTERNAME; timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') }
        phase    = ''
        elevated = $false
        errorCode = $Code
        error    = $Message
        steps    = @()
    }
}

if (-not (Test-Path -LiteralPath $modulePath)) {
    Write-QmResult (New-QmErrorResult 'NO_MODULE' "找不到维护模块：$modulePath") $ResultFile
    exit 3
}
Import-Module $modulePath -Force

try {
    if (-not $RequestFile -or -not (Test-Path -LiteralPath $RequestFile)) {
        Write-QmResult (New-QmErrorResult 'NO_REQUEST' "请求文件不存在：$RequestFile。人工执行请用 qmctl maint night|day --apply 生成计划。") $ResultFile
        exit 3
    }

    $elevated = Test-QmElevated
    $raw = [System.IO.File]::ReadAllText($RequestFile, [System.Text.Encoding]::UTF8)
    $req = $raw | ConvertFrom-Json
    $phase = [string]$req.phase
    $steps = @($req.steps)

    if (-not $elevated) {
        Write-QmResult (New-QmErrorResult 'ELEVATION_REQUIRED' '维护切换需要管理员权限（禁用计划任务、改服务启动类型、写 HKLM）') $ResultFile
        exit 3
    }

    $logDir = Join-Path $Root 'logs'
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $logFile = Join-Path $logDir 'maintenance.log'

    $results = New-Object System.Collections.ArrayList
    $failed = 0
    foreach ($step in $steps) {
        $r = Invoke-QmMaintenanceStep $step
        [void]$results.Add($r)
        if ($r.status -eq 'Failed') { $failed++ }
        $line = '{0} [{1}] {2,-20} {3} {4} {5}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $phase, $r.status, $r.order, $step.action, $r.error
        Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
    }

    $payload = [ordered]@{
        host     = [ordered]@{
            computerName = $env:COMPUTERNAME
            timestamp    = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
            elevated     = $elevated
        }
        phase    = $phase
        elevated = $elevated
        error    = ''
        steps    = @($results)
    }
    Write-QmResult $payload $ResultFile

    if ($failed -gt 0) { exit 2 }
    exit 0
} catch {
    Write-QmResult (New-QmErrorResult 'SCRIPT_ERROR' ($_.Exception.Message + "`n" + $_.InvocationInfo.PositionMessage)) $ResultFile
    exit 3
}
