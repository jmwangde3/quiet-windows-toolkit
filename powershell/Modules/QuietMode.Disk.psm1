#Requires -Version 5.1
<#
    QuietMode.Disk.psm1 — 磁盘身份解析、安全闸门、脱机/联机执行与回读验证。

    设计原则（对应 docs/V2_SPEC.md §3）：
      · 唯一身份 = UniqueId / SerialNumber，磁盘编号只作运行时线索，永不用于匹配；
      · 任何「多命中 / 无身份 / 字段冲突 / 状态未知」都拒绝操作；
      · 系统盘、启动盘、页面文件所在盘、存储池成员一律禁止脱机，force 越不过；
      · 操作后必须重新查询并回读 IsOffline，不一致即失败；
      · 只读探测与修改分离，探测不产生副作用。
#>

$ErrorActionPreference = 'Stop'

function ConvertTo-QmId {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return $Value.Trim().ToUpperInvariant()
}

function ConvertTo-QmSerial {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value -replace '[\s\x00]', '').ToUpperInvariant()
}

function ConvertTo-QmName {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return (($Value -replace "`0", ' ') -replace '\s+', ' ').Trim()
}

function Format-QmSize {
    param([long]$Bytes)
    if ($Bytes -le 0) { return '0' }
    $units = @('B','KB','MB','GB','TB','PB')
    $v = [double]$Bytes
    $i = 0
    while ($v -ge 1024 -and $i -lt ($units.Count - 1)) { $v = $v / 1024; $i++ }
    if ($i -eq 0) { return ('{0} {1}' -f [int]$v, $units[$i]) }
    return ('{0:0.##} {1}' -f $v, $units[$i])
}

# ---------------------------------------------------------------- 清单

function Get-QmDiskInventory {
    [CmdletBinding()]
    param()
    $disks = @(Get-Disk -ErrorAction Stop)
    $out = @()
    foreach ($d in $disks) {
        $out += [pscustomobject]@{
            number       = [int]$d.Number
            uniqueId     = [string]$d.UniqueId
            serialNumber = [string]$d.SerialNumber
            friendlyName = [string]$d.FriendlyName
            sizeBytes    = [long]$d.Size
            busType      = [string]$d.BusType
            isOffline    = [bool]$d.IsOffline
            isReadOnly   = [bool]$d.IsReadOnly
            isSystem     = [bool]$d.IsSystem
            isBoot       = [bool]$d.IsBoot
            healthStatus = [string]$d.HealthStatus
            partitionStyle = [string]$d.PartitionStyle
        }
    }
    return $out
}

# ---------------------------------------------------------------- 身份解析

<#
    返回对象：
      status  : matched | not-found | ambiguous | no-identity | conflict
      disk    : 命中的磁盘对象（仅 matched 时非空）
      reason  : 人类可读原因
      candidates : 参与比较的磁盘编号
      notes   : 告警性说明
#>
function Resolve-QmDiskIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Target,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] $Disks
    )

    $result = [pscustomobject]@{
        status = 'not-found'; disk = $null; reason = ''; candidates = @(); notes = @()
    }
    if ($null -eq $Target) {
        $result.status = 'no-identity'; $result.reason = '未提供磁盘目标'; return $result
    }
    if ($null -eq $Disks -or @($Disks).Count -eq 0) {
        $result.reason = '当前无法枚举磁盘'; return $result
    }

    $wantUnique = ConvertTo-QmId $Target.uniqueId
    $wantSerial = ConvertTo-QmSerial $Target.serialNumber
    if ($wantUnique -eq '' -and $wantSerial -eq '') {
        $result.status = 'no-identity'
        $result.reason = '配置中没有 uniqueId / serialNumber，无法唯一确认身份'
        return $result
    }

    $hits = @()
    $byUnique = $false
    if ($wantUnique -ne '') {
        $hits = @($Disks | Where-Object { (ConvertTo-QmId $_.uniqueId) -eq $wantUnique })
        if ($hits.Count -gt 0) { $byUnique = $true }
    }
    if ($hits.Count -eq 0 -and $wantSerial -ne '') {
        $hits = @($Disks | Where-Object { (ConvertTo-QmSerial $_.serialNumber) -eq $wantSerial })
        if ($hits.Count -gt 0) { $result.notes += 'uniqueId 未命中，改用序列号匹配（较弱身份）' }
    }

    if ($hits.Count -eq 0) {
        $result.status = 'not-found'
        $result.reason = ('未找到匹配磁盘（uniqueId={0} serial={1}）' -f $Target.uniqueId, $Target.serialNumber)
        return $result
    }

    $result.candidates = @($hits | ForEach-Object { [int]$_.number })
    if ($hits.Count -gt 1) {
        $result.status = 'ambiguous'
        $result.reason = ('有 {0} 块磁盘同时匹配该身份（编号 {1}），拒绝操作' -f $hits.Count, ($result.candidates -join ', '))
        return $result
    }

    $disk = $hits[0]
    $result.disk = $disk

    # 辅助字段复核
    $haveSerial = ConvertTo-QmSerial $disk.serialNumber
    if ($wantSerial -ne '' -and $haveSerial -ne '' -and $haveSerial -ne $wantSerial) {
        $result.status = 'conflict'
        $result.reason = ('序列号不一致（配置 {0}，实际 {1}）' -f $Target.serialNumber, $disk.serialNumber)
        return $result
    }
    if ($wantSerial -ne '' -and $haveSerial -eq '') { $result.notes += '实际磁盘读不到序列号，无法复核' }

    if ([long]$Target.sizeBytes -gt 0 -and [long]$disk.sizeBytes -gt 0) {
        $diff = [Math]::Abs([long]$Target.sizeBytes - [long]$disk.sizeBytes)
        if ($diff -gt 1048576) {
            $result.status = 'conflict'
            $result.reason = ('容量不一致（配置 {0}，实际 {1}）' -f (Format-QmSize $Target.sizeBytes), (Format-QmSize $disk.sizeBytes))
            return $result
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Target.busType) -and -not [string]::IsNullOrWhiteSpace($disk.busType)) {
        if ($Target.busType.Trim() -ne ([string]$disk.busType).Trim()) {
            $result.status = 'conflict'
            $result.reason = ('总线类型不一致（配置 {0}，实际 {1}）' -f $Target.busType, $disk.busType)
            return $result
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Target.friendlyName) -and -not [string]::IsNullOrWhiteSpace($disk.friendlyName)) {
        if ((ConvertTo-QmName $Target.friendlyName) -ne (ConvertTo-QmName $disk.friendlyName)) {
            $result.notes += ('型号串不一致（配置 {0}，实际 {1}），已按身份字段判定为同一磁盘' -f $Target.friendlyName.Trim(), $disk.friendlyName.Trim())
        }
    }

    if (-not $byUnique) { $result.notes += '本次匹配基于序列号，建议在设置中补写 uniqueId' }

    $result.status = 'matched'
    $result.reason = ('唯一命中磁盘 #{0}{1}' -f $disk.number, $(if ($byUnique) { '（uniqueId）' } else { '（serialNumber）' }))
    return $result
}

# ---------------------------------------------------------------- 卷与占用

function Get-QmDiskVolumes {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][int]$DiskNumber)

    $vols = @()
    $parts = @(Get-Partition -DiskNumber $DiskNumber -ErrorAction SilentlyContinue)
    foreach ($p in $parts) {
        $letter = ''
        if ($p.DriveLetter) { $letter = ([string]$p.DriveLetter).ToUpperInvariant() + ':' }
        $label = ''; $fs = ''; $free = 0; $path = ''
        if ($letter -ne '') {
            $v = Get-Volume -DriveLetter $p.DriveLetter -ErrorAction SilentlyContinue
            if ($v) {
                $label = [string]$v.FileSystemLabel
                $fs = [string]$v.FileSystem
                $free = [long]$v.SizeRemaining
                $path = [string]$v.Path
            }
        }
        $vols += [pscustomobject]@{
            driveLetter = $letter
            label       = $label
            fileSystem  = $fs
            uniqueId    = $path
            sizeBytes   = [long]$p.Size
            freeBytes   = $free
            isSystem    = [bool]$p.IsSystem
            isBoot      = [bool]$p.IsBoot
        }
    }
    return $vols
}

function Get-QmPageFileInfo {
    [CmdletBinding()]
    param()
    $auto = $null
    try {
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
        $auto = [bool]$cs.AutomaticManagedPagefile
    } catch { $auto = $null }

    $paths = @()
    try {
        $paths = @(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction Stop |
                   ForEach-Object { [string]$_.Name } |
                   Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    } catch { }

    # 注册表里的 PagingFiles（含自动管理时的实际路径）
    try {
        $reg = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' -Name PagingFiles -ErrorAction Stop
        foreach ($entry in @($reg.PagingFiles)) {
            if ([string]::IsNullOrWhiteSpace($entry)) { continue }
            $p = ($entry -split '\s+')[0]
            if ($p -and ($paths -notcontains $p)) { $paths += $p }
        }
    } catch { }

    return [pscustomobject]@{ automaticManaged = $auto; paths = @($paths) }
}

function Get-QmCrashDumpInfo {
    [CmdletBinding()]
    param()
    $dumpFile = ''; $enabled = $true; $code = -1
    try {
        $cc = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop
        if ($null -ne $cc.DumpFile) { $dumpFile = [string]$cc.DumpFile }
        if ($null -ne $cc.CrashDumpEnabled) { $code = [int]$cc.CrashDumpEnabled; $enabled = ($code -ne 0) }
    } catch { }
    if ($dumpFile -ne '') {
        $dumpFile = $dumpFile.Replace('%SystemRoot%', $env:SystemRoot).Replace('%SystemDrive%', $env:SystemDrive)
    }
    return [pscustomobject]@{ dumpFile = $dumpFile; enabled = $enabled; crashDumpEnabled = $code }
}

function Get-QmStoragePoolInfo {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] $Disk)

    $info = [pscustomobject]@{ isMember = $false; poolName = ''; reason = ''; detected = $false }
    $pools = @()
    try {
        $pools = @(Get-StoragePool -IsPrimordial $false -ErrorAction Stop)
        $info.detected = $true
    } catch {
        $info.reason = '存储池信息读取失败：' + $_.Exception.Message
        return $info
    }
    if ($pools.Count -eq 0) { return $info }

    $wantSerial = ConvertTo-QmSerial $Disk.serialNumber
    foreach ($pool in $pools) {
        $members = @()
        try { $members = @($pool | Get-PhysicalDisk -ErrorAction Stop) } catch { continue }
        foreach ($m in $members) {
            if ($wantSerial -ne '' -and (ConvertTo-QmSerial $m.SerialNumber) -eq $wantSerial) {
                $info.isMember = $true
                $info.poolName = [string]$pool.FriendlyName
                $info.reason = '磁盘属于存储池「' + $info.poolName + '」'
                return $info
            }
        }
    }
    if ($wantSerial -eq '') {
        $info.reason = '本机存在存储池，但该磁盘读不到序列号，无法排除池成员身份'
    }
    return $info
}

function Get-QmDiskOccupancy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$DiskNumber,
        $Volumes = @(),
        [switch]$WithProcesses
    )

    $report = [pscustomobject]@{
        diskNumber = $DiskNumber
        busy = $false
        sampleSeconds = 0.0
        countersAvailable = $false
        bytesPerSecond = -1
        percentDiskTime = -1
        queueLength = -1
        occupants = @()
        warnings = @()
        notes = @()
        busySummary = ''
    }

    $readCounters = {
        param($n)
        # 实例名形如 "0"、"1 D:"、"2 C: G:"；没有挂载卷的磁盘只有编号。
        $c = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfDisk_PhysicalDisk -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -eq "$n" -or $_.Name -like "$n *" } | Select-Object -First 1
        if ($null -eq $c) { return $null }
        return [pscustomobject]@{
            bytes = [double]$c.DiskBytesPersec
            pct   = [double]$c.PercentDiskTime
            queue = [double]$c.CurrentDiskQueueLength
        }
    }

    $c1 = & $readCounters $DiskNumber
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds 700
    $c2 = & $readCounters $DiskNumber
    $sw.Stop()

    $sec = [Math]::Max(0.1, $sw.Elapsed.TotalSeconds)
    $report.sampleSeconds = [Math]::Round($sec, 3)
    if ($null -ne $c1 -and $null -ne $c2) {
        $report.countersAvailable = $true
        $report.bytesPerSecond = [Math]::Round((($c1.bytes + $c2.bytes) / 2), 0)
        $report.percentDiskTime = [Math]::Round((($c1.pct + $c2.pct) / 2), 1)
        $report.queueLength = [Math]::Max($c1.queue, $c2.queue)
    } else {
        $report.warnings += '性能计数器不可用，占用判定只能依赖进程线索'
    }

    $occupants = @()
    if ($WithProcesses) {
        $letters = @($Volumes | Where-Object { $_.driveLetter } | ForEach-Object { $_.driveLetter.ToUpperInvariant() })
        if ($letters.Count -gt 0) {
            $self = $PID
            $procs = @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue)
            foreach ($p in $procs) {
                if ($p.ProcessId -le 4 -or $p.ProcessId -eq $self) { continue }
                $exe = [string]$p.ExecutablePath
                $cmd = [string]$p.CommandLine
                $hit = ''
                foreach ($l in $letters) {
                    if ($exe -and $exe.ToUpperInvariant().Contains($l)) { $hit = '可执行文件在该盘'; break }
                }
                if ($hit -eq '') {
                    foreach ($l in $letters) {
                        if ($cmd -and $cmd.ToUpperInvariant().Contains($l)) { $hit = '命令行参数指向该盘'; break }
                    }
                }
                if ($hit -eq '') { continue }
                $trimmed = $cmd
                if ($trimmed.Length -gt 320) { $trimmed = $trimmed.Substring(0, 320) + '…' }
                $occupants += [pscustomobject]@{
                    pid = [int]$p.ProcessId; name = [string]$p.Name; executablePath = $exe
                    commandLine = $trimmed; hit = $hit
                }
            }
        }
        $report.occupants = $occupants
        if ($occupants.Count -gt 0) { $report.notes += '进程线索只是辅助判断，不代表真的持有句柄' }
    }

    $ioBusy = $report.countersAvailable -and ($report.bytesPerSecond -gt 262144 -or $report.percentDiskTime -gt 15)
    $procBusy = @($report.occupants).Count -gt 0
    $report.busy = ($ioBusy -or $procBusy)

    $parts = @()
    if ($report.bytesPerSecond -ge 0) {
        $rate = $report.bytesPerSecond
        $unit = 'B/s'; $i = 0
        $units = @('B/s','KB/s','MB/s','GB/s')
        while ($rate -ge 1024 -and $i -lt 3) { $rate = $rate / 1024; $i++ }
        $parts += ('{0:0.##} {1}' -f $rate, $units[$i])
    }
    if ($report.percentDiskTime -ge 0) { $parts += ('{0}% 磁盘时间' -f $report.percentDiskTime) }
    if (@($report.occupants).Count -gt 0) {
        $n = @($report.occupants).Count
        $shown = if ($n -gt 10) { '10+' } else { [string]$n }
        $parts += ('{0} 个进程可能占用' -f $shown)
    }
    $report.busySummary = if ($parts.Count -gt 0) { $parts -join '，' }
                          elseif ($report.countersAvailable) { '有活动' }
                          else { '无法判定（性能计数器不可用）' }
    return $report
}

# ---------------------------------------------------------------- 安全闸门

function Test-QmDiskSafety {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Disk,
        [switch]$Force,
        [switch]$WithProcesses
    )

    $blocks = @()
    $warnings = @()

    if ([int]$Disk.number -lt 0) { $blocks += '磁盘编号无效' }

    if ($null -eq $Disk.isBoot) { $blocks += '无法确定是否为启动盘' }
    elseif ([bool]$Disk.isBoot) { $blocks += '这是启动盘（IsBoot=True）' }

    if ($null -eq $Disk.isSystem) { $blocks += '无法确定是否为系统盘' }
    elseif ([bool]$Disk.isSystem) { $blocks += '这是系统盘（IsSystem=True）' }

    $vols = @(Get-QmDiskVolumes -DiskNumber ([int]$Disk.number))
    foreach ($v in $vols) {
        $label = if ($v.driveLetter) { $v.driveLetter } else { '(无盘符)' }
        if ($v.isSystem) { $blocks += ('磁盘上的卷 {0} 是系统卷' -f $label) }
        if ($v.isBoot) { $blocks += ('磁盘上的卷 {0} 是启动卷' -f $label) }
    }

    $pf = Get-QmPageFileInfo
    foreach ($p in $pf.paths) {
        if ($p.Length -ge 2 -and $p[1] -eq ':') {
            $letter = $p.Substring(0, 2).ToUpperInvariant()
            if (@($vols | Where-Object { $_.driveLetter -eq $letter }).Count -gt 0) {
                $blocks += ('页面文件位于该磁盘：{0}' -f $p)
            }
        }
    }
    if ($pf.automaticManaged -eq $true) {
        $warnings += '系统自动管理页面文件，无法静态排除该盘被用作页面文件'
    }

    $dump = Get-QmCrashDumpInfo
    if ($dump.dumpFile -ne '' -and $dump.dumpFile.Length -ge 2 -and $dump.dumpFile[1] -eq ':') {
        $dl = $dump.dumpFile.Substring(0, 2).ToUpperInvariant()
        if (@($vols | Where-Object { $_.driveLetter -eq $dl }).Count -gt 0 -and $dump.enabled) {
            $warnings += ('崩溃转储文件位于该磁盘（{0}），脱机期间蓝屏无法写入转储' -f $dump.dumpFile)
        }
    }

    $pool = Get-QmStoragePoolInfo -Disk $Disk
    if ($pool.isMember) { $blocks += $pool.reason }
    elseif ($pool.reason -ne '' -and $pool.detected) { $warnings += $pool.reason }

    if ([string]$Disk.busType -eq 'Spaces') { $blocks += '磁盘类型为存储空间（Spaces），不支持脱机' }
    if ([string]$Disk.busType -eq 'FileBackedVirtual') { $blocks += '磁盘为文件支持的虚拟盘，不支持脱机' }
    if ([string]$Disk.busType -eq 'USB') { $warnings += '磁盘通过 USB 连接，脱机后可能被重新枚举' }

    if ([string]$Disk.healthStatus -ne '' -and [string]$Disk.healthStatus -ne 'Healthy') {
        $warnings += ('磁盘健康状态为 {0}' -f $Disk.healthStatus)
    }
    if ([bool]$Disk.isReadOnly) { $warnings += '磁盘当前为只读' }
    if ($vols.Count -eq 0) { $warnings += '该磁盘上没有可识别的卷' }

    $occ = Get-QmDiskOccupancy -DiskNumber ([int]$Disk.number) -Volumes $vols -WithProcesses:$WithProcesses
    if ($occ.busy) {
        if ($Force) { $warnings += ('检测到占用（{0}），已按 Force 忽略' -f $occ.busySummary) }
        else { $blocks += ('检测到占用（{0}）；确认可中断后可用 -Force 越过此项' -f $occ.busySummary) }
    } else {
        $warnings += $occ.warnings
    }

    return [pscustomobject]@{
        allowed  = ($blocks.Count -eq 0)
        blocks   = @($blocks)
        warnings = @($warnings)
        volumes  = @($vols)
        occupancy = $occ
    }
}

# ---------------------------------------------------------------- 执行与回读

function Invoke-QmDiskState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Disk,
        [Parameter(Mandatory = $true)][bool]$Offline,
        [switch]$Apply,
        [switch]$Force
    )

    $requested = if ($Offline) { 'offline' } else { 'online' }
    $result = [ordered]@{
        operation        = $requested
        success          = $false
        verified         = $false
        dryRun           = (-not $Apply)
        errorCode        = 'OK'
        errorMessage     = ''
        uniqueId         = [string]$Disk.uniqueId
        serialNumber     = [string]$Disk.serialNumber
        friendlyName     = [string]$Disk.friendlyName
        currentDiskNumber = [int]$Disk.number
        previousState    = if ([bool]$Disk.isOffline) { 'offline' } else { 'online' }
        requestedState   = $requested
        actualState      = if ([bool]$Disk.isOffline) { 'offline' } else { 'online' }
        timestamp        = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        steps            = @()
        warnings         = @()
    }

    $safety = Test-QmDiskSafety -Disk $Disk -Force:$Force -WithProcesses:$Apply
    $result.warnings = @($safety.warnings)
    $result.steps += ('安全检查：{0} 个阻止项，{1} 个告警' -f @($safety.blocks).Count, @($safety.warnings).Count)

    if (-not $safety.allowed) {
        $result.errorCode = 'SAFETY_BLOCKED'
        $result.errorMessage = (@($safety.blocks) -join '；')
        $result.steps += ('拒绝执行：' + $result.errorMessage)
        return $result
    }

    if ($result.previousState -eq $requested) {
        $result.errorCode = 'ALREADY_IN_STATE'
        $result.errorMessage = ('磁盘已经是 {0} 状态，无需操作' -f $requested)
        $result.success = $true
        $result.verified = $true
        $result.steps += $result.errorMessage
        return $result
    }

    if (-not $Apply) {
        $result.steps += ('演练模式：将执行 Set-Disk -Number {0} -IsOffline ${1}' -f $Disk.number, $Offline)
        $result.success = $true
        return $result
    }

    # 执行前用「已验证的对象」而不是编号：重新取一次同一 UniqueId 的磁盘
    $fresh = $null
    try {
        $fresh = Get-Disk -Number ([int]$Disk.number) -ErrorAction Stop
        if ((ConvertTo-QmId $fresh.UniqueId) -ne (ConvertTo-QmId $Disk.uniqueId)) {
            $result.errorCode = 'IDENTITY_CONFLICT'
            $result.errorMessage = '执行前复检时磁盘身份发生变化（编号被重新分配），已中止'
            return $result
        }
    } catch {
        $result.errorCode = 'PROBE_FAILED'
        $result.errorMessage = '执行前重新读取磁盘失败：' + $_.Exception.Message
        return $result
    }

    $failCode = 'SET_FAILED'
    try {
        Set-Disk -InputObject $fresh -IsOffline $Offline -ErrorAction Stop
        $result.steps += ('Set-Disk -IsOffline {0} 已下发' -f $Offline)
    } catch {
        $msg = ($_.Exception.Message -replace '\r?\n', ' ').Trim()
        if ($msg -match '拒绝访问|Access is denied|Access Denied|denied|权限') { $failCode = 'ELEVATION_REQUIRED' }
        elseif ($msg -match '被占用|in use|busy|正在使用') { $failCode = 'BUSY' }
        $result.errorCode = $failCode
        $result.errorMessage = 'Set-Disk 失败：' + $msg
        $result.steps += $result.errorMessage
    }

    # 无论成功与否都回读实际状态
    $want = $Offline
    $actual = $null
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep -Milliseconds 250
        try {
            $cur = Get-Disk -Number ([int]$Disk.number) -ErrorAction Stop
            $actual = [bool]$cur.IsOffline
            if ($actual -eq $want) { break }
        } catch { }
    }
    if ($null -eq $actual) {
        $result.actualState = 'unknown'
        if ($result.errorCode -eq 'OK') { $result.errorCode = 'PROBE_FAILED'; $result.errorMessage = '回读磁盘状态失败' }
        return $result
    }
    $result.actualState = if ($actual) { 'offline' } else { 'online' }
    $result.steps += ('回读实际状态：{0}' -f $result.actualState)

    if ($result.actualState -eq $requested) {
        $result.errorCode = 'OK'
        $result.errorMessage = ''
        $result.success = $true
        $result.verified = $true
    } else {
        if ($result.errorCode -eq 'OK') { $result.errorCode = 'VERIFY_FAILED' }
        if ($result.errorMessage -eq '') {
            $result.errorMessage = ('请求 {0}，实际 {1}' -f $requested, $result.actualState)
        }
        $result.success = $false
        $result.verified = $false
    }
    return $result
}

# ---------------------------------------------------------------- 批量

function Invoke-QmDiskOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('offline','online','verify')][string]$Operation,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()] $Targets,
        [switch]$Apply,
        [switch]$Force,
        [string]$LogFile = ''
    )

    $disks = @(Get-QmDiskInventory)
    $results = @()

    foreach ($t in @($Targets)) {
        $match = Resolve-QmDiskIdentity -Target $t -Disks $disks
        if ($match.status -ne 'matched') {
            $code = switch ($match.status) {
                'not-found'     { 'IDENTITY_NOT_FOUND' }
                'ambiguous'     { 'IDENTITY_AMBIGUOUS' }
                'no-identity'   { 'NO_IDENTITY' }
                'conflict'      { 'IDENTITY_CONFLICT' }
                default         { 'IDENTITY_NOT_FOUND' }
            }
            $r = [ordered]@{
                operation = $Operation; success = $false; verified = $false
                dryRun = (-not $Apply); errorCode = $code; errorMessage = $match.reason
                uniqueId = [string]$t.uniqueId; serialNumber = [string]$t.serialNumber
                friendlyName = [string]$t.friendlyName; currentDiskNumber = -1
                previousState = 'unknown'; requestedState = $Operation; actualState = 'unknown'
                timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
                steps = @($match.reason); warnings = @($match.notes)
            }
            $results += $r
            continue
        }

        $r = $null
        if ($Operation -eq 'verify') {
            $r = [ordered]@{
                operation = 'verify'; success = $true; verified = $true
                dryRun = $true; errorCode = 'OK'; errorMessage = ''
                uniqueId = [string]$match.disk.uniqueId; serialNumber = [string]$match.disk.serialNumber
                friendlyName = [string]$match.disk.friendlyName; currentDiskNumber = [int]$match.disk.number
                previousState = if ($match.disk.isOffline) { 'offline' } else { 'online' }
                requestedState = if ($match.disk.isOffline) { 'offline' } else { 'online' }
                actualState = if ($match.disk.isOffline) { 'offline' } else { 'online' }
                timestamp = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
                steps = @($match.reason); warnings = @($match.notes)
            }
        } else {
            $r = Invoke-QmDiskState -Disk $match.disk -Offline ($Operation -eq 'offline') -Apply:$Apply -Force:$Force
            $r.warnings = @($match.notes) + @($r.warnings)
        }
        $results += $r
    }

    $payload = [ordered]@{
        host = [ordered]@{
            computerName = $env:COMPUTERNAME
            user         = [string]$env:USERNAME
            elevated     = $false
            powershell   = $PSVersionTable.PSVersion.ToString()
            timestamp    = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        }
        operation = $Operation
        apply     = [bool]$Apply
        results   = @($results)
    }
    try {
        $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $payload.host.elevated = (New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole(
            [System.Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { }

    if ($LogFile -ne '') {
        try {
            $dir = Split-Path -Parent $LogFile
            if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            $line = ($payload | ConvertTo-Json -Depth 8 -Compress)
            Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
        } catch { }
    }
    return $payload
}

Export-ModuleMember -Function Get-QmDiskInventory, Resolve-QmDiskIdentity, Get-QmDiskVolumes,
    Get-QmPageFileInfo, Get-QmCrashDumpInfo, Get-QmStoragePoolInfo, Get-QmDiskOccupancy,
    Test-QmDiskSafety, Invoke-QmDiskState, Invoke-QmDiskOperation, Format-QmSize
