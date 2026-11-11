#Requires -Version 5.1
<#
    Invoke-QuietModeWorker.ps1 — v2 唯一执行器（规格 §7.3 / Phase 4）。

    它不做任何策略判断：执行什么、按什么顺序执行，全部来自 C# 生成的请求 JSON。
    决策只在 QuietMode.Core.Scheduling.StateMachine，顺序只在 ReconcilePlanner。

    用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File Invoke-QuietModeWorker.ps1 `
          -RequestFile <request.json> -ResultFile <result.json> -Root C:\ProgramData\QuietMode

    请求 JSON 关键字段：
      action        night | day | restore | verify
      steps         [ { order, module: maintenance|disk|spindown|fan|verify, action, detail } ]
      maintenance   { phase, steps: [ MaintenanceStep... ] }     # 交给 Invoke-QmMaintenanceStep
      diskTargets   [ { uniqueId, serialNumber, ... } ]
      diskForce     bool
      spinDown      { enabled, minutes, seconds }                # 交给 Invoke-QmSpinDown
      fan           { enabled, ipcPath, controlExe, normalProfile, silentProfile, flagPath }

    结果 JSON：host / action / startTime / endTime / exitCode / elevated / steps[] / error
    退出码：0 全部完成或跳过；2 有失败；3 参数错误或未提权。
#>
[CmdletBinding()]
param(
    [string]$RequestFile,
    [string]$ResultFile,
    [string]$Root = 'C:\ProgramData\QuietMode',
    [string]$Action = ''
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

$startTime = Get-Date
$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot  = Split-Path -Parent (Split-Path -Parent $scriptDir)
$modDir    = Join-Path $repoRoot 'powershell\Modules'

function Write-QmWorkerResult {
    param($Payload, [string]$Path)
    $json = $Payload | ConvertTo-Json -Depth 12
    if ($Path -and $Path.Trim() -ne '') {
        $dir = Split-Path -Parent $Path
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
    } else {
        Write-Output $json
    }
}

function New-QmWorkerError {
    param([string]$Code, [string]$Message)
    return [ordered]@{
        host      = [ordered]@{ computerName = $env:COMPUTERNAME; elevated = $false; timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') }
        action    = $Action
        startTime = $startTime.ToString('yyyy-MM-dd HH:mm:ss')
        endTime   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        exitCode  = 3
        elevated  = $false
        errorCode = $Code
        error     = $Message
        steps     = @()
    }
}

function Add-WorkerLog {
    param([string]$Line)
    try {
        $logDir = Join-Path $Root 'logs'
        if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Add-Content -LiteralPath (Join-Path $logDir 'worker.log') -Value $Line -Encoding UTF8
    } catch { }
}

foreach ($m in @('QuietMode.Common', 'QuietMode.Maintenance', 'QuietMode.Disk', 'QuietMode.Fan', 'QuietMode.Power')) {
    $p = Join-Path $modDir ($m + '.psm1')
    if (-not (Test-Path -LiteralPath $p)) {
        Write-QmWorkerResult (New-QmWorkerError 'NO_MODULE' "找不到模块：$p") $ResultFile
        exit 3
    }
    Import-Module $p -Force
}

try {
    if (-not $RequestFile -or -not (Test-Path -LiteralPath $RequestFile)) {
        Write-QmWorkerResult (New-QmWorkerError 'NO_REQUEST' "请求文件不存在：$RequestFile") $ResultFile
        exit 3
    }

    $raw = [System.IO.File]::ReadAllText($RequestFile, [System.Text.Encoding]::UTF8)
    $req = $raw | ConvertFrom-Json

    $action = if ($Action) { $Action } else { [string]$req.action }
    if (-not $action) { $action = 'verify' }

    $elevated = Test-QmElevated
    $steps = @($req.steps)
    $needsAdmin = ($action -eq 'night' -or $action -eq 'day' -or $action -eq 'restore')

    if ($needsAdmin -and -not $elevated) {
        $out = @()
        foreach ($s in $steps) {
            $out += [ordered]@{
                order  = [int]$s.order
                module = [string]$s.module
                action = [string]$s.action
                status = 'notrun'
                error  = '需要管理员权限（维护任务 / 磁盘脱机 / HKLM 写入）'
                detail = ''
            }
        }
        $payload = New-QmWorkerError 'ELEVATION_REQUIRED' '维护闸门与磁盘操作需要管理员权限，本次未做任何修改'
        $payload.steps = @($out)
        Write-QmWorkerResult $payload $ResultFile
        Add-WorkerLog ('{0} [{1}] ELEVATION_REQUIRED 未提权，全部步骤未执行' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $action)
        exit 3
    }

    $results = New-Object System.Collections.ArrayList
    $failed = 0
    $diskTargets = @($req.diskTargets)
    $diskForce = [bool]$req.diskForce

    foreach ($s in $steps) {
        $module = [string]$s.module
        $op = [string]$s.action
        $status = 'done'
        $err = ''
        $detail = ''

        switch ($module) {
            'maintenance' {
                if (-not $req.maintenance -or -not $req.maintenance.steps) {
                    $status = 'skipped'
                    $detail = '请求里没有维护步骤'
                    break
                }
                $mFailed = 0
                $sub = New-Object System.Collections.ArrayList
                foreach ($ms in @($req.maintenance.steps)) {
                    $r = Invoke-QmMaintenanceStep $ms
                    [void]$sub.Add($r)
                    if ($r.status -eq 'Failed') { $mFailed++ }
                }
                $detail = "$($sub.Count) 步，失败 $mFailed"
                if ($mFailed -gt 0) {
                    $status = 'failed'
                    $err = "$mFailed 个维护步骤失败"
                }
            }
            'disk' {
                if ($diskTargets.Count -eq 0) {
                    $status = 'skipped'
                    $detail = '没有启用的目标磁盘'
                    break
                }
                $r = Invoke-QmDiskOperation -Operation $op -Targets $diskTargets -Apply -Force:$diskForce
                $bad = @($r.results | Where-Object { -not $_.success })
                $detail = "$(@($r.results).Count) 块磁盘，失败 $($bad.Count)"
                if ($bad.Count -gt 0) {
                    $status = 'failed'
                    $err = (($bad | ForEach-Object { "$($_.friendlyName):$($_.errorCode)" }) -join '; ')
                }
            }
            'spindown' {
                $sd = $req.spinDown
                if (-not $sd -or $sd.enabled -ne $true) {
                    $status = 'skipped'
                    $detail = '硬盘停转未启用'
                    break
                }
                $sec = [int]$sd.seconds
                $r = Invoke-QmSpinDown -Seconds $sec
                $detail = "目标 ${sec}s 回读 AC=$($r.ac)s DC=$($r.dc)s"
                if ($r.status -ne 'Done') { $status = 'failed'; $err = [string]$r.error }
            }
            'fan' {
                $fan = $req.fan
                if (-not $fan -or $fan.enabled -ne $true) {
                    $status = 'skipped'
                    $detail = '风扇切换未启用'
                    break
                }
                $mode = if ($op -eq 'silent') { 'silent' } else { 'normal' }
                $profile = if ($mode -eq 'silent') { [string]$fan.silentProfile } else { [string]$fan.normalProfile }
                $r = Invoke-QmFanSwitch -Mode $mode -IpcPath ([string]$fan.ipcPath) -Profile $profile `
                                        -ControlExe ([string]$fan.controlExe) -FlagPath ([string]$fan.flagPath)
                $detail = "$($r.profile) 当前=$($r.current)"
                if ($r.status -ne 'Done') { $status = 'failed'; $err = [string]$r.error }
            }
            'verify' {
                $detail = '由 C# 复观测做最终判定'
            }
            default {
                $status = 'failed'
                $err = "未知模块：$module"
            }
        }

        if ($status -eq 'failed') { $failed++ }
        [void]$results.Add([ordered]@{
            order = [int]$s.order; module = $module; action = $op
            status = $status; error = $err; detail = $detail
        })
        Add-WorkerLog ('{0} [{1}] {2,-12} {3,2} {4}/{5} {6}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'),
            $action, $status, $s.order, $module, $op, $err)
    }

    $payload = [ordered]@{
        host      = [ordered]@{
            computerName = $env:COMPUTERNAME
            user         = [string]$env:USERNAME
            elevated     = $elevated
            powershell   = $PSVersionTable.PSVersion.ToString()
            timestamp    = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        }
        action    = $action
        startTime = $startTime.ToString('yyyy-MM-dd HH:mm:ss')
        endTime   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        exitCode  = if ($failed -gt 0) { 2 } else { 0 }
        elevated  = $elevated
        errorCode = if ($failed -gt 0) { 'STEP_FAILED' } else { '' }
        error     = if ($failed -gt 0) { "$failed 个步骤失败" } else { '' }
        steps     = @($results)
    }
    Write-QmWorkerResult $payload $ResultFile
    if ($failed -gt 0) { exit 2 }
    exit 0
} catch {
    $msg = $_.Exception.Message + "`n" + $_.InvocationInfo.PositionMessage
    Write-QmWorkerResult (New-QmWorkerError 'SCRIPT_ERROR' $msg) $ResultFile
    Add-WorkerLog ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' SCRIPT_ERROR ' + $_.Exception.Message)
    exit 3
}
