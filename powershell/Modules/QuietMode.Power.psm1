#Requires -Version 5.1
<#
    QuietMode.Power.psm1 — DISKIDLE（硬盘空闲停转）的唯一写入入口。

    为什么要单独一个模块：v2 之前 DISKIDLE 只由托盘 / qmctl spin set 写，
    reconcile 链完全不碰它 —— 于是配置里写着 20 分钟、系统里实际是 300 秒，
    谁也不知道。现在它进链了，写入和回读都必须走这里。

    两条硬规则：
      1. 只认回读值。powercfg 退出码 0 不等于设置生效（可能被方案夹到范围外）。
      2. 回读走 powercfg /query，失败再读注册表 —— 两条独立的读路径，
         与写入路径分开，避免「自己写的自己信」。
#>

$script:DiskSubGroup = '0012ee47-9041-4b5d-9b77-535fba8b1442'
$script:DiskIdleSetting = '6738e2c4-e8a5-4a42-b16a-e040e769756e'
$script:PowerSchemesKey = 'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes'

function Get-QmDiskIdleSeconds {
    <#
    .SYNOPSIS
        只读 DISKIDLE。返回 { ac, dc, captured, source, error }。
        ac/dc 为 -1 表示没读到。
    #>
    [CmdletBinding()]
    param()

    $r = [ordered]@{ ac = -1; dc = -1; captured = $false; source = ''; error = '' }

    # 路径一：powercfg /query（十六进制索引，与语言无关）
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = ''
        try { $text = (& powercfg.exe /query SCHEME_CURRENT SUB_DISK DISKIDLE 2>$null | Out-String) } catch { $text = '' }
        if ($text -and $text.Trim().Length -gt 0) {
            $hex = @()
            foreach ($m in [regex]::Matches($text, '0x([0-9a-fA-F]{1,8})')) {
                $v = 0
                if ([int]::TryParse($m.Groups[1].Value, [System.Globalization.NumberStyles]::HexNumber, $null, [ref]$v)) {
                    $hex += $v
                }
            }
            # 输出顺序：最小 / 最大 / 增量 / 当前交流 / 当前直流
            if ($hex.Count -ge 2) {
                $r.ac = $hex[$hex.Count - 2]
                $r.dc = $hex[$hex.Count - 1]
                $r.captured = $true
                $r.source = 'powercfg'
                return $r
            }
        }
    } finally {
        $ErrorActionPreference = $prev
    }

    # 路径二：注册表（powercfg 不可用或被策略挡住时的兜底）
    try {
        $scheme = ''
        $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($script:PowerSchemesKey)
        if ($k) {
            $scheme = [string]$k.GetValue('ActivePowerScheme')
            $k.Close()
        }
        if ($scheme) {
            $path = $script:PowerSchemesKey + '\' + $scheme + '\' + $script:DiskSubGroup + '\' + $script:DiskIdleSetting
            $k2 = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path)
            if ($k2) {
                $a = $k2.GetValue('ACSettingIndex')
                $d = $k2.GetValue('DCSettingIndex')
                if ($null -ne $a) { $r.ac = [int]$a }
                if ($null -ne $d) { $r.dc = [int]$d }
                $k2.Close()
                if ($r.ac -ge 0 -or $r.dc -ge 0) {
                    $r.captured = $true
                    $r.source = 'registry'
                    return $r
                }
            }
        }
        $r.error = 'powercfg 与注册表均未取到 DISKIDLE'
    } catch {
        $r.error = '读取 DISKIDLE 失败：' + $_.Exception.Message
    }
    return $r
}

function Invoke-QmSpinDown {
    <#
    .SYNOPSIS
        写入 DISKIDLE 并回读校验。

    .PARAMETER Seconds
        目标秒数；0 表示「永不」。负数按 0 处理。

    .OUTPUTS
        [ordered]@{ status='Done'|'Failed'; seconds; ac; dc; previousAc; previousDc; error }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Seconds
    )

    $out = [ordered]@{
        status = 'Failed'; seconds = $Seconds; ac = -1; dc = -1
        previousAc = -1; previousDc = -1; changed = $false; error = ''
    }
    if ($Seconds -lt 0) { $Seconds = 0; $out.seconds = 0 }

    if (-not (Test-QmElevated)) {
        $out.error = '需要管理员权限才能修改 DISKIDLE'
        return $out
    }

    $before = Get-QmDiskIdleSeconds
    $out.previousAc = $before.ac
    $out.previousDc = $before.dc

    # 已经是目标值就不折腾电源方案：setactive 会让某些机器闪一下屏。
    if ($before.captured -and $before.ac -eq $Seconds -and ($before.dc -lt 0 -or $before.dc -eq $Seconds)) {
        $out.status = 'Done'
        $out.ac = $before.ac
        $out.dc = $before.dc
        return $out
    }

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $sec = [string]$Seconds
        $cmds = @(
            "/setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec",
            "/setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec",
            "/setactive SCHEME_CURRENT"
        )
        foreach ($a in $cmds) {
            $null = & powercfg.exe $a 2>$null
            if ($LASTEXITCODE -ne 0) {
                $out.error = "powercfg 退出码 $($LASTEXITCODE)：$a"
                return $out
            }
        }
    } catch {
        $out.error = 'powercfg 调用异常：' + $_.Exception.Message
        return $out
    } finally {
        $ErrorActionPreference = $prev
    }

    $after = Get-QmDiskIdleSeconds
    $out.ac = $after.ac
    $out.dc = $after.dc
    $out.changed = $true

    if (-not $after.captured) {
        $out.error = '写入后无法回读 DISKIDLE：' + $after.error
        return $out
    }
    $okAc = ($after.ac -eq $Seconds)
    $okDc = ($after.dc -lt 0) -or ($after.dc -eq $Seconds)
    if ($okAc -and $okDc) {
        $out.status = 'Done'
        return $out
    }
    $out.error = ("回读与目标不一致（目标 {0}s，AC={1}s DC={2}s）；" -f $Seconds, $after.ac, $after.dc) +
                 '电源方案可能把该值夹到支持范围内'
    return $out
}

Export-ModuleMember -Function Get-QmDiskIdleSeconds, Invoke-QmSpinDown
