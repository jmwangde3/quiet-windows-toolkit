#Requires -Version 5.1
<#
    QuietMode.Maintenance.psm1 — 维护任务/服务/注册表的唯一写入实现。

    设计约束（规格 §4.2）：
      * 每个写动作都必须用 -ErrorAction Stop。
      * 写完必须重新读取核对，核对不过就是失败，不能因为「没抛异常」报成功。
      * 只按任务路径+名字停止任务，绝不按进程名杀 defrag.exe 之类。
      * 不猜原始值：恢复什么由 C# 传进来的 baseline 决定，本模块不做默认值兜底。
#>

$ErrorActionPreference = 'Stop'

function Test-QmElevated {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-QmRegistryPsPath {
    param([string]$KeyPath)
    if ($KeyPath -match '^(HKLM|HKEY_LOCAL_MACHINE)[:\\]') { return $KeyPath }
    if ($KeyPath -match '^(HKCU|HKEY_CURRENT_USER)[:\\]') { return $KeyPath }
    return "HKLM:\$($KeyPath.TrimStart('\'))"
}

function Get-QmTaskState {
    param([string]$TaskPath, [string]$TaskName)
    try {
        $t = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop
        return [pscustomobject]@{
            exists  = $true
            enabled = [bool]$t.Settings.Enabled
            state   = [string]$t.State
            error   = ''
        }
    } catch {
        $msg = $_.Exception.Message
        $missing = $msg -match 'No MSFT_ScheduledTask|不存在|not found|找不到'
        return [pscustomobject]@{
            exists  = (-not $missing)
            enabled = $null
            state   = ''
            error   = $msg
        }
    }
}

function New-QmStepResult {
    param([int]$Order, [string]$Status, [string]$Error = '', [string]$ObservedAfter = '')
    return [ordered]@{
        order         = $Order
        status        = $Status
        error         = $Error
        observedAfter = $ObservedAfter
    }
}

function Invoke-QmStopTask {
    param($Step)
    $before = Get-QmTaskState -TaskPath $Step.path -TaskName $Step.name
    if (-not $before.exists) { return (New-QmStepResult $Step.order 'NotPresent' '') }
    if ($before.state -ne 'Running') { return (New-QmStepResult $Step.order 'Skipped' '任务本来未运行') }
    try {
        Stop-ScheduledTask -TaskPath $Step.path -TaskName $Step.name -ErrorAction Stop
        $after = Get-QmTaskState -TaskPath $Step.path -TaskName $Step.name
        if ($after.state -eq 'Running') {
            return (New-QmStepResult $Step.order 'Failed' '停止后复读仍为 Running' $after.state)
        }
        return (New-QmStepResult $Step.order 'Done' '' $after.state)
    } catch {
        # 停止失败只记录：调用方会把它记为非关键失败
        return (New-QmStepResult $Step.order 'Failed' $_.Exception.Message)
    }
}

function Invoke-QmSetTaskEnabled {
    param($Step, [bool]$Enabled)
    $before = Get-QmTaskState -TaskPath $Step.path -TaskName $Step.name
    if (-not $before.exists) { return (New-QmStepResult $Step.order 'NotPresent' '') }
    if ($Enabled -and $before.enabled) { return (New-QmStepResult $Step.order 'Skipped' '本来就是启用' 'enabled=True') }
    if (-not $Enabled -and -not $before.enabled) { return (New-QmStepResult $Step.order 'Skipped' '本来就是禁用' 'enabled=False') }
    try {
        if ($Enabled) {
            Enable-ScheduledTask -TaskPath $Step.path -TaskName $Step.name -ErrorAction Stop | Out-Null
        } else {
            Disable-ScheduledTask -TaskPath $Step.path -TaskName $Step.name -ErrorAction Stop | Out-Null
        }
        $after = Get-QmTaskState -TaskPath $Step.path -TaskName $Step.name
        if (-not $after.exists) {
            return (New-QmStepResult $Step.order 'Failed' '写入后任务消失了' '')
        }
        if ([bool]$after.enabled -ne $Enabled) {
            return (New-QmStepResult $Step.order 'Failed' ('复读 Enabled 与期望不一致：期望 ' + $Enabled + '，实际 ' + $after.enabled) ('enabled=' + $after.enabled))
        }
        return (New-QmStepResult $Step.order 'Done' '' ('enabled=' + $after.enabled))
    } catch {
        return (New-QmStepResult $Step.order 'Failed' $_.Exception.Message)
    }
}

function Invoke-QmServiceStartType {
    param($Step)
    $map = @{ '0' = 'Boot'; '1' = 'System'; '2' = 'Automatic'; '3' = 'Manual'; '4' = 'Disabled' }
    $desired = [string]$Step.desiredValue
    if (-not $map.ContainsKey($desired)) {
        return (New-QmStepResult $Step.order 'Failed' "未知的启动类型值：$desired")
    }
    try {
        $svc = Get-Service -Name $Step.name -ErrorAction Stop
    } catch {
        return (New-QmStepResult $Step.order 'NotPresent' $_.Exception.Message)
    }
    try {
        Set-Service -Name $Step.name -StartupType $map[$desired] -ErrorAction Stop
        $reg = "HKLM:\SYSTEM\CurrentControlSet\Services\$($Step.name)"
        $actual = (Get-ItemProperty -Path $reg -Name 'Start' -ErrorAction Stop).Start
        if ([int]$actual -ne [int]$desired) {
            return (New-QmStepResult $Step.order 'Failed' "复读 Start=$actual，期望 $desired" "Start=$actual")
        }
        return (New-QmStepResult $Step.order 'Done' '' "Start=$actual")
    } catch {
        return (New-QmStepResult $Step.order 'Failed' $_.Exception.Message)
    }
}

function Invoke-QmServiceState {
    param($Step, [string]$Desired)
    try {
        $svc = Get-Service -Name $Step.name -ErrorAction Stop
    } catch {
        return (New-QmStepResult $Step.order 'NotPresent' $_.Exception.Message)
    }
    if ([string]$svc.Status -eq $Desired) {
        return (New-QmStepResult $Step.order 'Skipped' "本来就是 $Desired" ([string]$svc.Status))
    }
    try {
        if ($Desired -eq 'Stopped') {
            Stop-Service -Name $Step.name -Force -ErrorAction Stop
        } else {
            Start-Service -Name $Step.name -ErrorAction Stop
        }
        $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]$Desired, [TimeSpan]::FromSeconds(20))
        $now = Get-Service -Name $Step.name -ErrorAction Stop
        if ([string]$now.Status -ne $Desired) {
            return (New-QmStepResult $Step.order 'Failed' ("复读状态为 " + $now.Status) ([string]$now.Status))
        }
        return (New-QmStepResult $Step.order 'Done' '' ([string]$now.Status))
    } catch {
        return (New-QmStepResult $Step.order 'Failed' $_.Exception.Message)
    }
}

function ConvertTo-QmRegistryValue {
    param([string]$Value, [string]$Kind)
    switch ($Kind) {
        'DWord' { return [int]$Value }
        'QWord' { return [long]$Value }
        'MultiString' {
            if ($Value -eq '') { return @() }
            return @($Value -split "`n")
        }
        'Binary' {
            if ($Value -eq '') { return ,([byte[]]@()) }
            $bytes = New-Object 'System.Collections.Generic.List[byte]'
            for ($i = 0; $i + 1 -lt $Value.Length; $i += 2) {
                $bytes.Add([Convert]::ToByte($Value.Substring($i, 2), 16))
            }
            return ,($bytes.ToArray())
        }
        default { return [string]$Value }
    }
}

function Format-QmRegistryValue {
    param($Value, [string]$Kind)
    if ($null -eq $Value) { return '' }
    switch ($Kind) {
        'Binary' {
            $sb = New-Object System.Text.StringBuilder
            foreach ($b in [byte[]]$Value) { [void]$sb.Append($b.ToString('x2')) }
            return $sb.ToString()
        }
        'MultiString' { return (@($Value) -join "`n") }
        default { return [string]$Value }
    }
}

function Invoke-QmSetRegistryValue {
    param($Step)
    $psKey = Get-QmRegistryPsPath $Step.path
    try {
        if (-not (Test-Path -LiteralPath $psKey)) { New-Item -Path $psKey -Force -ErrorAction Stop | Out-Null }
        $val = ConvertTo-QmRegistryValue $Step.desiredValue $Step.kind
        New-ItemProperty -Path $psKey -Name $Step.name -Value $val -PropertyType $Step.kind -Force -ErrorAction Stop | Out-Null
        $actual = (Get-ItemProperty -Path $psKey -Name $Step.name -ErrorAction Stop).$($Step.name)
        $text = Format-QmRegistryValue $actual $Step.kind
        $expected = Format-QmRegistryValue (ConvertTo-QmRegistryValue $Step.desiredValue $Step.kind) $Step.kind
        if ($text -ne $expected) {
            return (New-QmStepResult $Step.order 'Failed' "复读为 $text，期望 $expected" $text)
        }
        return (New-QmStepResult $Step.order 'Done' '' $text)
    } catch {
        return (New-QmStepResult $Step.order 'Failed' $_.Exception.Message)
    }
}

function Invoke-QmDeleteRegistryValue {
    param($Step)
    $psKey = Get-QmRegistryPsPath $Step.path
    try {
        if (-not (Test-Path -LiteralPath $psKey)) {
            return (New-QmStepResult $Step.order 'Skipped' '键路径本来就不存在' '')
        }
        $existing = Get-ItemProperty -Path $psKey -Name $Step.name -ErrorAction SilentlyContinue
        if ($null -eq $existing) {
            return (New-QmStepResult $Step.order 'Skipped' '值本来就不存在' '')
        }
        Remove-ItemProperty -Path $psKey -Name $Step.name -ErrorAction Stop
        $after = Get-ItemProperty -Path $psKey -Name $Step.name -ErrorAction SilentlyContinue
        if ($null -ne $after) {
            return (New-QmStepResult $Step.order 'Failed' '删除后复读该值仍然存在' '')
        }
        return (New-QmStepResult $Step.order 'Done' '')
    } catch {
        return (New-QmStepResult $Step.order 'Failed' $_.Exception.Message)
    }
}

function Invoke-QmMaintenanceStep {
    param($Step)
    switch ([string]$Step.action) {
        'StopTask'           { return (Invoke-QmStopTask $Step) }
        'DisableTask'        { return (Invoke-QmSetTaskEnabled $Step $false) }
        'EnableTask'         { return (Invoke-QmSetTaskEnabled $Step $true) }
        'StopService'        { return (Invoke-QmServiceState $Step 'Stopped') }
        'StartService'       { return (Invoke-QmServiceState $Step 'Running') }
        'SetServiceStart'    { return (Invoke-QmServiceStartType $Step) }
        'SetRegistryValue'   { return (Invoke-QmSetRegistryValue $Step) }
        'DeleteRegistryValue'{ return (Invoke-QmDeleteRegistryValue $Step) }
        default {
            return (New-QmStepResult ([int]$Step.order) 'Failed' "未知动作：$($Step.action)")
        }
    }
}

Export-ModuleMember -Function Test-QmElevated, Get-QmTaskState, Invoke-QmMaintenanceStep, New-QmStepResult
