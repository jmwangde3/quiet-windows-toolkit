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

# ---- 任务状态三态 ---------------------------------------------------------
#
# true / false / $null 必须严格区分：$null 是「读不到」，既不代表启用也不代表禁用。
# 旧实现用错误文本猜「任务不存在」，中文和英文 Windows 文案不同，猜错就会把
# 拒绝访问当成「任务不存在」，或者在读不到时直接跳过写入还报成功。
# 现在只认 COM 的 HRESULT 低位码（2/3 = 不存在，5 = 拒绝访问 = 未知），语言无关。

function ConvertFrom-QmTaskHResult {
    param([int64]$HResult)
    $low = $HResult -band 0xFFFF
    switch ($low) {
        2 { return 'NotPresent' }    # ERROR_FILE_NOT_FOUND
        3 { return 'NotPresent' }    # ERROR_PATH_NOT_FOUND
        default { return 'Unknown' } # 5 = 拒绝访问；其余一律「无法确认」
    }
}

function ConvertFrom-QmTaskStateCode {
    param($Code)
    switch ([int]$Code) {
        1 { return 'Disabled' }
        2 { return 'Queued' }
        3 { return 'Ready' }
        4 { return 'Running' }
        default { return 'Unknown' }
    }
}

function New-QmTaskState {
    param([string]$Status = 'Unknown', $Enabled = $null, [string]$State = '',
          [string]$Error = '', [string]$Source = '')
    $exists = $null
    if ($Status -eq 'Present') { $exists = $true }
    elseif ($Status -eq 'NotPresent') { $exists = $false }
    return [pscustomobject]@{
        status  = $Status      # Present | NotPresent | Unknown
        exists  = $exists      # 只有 Present / NotPresent 才有布尔值
        enabled = $Enabled     # $true / $false / $null（读不到）
        state   = $State
        error   = $Error
        source  = $Source
    }
}

function Get-QmTaskState {
    param([string]$TaskPath, [string]$TaskName)

    $svc = $null
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $folderPath = ($TaskPath -replace '^\\', '') -replace '\\$', ''
        if ($folderPath) { $folder = $svc.GetFolder($folderPath) } else { $folder = $svc.GetFolder('\') }
        $task = $folder.GetTask($TaskName)
        return (New-QmTaskState -Status 'Present' -Enabled ([bool]$task.Enabled) `
                    -State (ConvertFrom-QmTaskStateCode $task.State) -Source 'com')
    } catch {
        $hr = 0
        try { $hr = [int]$_.Exception.HResult } catch { $hr = 0 }
        if ($hr -ne 0) {
            return (New-QmTaskState -Status (ConvertFrom-QmTaskHResult $hr) -Error $_.Exception.Message -Source 'com')
        }
        # COM 拿不到（极少见）：退到 ScheduledTasks 模块，但只允许证明「存在」，
        # 失败一律算未知 —— 宁可说不知道，也不能说没有。
        try {
            $t = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction Stop
            return (New-QmTaskState -Status 'Present' -Enabled ([bool]$t.Settings.Enabled) `
                        -State ([string]$t.State) -Source 'cmdlet')
        } catch {
            return (New-QmTaskState -Status 'Unknown' -Error $_.Exception.Message -Source 'cmdlet')
        }
    } finally {
        if ($svc) {
            try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($svc) } catch { }
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
    if ($before.status -eq 'NotPresent') { return (New-QmStepResult $Step.order 'NotPresent' '') }
    if ($before.status -eq 'Unknown') {
        return (New-QmStepResult $Step.order 'Failed' ('无法确认任务状态，未执行停止：' + $before.error) '')
    }
    if ($before.state -ne 'Running') {
        return (New-QmStepResult $Step.order 'Skipped' '任务本来未运行' $before.state)
    }
    try {
        Stop-ScheduledTask -TaskPath $Step.path -TaskName $Step.name -ErrorAction Stop
        $after = Get-QmTaskState -TaskPath $Step.path -TaskName $Step.name
        if ($after.status -ne 'Present') {
            return (New-QmStepResult $Step.order 'Failed' ('停止后无法确认任务状态（' + $after.status + '）') '')
        }
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
    if ($before.status -eq 'NotPresent') { return (New-QmStepResult $Step.order 'NotPresent' '') }

    # 只有在「确实是已知状态且已经一致」时才跳过。$null 绝不能走进这里 ——
    # 那正是旧实现把「读不到」当成「本来就是禁用」的地方。
    if ($before.status -eq 'Present' -and $null -ne $before.enabled) {
        if ([bool]$before.enabled -eq $Enabled) {
            $word = '禁用'
            if ($Enabled) { $word = '启用' }
            return (New-QmStepResult $Step.order 'Skipped' ('本来就是' + $word) ('enabled=' + $before.enabled))
        }
    }

    try {
        if ($Enabled) {
            Enable-ScheduledTask -TaskPath $Step.path -TaskName $Step.name -ErrorAction Stop | Out-Null
        } else {
            Disable-ScheduledTask -TaskPath $Step.path -TaskName $Step.name -ErrorAction Stop | Out-Null
        }
        $after = Get-QmTaskState -TaskPath $Step.path -TaskName $Step.name
        if ($after.status -ne 'Present') {
            return (New-QmStepResult $Step.order 'Failed' ('写入后无法确认任务状态（' + $after.status + '）') '')
        }
        if ($null -eq $after.enabled) {
            return (New-QmStepResult $Step.order 'Failed' '写入后仍读不到 Enabled，按验证失败处理' '')
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

Export-ModuleMember -Function Test-QmElevated, Get-QmTaskState, New-QmTaskState, ConvertFrom-QmTaskHResult, ConvertFrom-QmTaskStateCode, Invoke-QmMaintenanceStep, New-QmStepResult
