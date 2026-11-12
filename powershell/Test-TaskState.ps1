#Requires -Version 5.1
<#
    Test-TaskState.ps1 — 维护任务三态判断的只读测试。

    只做三件不改变系统的事情：
      1. 纯函数：HRESULT → Present/NotPresent/Unknown 的映射（不依赖错误文本）。
      2. 纯函数：状态对象的 true / false / $null 三态不会被压成两态。
      3. 真实读取：已知任务读得到、不存在的任务判 NotPresent、读不到时判 Unknown。
         以及「不该写的路径一个字节都不写」（不存在 / 本来就是目标状态）。

    本脚本不调用 Disable/Enable/Stop-ScheduledTask，也不写注册表。
    同一份脚本在 Windows PowerShell 5.1 和 PowerShell 7 下都应通过。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
} catch { }

$modulePath = Join-Path $PSScriptRoot 'Modules\QuietMode.Maintenance.psm1'
if (-not (Test-Path -LiteralPath $modulePath)) {
    Write-Host "找不到模块：$modulePath" -ForegroundColor Red
    exit 2
}
Import-Module $modulePath -Force

$script:pass = 0
$script:fail = 0

function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:pass++
        Write-Host ("PASS  " + $Name) -ForegroundColor Green
    } catch {
        $script:fail++
        Write-Host ("FAIL  " + $Name + " — " + $_.Exception.Message) -ForegroundColor Red
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$What)
    if ($Expected -ne $Actual) {
        throw ("$What：期望 <" + $Expected + ">，实际 <" + $Actual + ">")
    }
}

Write-Host ("任务三态只读测试（PowerShell " + $PSVersionTable.PSVersion.ToString() + "）") -ForegroundColor Cyan
Write-Host ''

# ---- 1. HRESULT 映射（语言无关） -----------------------------------------
Check 'HRESULT 2/3 → NotPresent' {
    Assert-Equal 'NotPresent' (ConvertFrom-QmTaskHResult 2) '2'
    Assert-Equal 'NotPresent' (ConvertFrom-QmTaskHResult 3) '3'
    Assert-Equal 'NotPresent' (ConvertFrom-QmTaskHResult 0x80070002) '0x80070002'
    Assert-Equal 'NotPresent' (ConvertFrom-QmTaskHResult -2147024894) '-2147024894'
}

Check 'HRESULT 5（拒绝访问）→ Unknown 而不是 NotPresent' {
    Assert-Equal 'Unknown' (ConvertFrom-QmTaskHResult 5) '5'
    Assert-Equal 'Unknown' (ConvertFrom-QmTaskHResult -2147024891) '0x80070005'
}

Check '其它 HRESULT 一律 Unknown' {
    Assert-Equal 'Unknown' (ConvertFrom-QmTaskHResult 0) '0'
    Assert-Equal 'Unknown' (ConvertFrom-QmTaskHResult 1234) '1234'
    Assert-Equal 'Unknown' (ConvertFrom-QmTaskHResult -2147216600) 'other'
}

Check '任务状态码映射' {
    Assert-Equal 'Running' (ConvertFrom-QmTaskStateCode 4) '4'
    Assert-Equal 'Ready' (ConvertFrom-QmTaskStateCode 3) '3'
    Assert-Equal 'Disabled' (ConvertFrom-QmTaskStateCode 1) '1'
    Assert-Equal 'Unknown' (ConvertFrom-QmTaskStateCode 99) '99'
}

# ---- 2. 三态不被压平 ------------------------------------------------------
Check 'Unknown 的 enabled / exists 都是 $null' {
    $s = New-QmTaskState -Status 'Unknown' -Error 'x'
    if ($null -ne $s.enabled) { throw 'enabled 必须是 $null，不能是 false' }
    if ($null -ne $s.exists) { throw 'exists 必须是 $null' }
    Assert-Equal 'Unknown' $s.status 'status'
}

Check 'Present + enabled=false 与 Unknown 必须可区分' {
    $present = New-QmTaskState -Status 'Present' -Enabled $false -State 'Ready'
    $unknown = New-QmTaskState -Status 'Unknown' -Error '拒绝访问'
    Assert-Equal $true $present.exists 'present.exists'
    Assert-Equal $false $present.enabled 'present.enabled'
    Assert-Equal $null $unknown.enabled 'unknown.enabled'
    if ([bool]$unknown.enabled -eq $false -and $null -ne $unknown.enabled) { throw '$null 被当成 false' }
}

Check 'NotPresent 的 exists=false 但 enabled 仍是 $null' {
    $s = New-QmTaskState -Status 'NotPresent'
    Assert-Equal $false $s.exists 'exists'
    Assert-Equal $null $s.enabled 'enabled'
}

# ---- 3. 真实读取（只读） --------------------------------------------------
Check '已知系统任务读得到，且 enabled 是布尔' {
    $s = Get-QmTaskState -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag'
    if ($s.status -eq 'NotPresent') { throw 'ScheduledDefrag 不应判为不存在' }
    if ($s.status -eq 'Present') {
        if ($null -eq $s.enabled) { throw 'Present 却读不到 enabled' }
        Write-Host ("    ScheduledDefrag enabled=" + $s.enabled + " state=" + $s.state + " source=" + $s.source) -ForegroundColor DarkGray
    } else {
        Write-Host ("    ScheduledDefrag 读不到（" + $s.error + "），按 Unknown 处理") -ForegroundColor DarkGray
    }
}

Check '不存在的任务判 NotPresent（不是 Unknown）' {
    $s = Get-QmTaskState -TaskPath '\QuietMode\' -TaskName ('NoSuchTask-' + [Guid]::NewGuid().ToString('N'))
    Assert-Equal 'NotPresent' $s.status 'status'
}

Check '不存在的任务路径判 NotPresent' {
    $s = Get-QmTaskState -TaskPath ('\NoSuchFolder-' + [Guid]::NewGuid().ToString('N') + '\') -TaskName 'X'
    Assert-Equal 'NotPresent' $s.status 'status'
}

Check '读取失败时不抛异常' {
    $s = Get-QmTaskState -TaskPath '\QuietHDD\' -TaskName 'QuietHDD_NightGate'
    if ($null -eq $s) { throw '应返回状态对象而不是 $null' }
    Write-Host ("    \QuietHDD\QuietHDD_NightGate → " + $s.status) -ForegroundColor DarkGray
}

# ---- 4. 不该写的路径一个字节都不写 ---------------------------------------
Check '对不存在的任务执行禁用，返回 NotPresent 且不报成功' {
    $step = [pscustomobject]@{ order = 1; path = '\QuietMode\'; name = ('NoSuchTask-' + [Guid]::NewGuid().ToString('N')) }
    $r = Invoke-QmMaintenanceStep ([pscustomobject]@{ order = 1; action = 'DisableTask'; path = $step.path; name = $step.name })
    Assert-Equal 'NotPresent' $r.status 'status'
}

Check '对未运行的任务停止，返回 Skipped' {
    $r = Invoke-QmMaintenanceStep ([pscustomobject]@{
        order = 1; action = 'StopTask'; path = '\Microsoft\Windows\Defrag\'; name = 'ScheduledDefrag' })
    if ($r.status -ne 'Skipped' -and $r.status -ne 'NotPresent') {
        throw ("未运行的任务应 Skipped（或不存在 NotPresent），实际 " + $r.status + "：" + $r.error)
    }
}

Write-Host ''
Write-Host ("TASK STATE: " + $script:pass + " pass / " + $script:fail + " fail")
if ($script:fail -gt 0) { exit 1 }
Write-Host 'ALL PASS' -ForegroundColor Green
exit 0
