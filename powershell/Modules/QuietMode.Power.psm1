#Requires -Version 5.1
<#
    QuietMode.Power.psm1 — DISKIDLE（硬盘空闲停转）的唯一写入入口。

    为什么要单独一个模块：v2 之前 DISKIDLE 只由托盘 / qmctl spin set 写，
    reconcile 链完全不碰它 —— 于是配置里写着 20 分钟、系统里实际是 300 秒，
    谁也不知道。现在它进链了，写入和回读都必须走这里。

    三条硬规则：
      1. 只认回读值。powercfg 退出码 0 不等于设置生效（可能被方案夹到范围外）。
      2. 回读走 powercfg /query，失败再读注册表 —— 两条独立的读路径，
         与写入路径分开，避免「自己写的自己信」。
      3. 原生命令一律用参数数组（splatting），绝不用「带空格的整串」当单个参数。

    2.0.6 修复（2026-11-16）：v2.0.5 及以前的写法是
        $cmds = @("/setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE $sec", ...)
        foreach ($a in $cmds) { $null = & powercfg.exe $a 2>$null }
    PowerShell 不会把一个含空格的字符串拆成多个原生命令参数，powercfg 收到的是
    一整条 argv[1]，一律以「参数无效」退出 1；而 2>$null 把唯一的证据吞掉了。
    结果是：只要当前 AC 不是目标值，这一步就永远失败，只有别人恰好把值留成目标值
    时才「通过」—— 看起来像间歇性故障，实际是 100% 复现的调用缺陷。
    现在改成：真实活动方案 GUID + 参数数组 + stderr 合流 + 独立回读 + 分类错误码。
#>

$script:PowerCfgExe = Join-Path $env:SystemRoot 'System32\powercfg.exe'
$script:DiskSubGroup = '0012ee47-9041-4b5d-9b77-535fba8b1442'
$script:DiskIdleSetting = '6738e2c4-e8a5-4a42-b16a-e040e769756e'
$script:PowerSchemesKey = 'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes'
$script:GuidPattern = '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}'

function Invoke-QmPowerCfgNative {
    <#
    .SYNOPSIS
        调用一次 powercfg.exe。参数必须已经拆成数组。
    .OUTPUTS
        [ordered]@{ ok; exit; text; command; error }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $r = [ordered]@{ ok = $false; exit = -1; text = ''; command = ''; error = '' }
    $r.command = 'powercfg ' + ($Arguments -join ' ')

    # 调用方常把 $ErrorActionPreference 设成 Stop。原生命令把 stderr 合流到成功流时，
    # 那一条 ErrorRecord 在 Stop 语义下会变成终止错误，把「退出码 1」变成异常 —— 这里局部放宽。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # 2>&1 而不是 2>$null：powercfg 的诊断信息全在 stderr，扔掉它等于扔掉根因。
        $raw = @(& $script:PowerCfgExe @Arguments 2>&1)
        $code = $LASTEXITCODE
        $lines = New-Object System.Collections.ArrayList
        foreach ($item in $raw) {
            if ($null -eq $item) { continue }
            $s = [string]$item
            if ($s.Length -gt 0) { [void]$lines.Add($s) }
        }
        $r.text = ($lines -join "`n").Trim()
        if ($null -eq $code) {
            $r.error = 'powercfg 未返回退出码（可能未能启动）'
        } else {
            $r.exit = [int]$code
            $r.ok = ($r.exit -eq 0)
            if (-not $r.ok) {
                $r.error = "退出码 $($r.exit)" + $(if ($r.text) { "：$($r.text)" } else { '' })
            }
        }
    } catch {
        $r.error = '调用异常：' + $_.Exception.Message
    } finally {
        $ErrorActionPreference = $prevEap
    }
    return $r
}

function Get-QmActivePowerSchemeFromRegistry {
    <#
    .SYNOPSIS
        只读 HKLM 的 ActivePowerScheme。作为 /getactivescheme 的旁证，不代替它。
    #>
    [CmdletBinding()]
    param()

    try {
        $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($script:PowerSchemesKey)
        if ($k) {
            $v = [string]$k.GetValue('ActivePowerScheme')
            $k.Close()
            return $v.Trim().Trim('{', '}').ToLowerInvariant()
        }
    } catch { }
    return ''
}

function Get-QmActivePowerScheme {
    <#
    .SYNOPSIS
        解析当前活动电源方案 GUID（以 /getactivescheme 为准，注册表交叉验证）。

    .PARAMETER ConfirmDelayMs
        大于 0 时，隔这么久再探一次；两次一致才算稳定（写入前用，2.0.6 新增）。
        活动方案会被外部工具切换（实测 Process Lasso 会），所以「一次读到」不等于
        「写入期间还是这个」，宁可多花 1 秒。

    .OUTPUTS
        [ordered]@{ guid; name; registryGuid; consistent; source; error }
    #>
    [CmdletBinding()]
    param(
        [int]$ConfirmDelayMs = 0
    )

    $r = [ordered]@{
        guid = ''; name = ''; registryGuid = ''; consistent = $false
        source = ''; error = ''
    }

    $probe = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind getactive)
    if ($probe.ok) {
        $m = [regex]::Match($probe.text, $script:GuidPattern)
        if ($m.Success) {
            $r.guid = $m.Value.ToLowerInvariant()
            $r.source = 'powercfg'
            $n = [regex]::Match($probe.text, '\(([^()]+)\)\s*$')
            if ($n.Success) { $r.name = $n.Groups[1].Value.Trim() }
        } else {
            $r.error = 'getactivescheme 输出里没有 GUID：' + $probe.text
        }
    } else {
        $r.error = 'POWERCFG_ACTIVE_SCHEME_UNAVAILABLE：' + $probe.error
    }

    $r.registryGuid = Get-QmActivePowerSchemeFromRegistry

    if (-not $r.guid) { return $r }

    if ($ConfirmDelayMs -gt 0) {
        Start-Sleep -Milliseconds $ConfirmDelayMs
        $again = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind getactive)
        $g2 = ''
        if ($again.ok) {
            $mm = [regex]::Match($again.text, $script:GuidPattern)
            if ($mm.Success) { $g2 = $mm.Value.ToLowerInvariant() }
        }
        $r.consistent = ($g2 -eq $r.guid)
        if (-not $r.consistent) {
            $r.error = "活动方案在探测期间发生变化：$($r.guid) -> ${g2}"
        }
    }

    return $r
}

function New-QmPowerCfgArgumentSet {
    <#
    .SYNOPSIS
        构造一条 powercfg 命令的独立参数数组。

    .DESCRIPTION
        单独抽出来的唯一目的：让「一个参数里不许有空格」这件事可以被测试钉住。
        v2.0.5 的缺陷就是把五个参数写成了一整串字符串。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('setac', 'setdc', 'setactive', 'query', 'getactive')]
        [string]$Kind,
        [string]$SchemeGuid = '',
        [int]$Seconds = 0
    )

    $sub = $script:DiskSubGroup
    $setting = $script:DiskIdleSetting

    switch ($Kind) {
        'getactive' { return [string[]]@('/getactivescheme') }
        'query' { return [string[]]@('/query', $SchemeGuid, $sub, $setting) }
        'setac' { return [string[]]@('/setacvalueindex', $SchemeGuid, $sub, $setting, [string]$Seconds) }
        'setdc' { return [string[]]@('/setdcvalueindex', $SchemeGuid, $sub, $setting, [string]$Seconds) }
        'setactive' { return [string[]]@('/setactive', $SchemeGuid) }
    }
}

function Test-QmPowerElevated {
    <#
    .SYNOPSIS
        本模块自带的提权判定：单独 import 本模块时不必依赖 QuietMode.Core。
    #>
    [CmdletBinding()]
    param()

    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $pr = New-Object Security.Principal.WindowsPrincipal($id)
        return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Get-QmDiskIdleSeconds {
    <#
    .SYNOPSIS
        只读 DISKIDLE。返回 { ac, dc, captured, source, error, scheme }。
        ac/dc 为 -1 表示没读到 —— 这是「无证据」，不是「值等于 -1」。

    .PARAMETER SchemeGuid
        指定方案 GUID；不给就用当前活动方案。
    #>
    [CmdletBinding()]
    param(
        [string]$SchemeGuid = ''
    )

    $r = [ordered]@{
        ac = -1; dc = -1; captured = $false; source = ''; error = ''
        scheme = $SchemeGuid
    }

    if (-not $SchemeGuid) {
        $active = Get-QmActivePowerScheme
        $SchemeGuid = $active.guid
        $r.scheme = $SchemeGuid
        if (-not $SchemeGuid) {
            $r.error = 'POWERCFG_NOT_READY：' + $active.error
            return $r
        }
    }

    # 路径一：powercfg /query（十六进制索引，与语言无关）
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $q = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind query -SchemeGuid $SchemeGuid)
        if ($q.ok -and $q.text) {
            $hex = @()
            foreach ($m in [regex]::Matches($q.text, '0x([0-9a-fA-F]{1,8})')) {
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
            $r.error = 'query 输出里没有足够的 0x 数值'
        } elseif (-not $q.ok) {
            $r.error = 'query 失败：' + $q.error
        }
    } finally {
        $ErrorActionPreference = $prev
    }

    # 路径二：注册表（powercfg 不可用或被策略挡住时的兜底）
    try {
        $path = $script:PowerSchemesKey + '\' + $SchemeGuid + '\' + $script:DiskSubGroup + '\' + $script:DiskIdleSetting
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
                $r.error = ''
                return $r
            }
        }
        if (-not $r.error) { $r.error = 'powercfg 与注册表均未取到 DISKIDLE' }
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
        [ordered]@{ status='Done'|'Failed'|'Unknown'; code; seconds; ac; dc;
                    previousAc; previousDc; changed; scheme; schemeName;
                    attempts; exitAc; exitDc; exitActive; command; error }

    .NOTES
        code 取值：
          DISKIDLE_ALREADY_OK       原本就是目标值，未写入
          POWERCFG_APPLIED          写入 + 激活 + 同 GUID 回读全部通过
          POWERCFG_NOT_READY        活动方案取不到 / query 不通，未写入
          POWERCFG_WRITE_FAILED     写入命令非零退出
          POWERCFG_UNREADABLE       写入后回读不到（-1 只代表无证据）
          POWERCFG_VERIFY_MISMATCH  写入成功但回读与目标不符
          POWER_PLAN_CHANGED        写入过程中活动方案被外部切走
          ELEVATION_REQUIRED        非管理员
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Seconds
    )

    $out = [ordered]@{
        status = 'Failed'; code = ''; seconds = $Seconds; ac = -1; dc = -1
        previousAc = -1; previousDc = -1; changed = $false; scheme = ''; schemeName = ''
        attempts = 0; exitAc = -1; exitDc = -1; exitActive = -1; command = ''; error = ''
    }
    if ($Seconds -lt 0) { $Seconds = 0; $out.seconds = 0 }

    # QuietMode.Core 里的 Test-QmElevated 是唯一权威；单独 import 本模块时退回本地判定。
    $elevated = $false
    if (Get-Command Test-QmElevated -ErrorAction SilentlyContinue) {
        $elevated = [bool](Test-QmElevated)
    } else {
        $elevated = [bool](Test-QmPowerElevated)
    }
    if (-not $elevated) {
        $out.code = 'ELEVATION_REQUIRED'
        $out.error = '需要管理员权限才能修改 DISKIDLE'
        return $out
    }

    # 写入前先认准活动方案；2 秒复探一次，避免「读到 A、写到 B」。
    $active = Get-QmActivePowerScheme -ConfirmDelayMs 2000
    if (-not $active.guid) {
        $out.code = 'POWERCFG_NOT_READY'
        $out.status = 'Unknown'
        $out.error = '无法确定活动电源方案：' + $active.error
        return $out
    }

    $out.scheme = $active.guid
    $out.schemeName = $active.name

    $before = Get-QmDiskIdleSeconds -SchemeGuid $active.guid
    $out.previousAc = $before.ac
    $out.previousDc = $before.dc
    if (-not $before.captured) {
        $out.code = 'POWERCFG_NOT_READY'
        $out.status = 'Unknown'
        $out.error = '写入前读不到 DISKIDLE：' + $before.error
        return $out
    }

    # 已经是目标值就不折腾电源方案：setactive 会让某些机器闪一下屏。
    if ($before.ac -eq $Seconds -and ($before.dc -lt 0 -or $before.dc -eq $Seconds)) {
        $out.status = 'Done'
        $out.code = 'DISKIDLE_ALREADY_OK'
        $out.ac = $before.ac
        $out.dc = $before.dc
        return $out
    }

    $waits = @(2000, 5000)
    $lastCode = 'POWERCFG_WRITE_FAILED'
    $lastError = ''

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $out.attempts = $attempt

        # 每次重试都重新取 GUID：外部工具可能刚刚换过方案，旧 GUID 不能复用。
        $scheme = if ($attempt -eq 1) { $active.guid } else { (Get-QmActivePowerScheme).guid }
        if (-not $scheme) {
            $lastCode = 'POWERCFG_NOT_READY'
            $lastError = '重试时无法确定活动电源方案'
            break
        }
        $out.scheme = $scheme

        # 就绪判据：这个方案必须能被 /query 读通，否则不写。
        $probe = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind query -SchemeGuid $scheme)
        if (-not $probe.ok) {
            $lastCode = 'POWERCFG_NOT_READY'
            $lastError = 'POWERCFG_NOT_READY（' + $probe.command + '）：' + $probe.error
            if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $acArgs = New-QmPowerCfgArgumentSet -Kind setac -SchemeGuid $scheme -Seconds $Seconds
        $acRun = Invoke-QmPowerCfgNative -Arguments $acArgs
        $out.command = $acRun.command
        $out.exitAc = $acRun.exit
        if (-not $acRun.ok) {
            $lastCode = 'POWERCFG_WRITE_FAILED'
            $lastError = "POWERCFG_WRITE_FAILED AC（$($acRun.command)）：$($acRun.error)"
            if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $dcArgs = New-QmPowerCfgArgumentSet -Kind setdc -SchemeGuid $scheme -Seconds $Seconds
        $dcRun = Invoke-QmPowerCfgNative -Arguments $dcArgs
        $out.exitDc = $dcRun.exit
        if (-not $dcRun.ok) {
            $lastCode = 'POWERCFG_WRITE_FAILED'
            $lastError = "POWERCFG_WRITE_FAILED DC（$($dcRun.command)）：$($dcRun.error)"
            if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        # 写的是哪个方案，就激活哪个方案；如果它已经不是活动方案，别把旧方案抢回来。
        $nowGuid = (Get-QmActivePowerScheme).guid
        if ($nowGuid -and $nowGuid -ne $scheme) {
            $lastCode = 'POWER_PLAN_CHANGED'
            $lastError = "POWER_PLAN_CHANGED：写入期间活动方案从 $scheme 变成 $nowGuid，按新方案重写"
            if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $actRun = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind setactive -SchemeGuid $scheme)
        $out.exitActive = $actRun.exit
        if (-not $actRun.ok) {
            $lastCode = 'POWERCFG_WRITE_FAILED'
            $lastError = "POWERCFG_WRITE_FAILED setactive（$($actRun.command)）：$($actRun.error)"
            if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $after = Get-QmDiskIdleSeconds -SchemeGuid $scheme
        $out.ac = $after.ac
        $out.dc = $after.dc
        $out.changed = $true

        if (-not $after.captured) {
            $lastCode = 'POWERCFG_UNREADABLE'
            $lastError = 'POWERCFG_UNREADABLE：写入后回读不到 DISKIDLE（' + $after.error + '）'
            if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $okAc = ($after.ac -eq $Seconds)
        $okDc = ($after.dc -lt 0) -or ($after.dc -eq $Seconds)
        if ($okAc -and $okDc) {
            $out.status = 'Done'
            $out.code = 'POWERCFG_APPLIED'
            $out.error = ''
            return $out
        }

        $lastCode = 'POWERCFG_VERIFY_MISMATCH'
        $lastError = ("POWERCFG_VERIFY_MISMATCH：目标 {0}s，回读 AC={1}s DC={2}s（方案 $scheme）" -f $Seconds, $after.ac, $after.dc)
        if ($attempt -le $waits.Count) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
    }

    $out.code = $lastCode
    $out.error = $lastError
    if ($lastCode -eq 'POWERCFG_UNREADABLE' -or $lastCode -eq 'POWERCFG_NOT_READY') {
        # 读不到 ≠ 写失败：这是「没有证据」，不许伪装成失败，也不许伪装成成功。
        $out.status = 'Unknown'
    }
    return $out
}

Export-ModuleMember -Function Get-QmDiskIdleSeconds, Get-QmActivePowerScheme, Invoke-QmSpinDown
