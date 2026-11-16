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

function ConvertTo-QmPowerSchemeGuid {
    <#
    .SYNOPSIS
        把任意来源的 GUID 字符串规范成小写无花括号的 D 格式；不合法返回空串。

    .DESCRIPTION
        v2.0.6 第一版只做了 Trim('{','}') —— 那只是剥两端字符，不是验证。
        一个被污染的 ActivePowerScheme 值会被直接拼进注册表路径，读到别的键。
        2.0.6 加固：所有 GUID 必须先过这里，不合法就当「没有证据」。
    #>
    [CmdletBinding()]
    param([string]$Guid)

    if ([string]::IsNullOrWhiteSpace($Guid)) { return '' }
    $g = [guid]::Empty
    $s = ([string]$Guid).Trim().Trim('{', '}').Trim()
    if ([guid]::TryParse($s, [ref]$g)) { return $g.ToString('D').ToLowerInvariant() }
    return ''
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
            return ConvertTo-QmPowerSchemeGuid -Guid $v
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
        [ordered]@{ guid; name; registryGuid; registryMatch; consistent; stable; source; error }

    .NOTES
        consistent / stable 的区别（2.0.6 加固）：
          consistent   只有 ConfirmDelayMs > 0 时才有意义，表示两次探测 GUID 相同
          stable       本次探测结果是否可以作为写入依据 —— 没做复探时默认 true，
                       做了复探就必须两次一致
        调用方要写电源设置时必须判 stable，不能只看 guid 非空。
        第一版只判 guid 非空，于是「明确检测到方案正在被切走」依然会写。
    #>
    [CmdletBinding()]
    param(
        [int]$ConfirmDelayMs = 0
    )

    $r = [ordered]@{
        guid = ''; name = ''; registryGuid = ''; registryMatch = $false
        consistent = $false; stable = $false; source = ''; error = ''
    }

    $probe = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind getactive)
    if ($probe.ok) {
        $m = [regex]::Match($probe.text, $script:GuidPattern)
        if ($m.Success) {
            $r.guid = ConvertTo-QmPowerSchemeGuid -Guid $m.Value
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
    $r.registryMatch = [bool]($r.guid -and $r.registryGuid -and ($r.guid -eq $r.registryGuid))

    if (-not $r.guid) { return $r }

    if ($ConfirmDelayMs -gt 0) {
        Start-Sleep -Milliseconds $ConfirmDelayMs
        $again = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind getactive)
        $g2 = ''
        if ($again.ok) {
            $mm = [regex]::Match($again.text, $script:GuidPattern)
            if ($mm.Success) { $g2 = ConvertTo-QmPowerSchemeGuid -Guid $mm.Value }
        }
        $r.consistent = [bool]($g2 -and ($g2 -eq $r.guid))
        $r.stable = $r.consistent
        if (-not $r.consistent) {
            $r.error = "活动方案在探测期间不稳定：$($r.guid) -> $g2"
        }
    } else {
        # 没做复探：结论只代表「读到了」，写入方必须自己承担「读到这里可能已经变了」的风险。
        $r.stable = $true
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
        ac = -1; dc = -1; captured = $false; acCaptured = $false; dcCaptured = $false
        source = ''; error = ''; scheme = ''
    }

    if (-not $SchemeGuid) {
        $active = Get-QmActivePowerScheme
        $SchemeGuid = $active.guid
        if (-not $SchemeGuid) {
            $r.error = 'POWERCFG_NOT_READY：' + $active.error
            return $r
        }
    } else {
        $SchemeGuid = ConvertTo-QmPowerSchemeGuid -Guid $SchemeGuid
        if (-not $SchemeGuid) {
            $r.error = 'POWERCFG_NOT_READY：方案 GUID 非法'
            return $r
        }
    }
    $r.scheme = $SchemeGuid

    # 路径一：powercfg /query（十六进制索引，与语言无关）
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $q = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind query -SchemeGuid $SchemeGuid)
        if ($q.ok -and $q.text) {
            # 只有输出里确实出现目标设置 GUID，才能认定这一段是 DISKIDLE 单个设置的块。
            # 若第三参数被某个 powercfg 版本忽略（退化成整个 SUB_DISK 子组），
            # 「取最后两个 0x」会静默拿到别的设置的值 —— 那比读不到更糟。
            if ($q.text -notmatch [regex]::Escape($script:DiskIdleSetting)) {
                $r.error = 'query 输出里没有 DISKIDLE 设置块（setting 参数可能被忽略）'
            } else {
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
                    $r.acCaptured = $true
                    $r.dcCaptured = $true
                    $r.source = 'powercfg'
                    return $r
                }
                $r.error = 'query 输出里没有足够的 0x 数值'
            }
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
            if ($null -ne $a) { $r.ac = [int]$a; $r.acCaptured = $true }
            if ($null -ne $d) { $r.dc = [int]$d; $r.dcCaptured = $true }
            $k2.Close()
            # 只读到一侧也算 captured —— 但调用方必须逐侧判，不能拿「整体 captured」代替「这个值可信」。
            if ($r.acCaptured -or $r.dcCaptured) {
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
        code 取值（→ 后面的 status）：
          DISKIDLE_ALREADY_OK       原本就是目标值，未写入            → Done
          POWERCFG_APPLIED          写入 + 激活 + 同 GUID 回读通过      → Done
          POWERCFG_NOT_READY        活动方案取不到 / query 不通，未写入  → Unknown
          POWERCFG_WRITE_FAILED     写入命令非零退出                    → Failed
          POWERCFG_UNREADABLE       写入后回读不到（-1 只代表无证据）   → Unknown
          POWERCFG_VERIFY_MISMATCH  写入成功但回读与目标不符            → Failed
          POWER_PLAN_CHANGED        活动方案被外部切走 / 探测期间不稳定  → Unknown
          INVALID_SECONDS           入参非法                              → Failed
          ELEVATION_REQUIRED        非管理员                              → Failed

        2.0.6 加固点（外部审查后修）：
          - 活动方案探测结果必须 stable 才允许写入，不再只看 guid 非空
          - 读不到当前活动方案时禁止 setactive（原实现会短路继续，把旧方案抢回来）
          - 激活后回读通过，还要再确认活动方案仍是目标方案，否则本轮不算成功
          - AC 读不到（-1）与 AC 读到了但不等于目标，是两件事，前者是 Unknown
          - changed 在第一次写成功时就置位，不再等到回读之后
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
    # 负数不能静默变 0：0 的语义是「永不停止」，把配置错误变成永不停止是最坏的结果。
    if ($Seconds -lt 0) {
        $out.status = 'Failed'
        $out.code = 'INVALID_SECONDS'
        $out.error = "DISKIDLE 秒数不能为负数：$Seconds"
        return $out
    }

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
    # 注意判的是 stable 不是「guid 非空」：复探发现方案正在被切走时，guid 仍然非空，
    # 但此时写入必然打偏 —— 这种「已经知道不稳定还照写」是比读不到更糟的失败。
    $active = Get-QmActivePowerScheme -ConfirmDelayMs 2000
    if (-not $active.guid) {
        $out.code = 'POWERCFG_NOT_READY'
        $out.status = 'Unknown'
        $out.error = '无法确定活动电源方案：' + $active.error
        return $out
    }
    if (-not $active.stable) {
        $out.code = 'POWER_PLAN_CHANGED'
        $out.status = 'Unknown'
        $out.scheme = $active.guid
        $out.schemeName = $active.name
        $out.error = '活动方案在探测期间不稳定，本轮不写入：' + $active.error
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
    # 上限从等待表推导，避免以后改等待次数忘了同步循环次数。
    $maxAttempts = $waits.Count + 1
    $lastCode = 'POWERCFG_WRITE_FAILED'
    $lastError = ''

    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $out.attempts = $attempt

        # 每次重试都重新取 GUID：外部工具可能刚刚换过方案，旧 GUID 不能复用。
        $scheme = if ($attempt -eq 1) { $active.guid } else { (Get-QmActivePowerScheme).guid }
        if (-not $scheme) {
            $lastCode = 'POWERCFG_NOT_READY'
            $lastError = "第 $attempt 次尝试时无法确定活动电源方案"
            break
        }
        $out.scheme = $scheme

        # 就绪判据：这个方案必须能被 /query 读通，否则不写。
        $probe = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind query -SchemeGuid $scheme)
        if (-not $probe.ok) {
            $lastCode = 'POWERCFG_NOT_READY'
            $lastError = 'POWERCFG_NOT_READY（' + $probe.command + '）：' + $probe.error
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $acArgs = New-QmPowerCfgArgumentSet -Kind setac -SchemeGuid $scheme -Seconds $Seconds
        $acRun = Invoke-QmPowerCfgNative -Arguments $acArgs
        $out.command = $acRun.command
        $out.exitAc = $acRun.exit
        if (-not $acRun.ok) {
            $lastCode = 'POWERCFG_WRITE_FAILED'
            $lastError = "POWERCFG_WRITE_FAILED AC（$($acRun.command)）：$($acRun.error)"
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }
        # 第一次写成功就意味着系统状态已经被动过 —— 后续即便失败，也不能对外报「什么都没改」。
        $out.changed = $true

        $dcArgs = New-QmPowerCfgArgumentSet -Kind setdc -SchemeGuid $scheme -Seconds $Seconds
        $dcRun = Invoke-QmPowerCfgNative -Arguments $dcArgs
        $out.exitDc = $dcRun.exit
        if (-not $dcRun.ok) {
            $lastCode = 'POWERCFG_WRITE_FAILED'
            $lastError = "POWERCFG_WRITE_FAILED DC（$($dcRun.command)）：$($dcRun.error)"
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        # 激活之前必须能读到当前活动方案。读不到就什么都不做。
        # 原实现写成 `if ($nowGuid -and $nowGuid -ne $scheme)`：读不到时条件为假，
        # 会径直 setactive 旧 GUID —— 等于在不知道当前方案的情况下把旧方案抢回来。
        $now = Get-QmActivePowerScheme
        if (-not $now.guid) {
            $lastCode = 'POWERCFG_NOT_READY'
            $lastError = '无法确认当前活动方案，拒绝激活（避免把旧方案抢回来）'
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }
        if ($now.guid -ne $scheme) {
            $lastCode = 'POWER_PLAN_CHANGED'
            $lastError = "POWER_PLAN_CHANGED：写入期间活动方案从 $scheme 变成 $($now.guid)，按新方案重写"
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $actRun = Invoke-QmPowerCfgNative -Arguments (New-QmPowerCfgArgumentSet -Kind setactive -SchemeGuid $scheme)
        $out.exitActive = $actRun.exit
        if (-not $actRun.ok) {
            $lastCode = 'POWERCFG_WRITE_FAILED'
            $lastError = "POWERCFG_WRITE_FAILED setactive（$($actRun.command)）：$($actRun.error)"
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $after = Get-QmDiskIdleSeconds -SchemeGuid $scheme
        $out.ac = $after.ac
        $out.dc = $after.dc

        if (-not $after.captured) {
            $lastCode = 'POWERCFG_UNREADABLE'
            $lastError = 'POWERCFG_UNREADABLE：写入后回读不到 DISKIDLE（' + $after.error + '）'
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        # 回读通过还不够：必须确认现在真的还在这个方案上。
        # 否则「A 写好了 → A 被激活 → 立刻被切到 B → 程序读 A 发现是 1200」会被报成成功，
        # 而用户实际跑的 B 根本没改过。
        $final = Get-QmActivePowerScheme
        if (-not $final.guid) {
            $lastCode = 'POWERCFG_UNREADABLE'
            $lastError = '回读后无法确认活动方案（' + $final.error + '）'
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }
        if ($final.guid -ne $scheme) {
            $lastCode = 'POWER_PLAN_CHANGED'
            $lastError = "POWER_PLAN_CHANGED：验证期间活动方案从 $scheme 变为 $($final.guid)"
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        # 三件事必须分开，不能混成一个布尔：
        #   读到了但不等于目标 → 真没写进去 / 被夹到范围外 → MISMATCH（Failed）
        #   根本没读到（-1）   → 没有证据               → UNREADABLE（Unknown）
        #   读到了且等于目标   → 通过
        $knownMismatch = ($after.acCaptured -and ($after.ac -ne $Seconds)) -or
                         ($after.dcCaptured -and ($after.dc -ne $Seconds))
        if ($knownMismatch) {
            $lastCode = 'POWERCFG_VERIFY_MISMATCH'
            $lastError = ("POWERCFG_VERIFY_MISMATCH：目标 {0}s，回读 AC={1}s DC={2}s（方案 $scheme）" -f $Seconds, $after.ac, $after.dc)
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }
        if (-not $after.acCaptured) {
            # DC 读到了但 AC 没读到：写入可能成功也可能没成功，不能报 Done。
            $lastCode = 'POWERCFG_UNREADABLE'
            $lastError = ("POWERCFG_UNREADABLE：AC 索引读不到（DC={0}s），无法证明写入生效" -f $after.dc)
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds $waits[$attempt - 1] }
            continue
        }

        $out.status = 'Done'
        $out.code = 'POWERCFG_APPLIED'
        $out.error = ''
        return $out
    }

    $out.code = $lastCode
    $out.error = $lastError
    if ($lastCode -eq 'POWERCFG_UNREADABLE' -or $lastCode -eq 'POWERCFG_NOT_READY' -or $lastCode -eq 'POWER_PLAN_CHANGED') {
        # 读不到 / 被别人切走 ≠ 功能坏了：这是「没有证据」或「外部干扰，下一轮再来」。
        # 不许伪装成失败（托盘不该为此亮红叹号），也不许伪装成成功。
        $out.status = 'Unknown'
    }
    return $out
}

Export-ModuleMember -Function Get-QmDiskIdleSeconds, Get-QmActivePowerScheme, Invoke-QmSpinDown
