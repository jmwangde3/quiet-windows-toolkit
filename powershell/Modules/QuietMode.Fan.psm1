#Requires -Version 5.1
<#
    QuietMode.Fan.psm1 — 风扇配置切换的唯一写入入口。

    v1 注释里写「FanControl 未暴露当前配置名」，实测不成立：
        IpcCtl.exe list  ->  current=FanControl.json folder=<FAN_ROOT>\Configurations
    所以 v2 每次切换后都回读 current=，对不上就报失败，不再靠 silent_active.flag 猜。
#>

function Get-QmFanCurrent {
    [CmdletBinding()]
    param([string]$IpcPath)

    $r = [ordered]@{ available = $false; current = ''; error = '' }
    if (-not $IpcPath -or -not (Test-Path -LiteralPath $IpcPath)) {
        $r.error = "IpcCtl.exe 不存在：$IpcPath"
        return $r
    }
    try {
        $out = & $IpcPath list 2>&1 | Out-String
    } catch {
        $r.error = "IpcCtl list 失败：$($_.Exception.Message)"
        return $r
    }
    foreach ($line in ($out -split "`r?`n")) {
        $s = $line.Trim()
        if ($s -like 'current=*') {
            $v = $s.Substring(8).Trim()
            $sp = $v.IndexOfAny([char[]]@(' ', "`t"))
            if ($sp -gt 0) { $v = $v.Substring(0, $sp) }
            $r.available = $true
            $r.current = $v
            return $r
        }
    }
    $r.error = 'IpcCtl list 没有返回 current=（FanControl 可能没运行）'
    return $r
}

function Invoke-QmFanSwitch {
    <#
        切换 FanControl 配置并回读验证。
        -Mode silent|normal 决定目标配置；切换后 current= 必须等于目标文件名。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('silent', 'normal')][string]$Mode,
        [Parameter(Mandatory = $true)][string]$IpcPath,
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$ControlExe = '',
        [string]$FlagPath = ''
    )

    $result = [ordered]@{
        mode = $Mode; profile = ''; status = 'Failed'; error = ''
        previous = ''; current = ''; verified = $false
    }

    if (-not (Test-Path -LiteralPath $Profile)) {
        $result.error = "配置文件不存在：$Profile"
        return $result
    }
    $result.profile = Split-Path -Leaf $Profile

    $before = Get-QmFanCurrent -IpcPath $IpcPath
    if ($before.available) { $result.previous = $before.current }

    # FanControl 没运行就拉起来（用户态即可，不弹 UAC）
    $running = @(Get-Process -Name 'FanControl' -ErrorAction SilentlyContinue).Count -gt 0
    if (-not $running) {
        if ($ControlExe -and (Test-Path -LiteralPath $ControlExe)) {
            try { Start-Process -FilePath $ControlExe | Out-Null; Start-Sleep -Seconds 3 }
            catch { $result.error = "启动 FanControl 失败：$($_.Exception.Message)"; return $result }
        } else {
            $result.error = "FanControl 未运行，且 controlExe 不存在：$ControlExe"
            return $result
        }
    }

    try {
        $out = & $IpcPath load $Profile 2>&1 | Out-String
    } catch {
        $result.error = "IpcCtl load 失败：$($_.Exception.Message)"
        return $result
    }

    # 回读：IPC 是异步的，给一点时间
    $want = Split-Path -Leaf $Profile
    $current = ''
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep -Milliseconds 300
        $now = Get-QmFanCurrent -IpcPath $IpcPath
        if (-not $now.available) { continue }
        $current = $now.current
        if ($current -eq $want) { break }
    }
    $result.current = $current

    if ($current -eq $want) {
        $result.status = 'Done'
        $result.verified = $true
        if ($FlagPath) {
            try {
                if ($Mode -eq 'silent') {
                    [System.IO.File]::WriteAllText($FlagPath, '(active)', (New-Object System.Text.ASCIIEncoding))
                } elseif (Test-Path -LiteralPath $FlagPath) {
                    Remove-Item -LiteralPath $FlagPath -Force
                }
            } catch { }
        }
    } else {
        $result.error = "切换后回读不一致：期望 $want，实际 $(if ($current) { $current } else { '(未知)' })；IPC 输出：$($out.Trim())"
    }
    return $result
}

Export-ModuleMember -Function Get-QmFanCurrent, Invoke-QmFanSwitch
