#Requires -Version 5.1
<#
    Invoke-DiskOperation.ps1 — 磁盘脱机/联机的唯一入口。

    用法（C# 调用）：
      powershell -NoProfile -ExecutionPolicy Bypass -File Invoke-DiskOperation.ps1 `
          -RequestFile <request.json> -ResultFile <result.json>

    用法（人手工执行）：
      .\Invoke-DiskOperation.ps1 -Operation offline -Apply -Index 0 -Root C:\ProgramData\QuietMode
      .\Invoke-DiskOperation.ps1 -Operation verify -Root C:\ProgramData\QuietMode

    请求 JSON：
      {
        "operation": "offline" | "online" | "verify",
        "apply": true | false,
        "force": true | false,
        "targets": [ { "uniqueId": "...", "serialNumber": "...", "friendlyName": "...",
                       "sizeBytes": 4000787030016, "busType": "SATA" } ]
      }

    结果 JSON：{ host, operation, apply, results: [ DiskOperationResult... ] }
    退出码：0 全部成功；2 存在失败；3 参数/脚本错误。
#>
[CmdletBinding()]
param(
    [string]$RequestFile,
    [string]$ResultFile,
    [ValidateSet('offline','online','verify')][string]$Operation,
    [switch]$Apply,
    [switch]$Force,
    [int]$Index = -1,
    [string]$Root = 'C:\ProgramData\QuietMode',
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot  = Split-Path -Parent (Split-Path -Parent $scriptDir)
$modulePath = Join-Path $repoRoot 'powershell\Modules\QuietMode.Disk.psm1'
if (-not (Test-Path -LiteralPath $modulePath)) {
    Write-Error "找不到磁盘模块：$modulePath"
    exit 3
}
Import-Module $modulePath -Force

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

function New-QmErrorPayload {
    param([string]$Code, [string]$Message)
    return [ordered]@{
        host = [ordered]@{ computerName = $env:COMPUTERNAME; timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') }
        operation = $Operation
        apply = [bool]$Apply
        results = @([ordered]@{
            operation = $Operation; success = $false; verified = $false; dryRun = (-not $Apply)
            errorCode = $Code; errorMessage = $Message
            uniqueId = ''; serialNumber = ''; friendlyName = ''; currentDiskNumber = -1
            previousState = 'unknown'; requestedState = $Operation; actualState = 'unknown'
            timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
            steps = @(); warnings = @()
        })
    }
}

try {
    $targets = @()

    if ($RequestFile -and $RequestFile.Trim() -ne '') {
        if (-not (Test-Path -LiteralPath $RequestFile)) {
            Write-QmResult (New-QmErrorPayload 'NO_REQUEST' "请求文件不存在：$RequestFile") $ResultFile
            exit 3
        }
        $raw = [System.IO.File]::ReadAllText($RequestFile, [System.Text.Encoding]::UTF8)
        $req = $raw | ConvertFrom-Json
        if ($req.operation) { $Operation = [string]$req.operation }
        if ($null -ne $req.apply) { $Apply = [bool]$req.apply }
        if ($null -ne $req.force) { $Force = [bool]$req.force }
        if ($null -ne $req.targets) { $targets = @($req.targets) }
    }

    if (-not $Operation) { $Operation = 'verify' }

    # 没有给 targets 时从 config.json 读取（人类手工执行路径）
    if (@($targets).Count -eq 0) {
        $cfgFile = $ConfigPath
        if (-not $cfgFile -or $cfgFile.Trim() -eq '') { $cfgFile = Join-Path $Root 'config.json' }
        if (-not (Test-Path -LiteralPath $cfgFile)) {
            Write-QmResult (New-QmErrorPayload 'NO_TARGET' "没有磁盘目标，且找不到配置文件：$cfgFile") $ResultFile
            exit 3
        }
        $cfg = [System.IO.File]::ReadAllText($cfgFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $all = @($cfg.disks.targets | Where-Object { $_.enabled -ne $false })
        if ($Index -ge 0) {
            if ($Index -ge $all.Count) {
                Write-QmResult (New-QmErrorPayload 'NO_TARGET' "配置中没有第 $Index 个磁盘目标") $ResultFile
                exit 2
            }
            $targets = @($all[$Index])
        } else {
            $targets = @($all)
        }
    }

    $logFile = Join-Path $Root 'logs\disks.log'
    $payload = Invoke-QmDiskOperation -Operation $Operation -Targets $targets -Apply:$Apply -Force:$Force -LogFile $logFile

    Write-QmResult $payload $ResultFile

    $failed = @($payload.results | Where-Object { -not $_.success })
    if ($failed.Count -gt 0) { exit 2 }
    exit 0
} catch {
    $msg = $_.Exception.Message
    $line = $_.InvocationInfo.PositionMessage
    Write-QmResult (New-QmErrorPayload 'SCRIPT_ERROR' ($msg + "`n" + $line)) $ResultFile
    exit 3
}
