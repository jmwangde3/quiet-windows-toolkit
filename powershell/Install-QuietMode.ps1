<#
.SYNOPSIS
    QuietMode v2 安装 / 迁移 / 回滚（Phase 6）。

.DESCRIPTION
    把 v1 的手工脚本体系切换到 v2 的统一控制器：部署二进制、迁移配置、抓 baseline、
    注册单一计划任务、停用 v1 闸门、交回自动调度。

    默认 DryRun：只读检查 + 打印将执行的每一步，不改任何系统状态。
    Apply 需要管理员。Rollback 把调度器退回 v1（不删配置与 baseline，便于复诊）。

.PARAMETER Mode
    DryRun | Apply | Rollback

.PARAMETER DeployDir
    二进制部署目录。默认取脚本所在仓库的 QuietMode\ 目录（就地部署）。

.PARAMETER Root
    机器级配置根，默认 C:\ProgramData\QuietMode。

.PARAMETER KeepLegacy
    Apply 时不禁用 v1 闸门（用于先并行观察一轮）。

.EXAMPLE
    .\Install-QuietMode.ps1
    .\Install-QuietMode.ps1 -Mode Apply
    .\Install-QuietMode.ps1 -Mode Rollback -DeployDir 'D:\Tools\QuietMode'
#>
[CmdletBinding()]
param(
    [ValidateSet('DryRun', 'Apply', 'Rollback')]
    [string]$Mode = 'DryRun',

    [string]$DeployDir = '',

    [string]$Root = 'C:\ProgramData\QuietMode',

    [switch]$KeepLegacy,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:StepNo = 0
$script:ExeName = 'QuietMode.exe'
$script:TaskFullName = '\QuietMode\QuietMode-Reconcile'
# v1 历史上装过两种路径：带 \QuietHDD\ 文件夹的，以及直接挂在根下的。
$script:LegacyTasks = @(
    '\QuietHDD\QuietHDD_NightGate',
    '\QuietHDD\QuietHDD_DiskGate',
    '\QuietHDD_NightGate',
    '\QuietHDD_DiskGate'
)

# ---------------------------------------------------------------- 基础设施

function Write-Log {
    param([string]$Text, [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    if ($script:LogPath) {
        try { Add-Content -LiteralPath $script:LogPath -Value $Text -Encoding UTF8 } catch { }
    }
}

function Write-Step {
    param([string]$Title)
    $script:StepNo++
    Write-Log ''
    Write-Log ("[{0:00}] {1}" -f $script:StepNo, $Title) 'Cyan'
}

function Write-Ok    { param([string]$Text) Write-Log ("     OK   " + $Text) 'Green' }
function Write-Note  { param([string]$Text) Write-Log ("     --   " + $Text) 'DarkGray' }
function Write-Warn2 { param([string]$Text) Write-Log ("     WARN " + $Text) 'Yellow'; $script:Warnings.Add($Text) }
function Write-Bad   { param([string]$Text) Write-Log ("     FAIL " + $Text) 'Red';    $script:Failures.Add($Text) }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
    Test-Path 在无权访问的目录上会抛 UnauthorizedAccessException，
    而本脚本 ErrorActionPreference=Stop —— 未提权的 DryRun 会因此直接中断。
    读不到 ≠ 不存在，所以这里返回 $null 表示「不知道」。
#>
function Test-PathSafe {
    param([string]$Path)
    try { return (Test-Path -LiteralPath $Path -ErrorAction Stop) }
    catch { return $null }
}

function Resolve-RepoRoot {
    # powershell\ 的上一级就是仓库根
    return (Split-Path -Parent $PSScriptRoot)
}

function Invoke-Qmctl {
    param([string[]]$Arguments)

    $exe = $script:QmctlPath
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) {
        throw "找不到 qmctl.exe"
    }
    $argv = @()
    if ($Root) { $argv += @('--root', $Root) }
    $argv += $Arguments

    Write-Log ("     $ qmctl " + ($argv -join ' ')) 'DarkGray'
    # 原生命令写 stderr 时，Stop 会把它变成终止性异常 —— 这里必须降级，只认退出码
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @argv 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
    foreach ($line in $out) {
        $s = [string]$line
        if ($s.Length -gt 0) { Write-Log ("       " + $s) 'DarkGray' }
    }
    return $code
}

function Invoke-QmctlJson {
    param([string[]]$Arguments)

    $tmp = Join-Path $env:TEMP ("qm-" + [Guid]::NewGuid().ToString('N') + ".json")
    $argv = @()
    if ($Root) { $argv += @('--root', $Root) }
    $argv += $Arguments
    $argv += @('--json', '--out', $tmp)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $script:QmctlPath @argv 2>&1 | Out-Null
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
    $data = $null
    if (Test-Path -LiteralPath $tmp) {
        try {
            $raw = Get-Content -LiteralPath $tmp -Raw -Encoding UTF8
            if ($raw -and $raw.Trim().Length -gt 0) { $data = $raw | ConvertFrom-Json }
        } catch { }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ Code = $code; Data = $data }
}

function Copy-Binaries {
    param([string]$From, [string]$To, [string]$BackupDir, [string[]]$Names, [bool]$Apply)

    foreach ($n in $Names) {
        $src = Join-Path $From $n
        if (-not (Test-Path -LiteralPath $src)) { throw "缺少源文件 $src" }
        $dst = Join-Path $To $n

        if (Test-Path -LiteralPath $dst) {
            $same = $false
            try {
                $h1 = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
                $h2 = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
                $same = ($h1 -eq $h2)
            } catch { }
            if ($same) { Write-Note "$n 已是最新，跳过"; continue }

            if (-not $Apply) { Write-Note "$n 将被备份后覆盖"; continue }

            $bak = Join-Path $BackupDir $n
            Copy-Item -LiteralPath $dst -Destination $bak -Force
            Write-Note "$n 已备份到 $bak"
        }
        elseif (-not $Apply) { Write-Note "$n 将复制到 $dst"; continue }

        Copy-Item -LiteralPath $src -Destination $dst -Force
        Write-Ok "$n → $dst"
    }
}

function Get-TrayProcess {
    Get-Process -Name 'QuietMode' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -like '*QuietMode.exe') }
}

function Stop-Tray {
    param([bool]$Apply)
    $procs = @(Get-TrayProcess)
    if ($procs.Count -eq 0) { Write-Note '托盘未运行'; return }
    if (-not $Apply) {
        $ids = ($procs | ForEach-Object { $_.Id }) -join ','
        Write-Note "托盘正在运行（PID $ids），Apply 时会先关闭"
        return
    }
    foreach ($p in $procs) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 800
    foreach ($p in @(Get-TrayProcess)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Write-Note "已结束托盘 PID $($p.Id)" }
        catch { Write-Warn2 "结束托盘 PID $($p.Id) 失败：$($_.Exception.Message)" }
    }
}

function Start-Tray {
    param([string]$ExePath, [bool]$Apply)
    if (-not $Apply) { Write-Note "Apply 完成后会重新启动 $ExePath"; return }
    if (-not (Test-Path -LiteralPath $ExePath)) { Write-Warn2 "找不到 $ExePath，未启动托盘"; return }
    try {
        Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) | Out-Null
        Write-Ok '托盘已启动'
    } catch { Write-Warn2 "启动托盘失败：$($_.Exception.Message)" }
}

function Split-TaskPath {
    param([string]$FullName)
    $parts = $FullName.TrimStart('\').Split('\')
    $name = $parts[-1]
    $path = '\'
    if ($parts.Length -gt 1) { $path = '\' + (($parts[0..($parts.Length - 2)]) -join '\') + '\' }
    return [pscustomobject]@{ Path = $path; Name = $name }
}

function Get-TaskState {
    param([string]$FullName)
    $tp = Split-TaskPath -FullName $FullName
    try {
        $t = Get-ScheduledTask -TaskPath $tp.Path -TaskName $tp.Name -ErrorAction Stop
        return [pscustomobject]@{ Exists = $true; Enabled = ($t.State -ne 'Disabled'); State = [string]$t.State; Error = '' }
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match 'No MSFT_ScheduledTask|找不到任何匹配|找不到|does not exist|不存在') {
            # \QuietHDD\ 的 ACL 对普通用户不可读：CIM 会报"找不到"，但那是看不见，不是不存在
            if ($tp.Path -eq '\QuietHDD\' -and -not (Test-Admin)) {
                return [pscustomobject]@{ Exists = $null; Enabled = $null; State = ''; Error = '未提权，\QuietHDD\ 下的任务不可见（读不到 ≠ 不存在）' }
            }
            return [pscustomobject]@{ Exists = $false; Enabled = $null; State = ''; Error = '' }
        }
        return [pscustomobject]@{ Exists = $null; Enabled = $null; State = ''; Error = $msg }
    }
}

function Set-LegacyEnabled {
    param([string]$FullName, [bool]$Enabled)
    $tp = Split-TaskPath -FullName $FullName
    if ($Enabled) { Enable-ScheduledTask -TaskPath $tp.Path -TaskName $tp.Name | Out-Null }
    else { Disable-ScheduledTask -TaskPath $tp.Path -TaskName $tp.Name | Out-Null }
    return (Get-TaskState -FullName $FullName)
}

# ---------------------------------------------------------------- 环境判定

$repoRoot = Resolve-RepoRoot
if (-not $DeployDir) { $DeployDir = Join-Path $repoRoot 'QuietMode' }
$DeployDir = [IO.Path]::GetFullPath($DeployDir)

$cliBin = Join-Path $repoRoot 'src\QuietMode.Cli\bin\Release\net481'
$trayBin = Join-Path $repoRoot 'QuietMode\build\bin\Release'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$isAdmin = Test-Admin

if (-not (Test-Path -LiteralPath $Root)) {
    if ($Mode -eq 'Apply') { New-Item -ItemType Directory -Path $Root -Force | Out-Null }
}
$logDir = Join-Path $Root 'logs'
if ($Mode -eq 'Apply') { New-Item -ItemType Directory -Path $logDir -Force -ErrorAction SilentlyContinue | Out-Null }
if (Test-Path -LiteralPath $logDir) { $script:LogPath = Join-Path $logDir ("install-" + $stamp + ".log") }
else { $script:LogPath = $null }

$backupRoot = Join-Path $Root ("recovery\install-" + $stamp)

Write-Log ''
Write-Log '============================================================' 'White'
Write-Log (" QuietMode v2 安装器   模式 = " + $Mode) 'White'
Write-Log '============================================================' 'White'
Write-Log (" 仓库     " + $repoRoot)
Write-Log (" 部署目录 " + $DeployDir)
Write-Log (" 配置根   " + $Root)
$adminText = '否'
if ($isAdmin) { $adminText = '是' }
Write-Log (" 管理员   " + $adminText)
if ($script:LogPath) { Write-Log (" 日志     " + $script:LogPath) }

if ($Mode -ne 'DryRun' -and -not $isAdmin) {
    Write-Log ''
    Write-Log '需要管理员权限：请右键「以管理员身份运行」，或从提权的终端执行。' 'Red'
    exit 5
}

# 预检一律用编译产物：部署目录里可能还是上一版，拿它探针会得到旧结论
$srcQmctl = Join-Path $cliBin 'qmctl.exe'
$script:QmctlPath = $srcQmctl

# ================================================================ ROLLBACK
if ($Mode -eq 'Rollback') {
    Write-Step '删除 v2 计划任务'
    $st = Get-TaskState -FullName $script:TaskFullName
    if ($st.Exists -eq $false) { Write-Note 'v2 任务不存在，跳过' }
    elseif ($st.Exists -eq $null) { Write-Warn2 ("无法确认 v2 任务状态：" + $st.Error) }
    else {
        try {
            Unregister-ScheduledTask -TaskPath '\QuietMode\' -TaskName 'QuietMode-Reconcile' -Confirm:$false
            $after = Get-TaskState -FullName $script:TaskFullName
            if ($after.Exists -eq $false) { Write-Ok '已删除 \QuietMode\QuietMode-Reconcile' } else { Write-Bad '删除后仍存在' }
        } catch { Write-Bad ("删除任务失败：" + $_.Exception.Message) }
    }

    Write-Step '恢复 v1 闸门'
    foreach ($full in $script:LegacyTasks) {
        $s = Get-TaskState -FullName $full
        if ($s.Exists -eq $false) { Write-Note "$full 不存在（v1 未安装过？）"; continue }
        if ($s.Exists -eq $null) { Write-Bad ("$full 无法确认：" + $s.Error); continue }
        if ($s.Enabled -eq $true) { Write-Note "$full 已是启用状态"; continue }
        try {
            $a = Set-LegacyEnabled -FullName $full -Enabled $true
            if ($a.Enabled -eq $true) { Write-Ok "$full 已启用" } else { Write-Bad "$full 启用后回读仍为禁用" }
        } catch { Write-Bad ("启用 $full 失败：" + $_.Exception.Message) }
    }

    Write-Step '恢复旧二进制（若存在安装备份）'
    $latest = Get-ChildItem -Path (Join-Path $Root 'recovery') -Directory -Filter 'install-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { Write-Note '没有找到安装备份，跳过' }
    else {
        foreach ($f in @(Get-ChildItem -LiteralPath $latest.FullName -File -ErrorAction SilentlyContinue)) {
            if ($f.Name -like '*.json' -or $f.Name -like '*.log') { continue }
            $target = Join-Path $DeployDir $f.Name
            Copy-Item -LiteralPath $f.FullName -Destination $target -Force
            Write-Ok "还原 $($f.Name) ← $($latest.Name)"
        }
    }

    Write-Step '托盘'
    Start-Tray -ExePath (Join-Path $DeployDir $script:ExeName) -Apply $true

    Write-Log ''
    Write-Log '回滚完成。config.json / baseline.json 保留在配置根，便于复诊后再决定是否清理。' 'Yellow'
    Write-Log ("失败 " + $script:Failures.Count + " 项 / 警告 " + $script:Warnings.Count + " 项")
    if ($script:Failures.Count -gt 0) { exit 2 } else { exit 0 }
}

$apply = ($Mode -eq 'Apply')

# ================================================================ 1 环境与权限
Write-Step '环境与权限'

$psv = $PSVersionTable.PSVersion
if ($psv.Major -gt 5 -or ($psv.Major -eq 5 -and $psv.Minor -ge 1)) {
    Write-Ok ("PowerShell " + $psv.ToString())
} else {
    Write-Bad ("PowerShell " + $psv.ToString() + " 过旧：需要 5.1 及以上")
}

$netRelease = 0
try {
    $ndp = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop
    if ($null -ne $ndp.Release) { $netRelease = [int]$ndp.Release }
} catch { $netRelease = 0 }

if ($netRelease -ge 533320) { Write-Ok (".NET Framework 4.8.1（Release " + $netRelease + "）") }
elseif ($netRelease -ge 528040) { Write-Ok (".NET Framework 4.8（Release " + $netRelease + "）") }
elseif ($netRelease -ge 461808) { Write-Bad (".NET Framework 4.7.2（Release " + $netRelease + "）：二进制按 4.8 编译，加载可能失败") }
elseif ($netRelease -gt 0) { Write-Bad (".NET Framework 4.x（Release " + $netRelease + "）低于 4.8") }
else { Write-Bad '读不到 .NET Framework 4.x 版本键：未安装或注册表不可读' }

$osv = [Environment]::OSVersion.Version
if ($osv.Major -ge 10) {
    Write-Ok ("Windows 内核 " + $osv.ToString(2) + "（" + [Environment]::OSVersion.VersionString + "）")
} elseif ($osv.Major -eq 6 -and $osv.Minor -ge 1) {
    Write-Warn2 ("Windows 内核 " + $osv.ToString(2) + "：只在 Win7 SP1 以上验证过")
} else {
    Write-Bad ("不支持的 Windows 版本 " + $osv.ToString(2))
}

if ($isAdmin) { Write-Ok '已提权（Administrators）' }
elseif ($Mode -eq 'DryRun') { Write-Note '未提权：DryRun 只读检查可继续；Apply 需要管理员' }
else { Write-Bad '未提权' }

# ================================================================ 2 载体完整性
Write-Step '载体完整性'

function Show-Artifact {
    param([string]$Path, [string]$Label, [switch]$Required)
    if (-not (Test-Path -LiteralPath $Path)) {
        if ($Required) { Write-Bad ("缺少 " + $Label + "：" + $Path + "（先 dotnet build QuietMode.sln -c Release）") }
        else { Write-Warn2 ("缺少 " + $Label + "：" + $Path) }
        return
    }
    $f = Get-Item -LiteralPath $Path
    $ver = ''
    try { $ver = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion } catch { }
    $hash = ''
    try { $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Substring(0, 12) } catch { }
    $parts = @($Label, ('{0:N0} B' -f $f.Length))
    if ($ver) { $parts += ('v' + $ver) }
    if ($hash) { $parts += ('sha256:' + $hash) }
    Write-Ok ($parts -join '  ')
}

Show-Artifact -Path $srcQmctl -Label 'qmctl.exe' -Required
Show-Artifact -Path (Join-Path $cliBin 'QuietMode.Core.dll') -Label 'QuietMode.Core.dll' -Required
Show-Artifact -Path (Join-Path $cliBin 'QuietMode.Windows.dll') -Label 'QuietMode.Windows.dll' -Required
Show-Artifact -Path (Join-Path $trayBin 'QuietMode.exe') -Label '托盘 QuietMode.exe' -Required

# 任务将来调用的脚本树必须完整：少一个模块就在夜里静默失败
$moduleNames = @(
    'QuietMode.Common.psm1', 'QuietMode.Config.psm1', 'QuietMode.Disk.psm1',
    'QuietMode.Fan.psm1', 'QuietMode.Maintenance.psm1', 'QuietMode.Power.psm1',
    'QuietMode.Security.psm1'
)
foreach ($m in $moduleNames) {
    Show-Artifact -Path (Join-Path $repoRoot ('powershell\Modules\' + $m)) -Label $m -Required
}
Show-Artifact -Path (Join-Path $repoRoot 'powershell\Worker\Invoke-QuietModeWorker.ps1') -Label 'Worker' -Required
foreach ($s in @('Disks\Invoke-DiskOperation.ps1', 'Maintenance\Invoke-MaintenanceOperation.ps1')) {
    Show-Artifact -Path (Join-Path $repoRoot ('powershell\' + $s)) -Label $s
}

if ($script:Failures.Count -gt 0) {
    Write-Log ''
    Write-Log '载体不完整，未做任何改动。' 'Red'
    exit 3
}

# ================================================================ 3 现有安装
Write-Step '现有安装'

$hasConfig = Test-Path -LiteralPath (Join-Path $Root 'config.json')
$hasBaseline = Test-Path -LiteralPath (Join-Path $Root 'baseline.json')
$legacyAppData = Join-Path $env:APPDATA 'QuietMode'
$legacySettings = Join-Path $legacyAppData 'settings.ini'
$legacyStateIni = Join-Path $legacyAppData 'state.ini'
$legacyDiskConf = 'C:\ProgramData\QuietHDD\diskgate.conf'

$cfgText = '不存在'
if ($hasConfig) { $cfgText = '已存在' }
$baseText = '不存在'
if ($hasBaseline) { $baseText = '已存在' }
Write-Note ("config.json     " + $cfgText)
Write-Note ("baseline.json   " + $baseText)

$v1Files = @()
foreach ($p in @($legacySettings, $legacyStateIni, $legacyDiskConf)) {
    if (Test-Path -LiteralPath $p) { $v1Files += $p }
}
if ($v1Files.Count -gt 0) {
    foreach ($p in $v1Files) { Write-Ok ("v1 配置 " + $p) }
} else {
    Write-Note '没有 v1 配置文件（全新安装路径）'
}

$v1Active = @()
$v1Seen = 0
foreach ($full in $script:LegacyTasks) {
    $s = Get-TaskState -FullName $full
    if ($s.Exists -eq $true) {
        $v1Seen++
        $stateText = '启用'
        if ($s.Enabled -eq $false) { $stateText = '禁用' }
        Write-Ok ("v1 任务 " + $full + " 存在，当前" + $stateText)
        if ($s.Enabled -eq $true) { $v1Active += $full }
    } elseif ($s.Exists -eq $null) {
        Write-Warn2 ("v1 任务 " + $full + " 无法确认：" + $s.Error)
    }
}
if ($v1Seen -eq 0) { Write-Note '没有 v1 闸门任务' }
elseif ($v1Active.Count -eq 0) { Write-Note 'v1 闸门任务都已禁用，不与 v2 抢设备' }

$st0 = Invoke-QmctlJson -Arguments @('task', 'status')
if ($st0.Data -and $st0.Data.exists -eq $true) {
    Write-Ok ("v2 任务已注册，状态 " + $st0.Data.state)
} elseif ($st0.Data -and $st0.Data.exists -eq $false) {
    Write-Note 'v2 任务未注册'
} else {
    Write-Warn2 ("读不到 v2 任务状态（退出码 " + $st0.Code + "）")
}

# ================================================================ 4 磁盘稳定身份
Write-Step '磁盘稳定身份'

$probe = Invoke-QmctlJson -Arguments @('status')
$probeData = $probe.Data

if ($null -eq $probeData) {
    Write-Warn2 ("status 探针没有返回数据（退出码 " + $probe.Code + "）：无法核对磁盘身份，Apply 前必须解决")
} else {
    $diskTargets = @($probeData.disks.targets)
    if ($diskTargets.Count -eq 0) {
        Write-Note '配置里没有启用的磁盘目标（disks.enabled=false 或 targets 为空）'
    } else {
        foreach ($d in $diskTargets) {
            $label = '磁盘 #' + $d.number + ' ' + $d.friendlyName
            if ($d.matched -eq $true -and $d.uniqueId) {
                Write-Ok ($label + ' → ' + $d.uniqueId)
            } elseif ($d.matched -eq $true) {
                Write-Bad ($label + ' 匹配到了但读不到稳定身份，拒绝继续（禁止按编号猜测）')
            } else {
                Write-Bad ($label + ' 未匹配：' + $d.matchReason)
            }
        }
    }
    if ($probeData.disks.unknown -gt 0) {
        Write-Warn2 ("有 " + $probeData.disks.unknown + " 个磁盘状态读不到（权限不足？）")
    }
}

# ================================================================ 5 静音窗口与风扇
Write-Step '静音窗口与风扇依赖'

function Convert-HhmmMinutes {
    param([string]$Text)
    if ($Text -notmatch '^\d{1,2}:\d{2}$') { return -1 }
    $parts = $Text.Split(':')
    $h = [int]$parts[0]
    $m = [int]$parts[1]
    if ($h -lt 0 -or $h -gt 23 -or $m -lt 0 -or $m -gt 59) { return -1 }
    return ($h * 60 + $m)
}

if ($null -eq $probeData) {
    Write-Warn2 '读不到 schedule 段，跳过窗口检查'
} else {
    $night = [string]$probeData.schedule.nightStart
    $day = [string]$probeData.schedule.dayStart
    $ns = Convert-HhmmMinutes $night
    $ds = Convert-HhmmMinutes $day
    if ($ns -lt 0 -or $ds -lt 0) {
        Write-Bad ("静音窗口时间格式非法：nightStart=" + $night + "  dayStart=" + $day)
    } elseif ($ns -eq $ds) {
        Write-Bad '夜间起点与白天起点相同，窗口为空，等于永不静音'
    } else {
        $len = $ds - $ns
        if ($len -le 0) { $len += 1440 }
        Write-Ok ("夜间窗口 " + $night + " → " + $day + "（" + [math]::Round($len / 60.0, 2) + " 小时 / " + $len + " 分钟）")
        if ($null -ne $probeData.window) {
            Write-Note ("当前判定 " + $probeData.window.band + "：" + $probeData.window.reason)
        }
        if ($len -gt 1380) { Write-Warn2 '窗口超过 23 小时：白天几乎没有恢复时间' }
    }

    $schedEnabled = $probeData.schedule.enabled
    if ($schedEnabled -ne $true) { Write-Warn2 'schedule.enabled=false：装完也不会自动切换，需要手动打开' }

    $fan = $probeData.fan
    if ($null -eq $fan) {
        Write-Warn2 '读不到 fan 段，跳过风扇依赖检查'
    } elseif ($fan.enabled -ne $true) {
        Write-Note 'fan.enabled=false：不依赖 FanControl'
    } elseif ($fan.available -eq $true) {
        Write-Ok ("FanControl 可用，当前档位 " + $fan.currentProfile + "（match=" + $fan.match + "）")
        if ($fan.silentProfile -and $fan.silentProfile -eq $fan.normalProfile) {
            Write-Warn2 '静音档与普通档同名：夜间切换等于什么也没做'
        }
    } else {
        Write-Warn2 ("fan.enabled=true 但 FanControl 当前不可用：" + $fan.error + "；夜间风扇步骤会失败")
    }
}

# ================================================================ 6 baseline 可信度
Write-Step 'baseline 可信度'

if (-not $hasBaseline) {
    Write-Note '还没有 baseline：Apply 时会抓一份（夜间抓会标「原始状态未知」，见下）'
} else {
    $bv = Invoke-QmctlJson -Arguments @('snapshot', 'verify')
    $bState = ''
    if ($bv.Data -and $bv.Data.state) { $bState = [string]$bv.Data.state }
    if ($bState -eq 'Complete' -and $bv.Data.valid -eq $true) {
        Write-Ok 'baseline 完整且校验通过'
    } elseif ($bState -eq 'Partial') {
        Write-Warn2 'baseline 不完整（缺 XML 备份或字段）：可以继续，但恢复会有无法确认的项'
    } elseif ($bState -eq 'Corrupt') {
        Write-Bad 'baseline 损坏（校验和不符或 schema 不符）：拒绝把损坏基线用于恢复，需 -Force 重建或先修复'
        if ($bv.Data -and $bv.Data.errors) { foreach ($e in $bv.Data.errors) { Write-Note ("  " + $e) } }
    } elseif ($bState -eq 'Missing') {
        Write-Warn2 'qmctl 说找不到 baseline，但文件存在：两者不一致，先查清再装'
    } else {
        Write-Warn2 ("baseline 状态未知（退出码 " + $bv.Code + "），按「不完整」对待")
    }
}

# ================================================================ 7 安装计划
Write-Step '安装计划'

$planLines = New-Object System.Collections.Generic.List[string]
$planLines.Add('1. 部署二进制与脚本树到 ' + $DeployDir)
if ($hasConfig -and -not $Force) { $planLines.Add('2. 配置：已存在，保持不动（-Force 可重建）') }
else { $planLines.Add('2. 配置：从 v1 settings.ini 迁移并校验（旧配置先备份）') }
if ($hasBaseline -and -not $Force) { $planLines.Add('3. baseline：已存在，保持不动（-Force 才重建）') }
else { $planLines.Add('3. baseline：抓取原始状态快照（任务 XML 一并备份）') }
$planLines.Add('4. 注册唯一调度任务 ' + $script:TaskFullName + '（SYSTEM + 最高权限）')
$planLines.Add('5. 授予 Users 读取任务文件的权限（托盘免提权）')
if ($KeepLegacy) { $planLines.Add('6. v1 闸门：按 -KeepLegacy 保留（两套调度会互相覆盖）') }
elseif ($v1Active.Count -gt 0) { $planLines.Add('6. 停用 v1 闸门：' + ($v1Active -join ', ')) }
else { $planLines.Add('6. 停用 v1 闸门：没有需要停用的') }
$planLines.Add('7. 清掉手动静音与今晚抑制标记（state auto），随后立即对齐一次')
$planLines.Add('8. 启动托盘')

foreach ($line in $planLines) { Write-Note $line }
Write-Note '不变量：任何时刻只允许一套调度器控制磁盘；v2 注册失败就不动 v1，v1 停不掉就回滚 v2。'

if ($script:Failures.Count -gt 0) {
    Write-Log ''
    Write-Log '预检失败，未做任何改动。修好上面标 FAIL 的项再重试。' 'Red'
    if ($script:LogPath) { Write-Log ("日志：" + $script:LogPath) 'Yellow' }
    exit 3
}

# ================================================================ 2 部署二进制
Write-Step '部署二进制'
if (-not (Test-Path -LiteralPath $DeployDir)) {
    if ($apply) { New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null; Write-Ok "创建 $DeployDir" }
    else { Write-Note "将创建 $DeployDir" }
}

if ($apply) {
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Write-Note ("安装备份 " + $backupRoot)
    # 上一版加固用的是 Users Deny，管理员也在 Users 组里，Deny 会连部署一起挡下来。
    # 覆盖任何文件之前先拆掉它，否则这次 Apply 会卡在「没有权限写 powershell\」。
    $secForDeploy = Join-Path $repoRoot 'powershell\Modules\QuietMode.Security.psm1'
    if (Test-Path -LiteralPath $secForDeploy) {
        try {
            Import-Module $secForDeploy -Force
            foreach ($dp in (Get-QmPrivilegedPaths -Root $Root -DeployDir $DeployDir)) {
                if (Remove-QmUserDeny -Path $dp) { Write-Note ("已拆除旧 Deny：" + $dp) }
            }
        } catch { Write-Warn2 ("拆除旧 Deny 失败（可能需要先手工 icacls）：" + $_.Exception.Message) }
    }
    Stop-Tray -Apply $true
} else {
    Stop-Tray -Apply $false
}

Copy-Binaries -From $cliBin -To $DeployDir -BackupDir $backupRoot `
    -Names @('qmctl.exe', 'qmctl.exe.config', 'QuietMode.Core.dll', 'QuietMode.Windows.dll') -Apply $apply
Copy-Binaries -From $trayBin -To $DeployDir -BackupDir $backupRoot `
    -Names @('QuietMode.exe', 'QuietMode.exe.config') -Apply $apply

# 脚本树必须跟二进制放在一起：qmctl 用 RepoLocator 从自身目录向上找 powershell\Modules，
# 部署目录不含这份脚本时，夜间任务会报「找不到仓库根目录」而整夜不动作。
$psSrc = Join-Path $repoRoot 'powershell'
$psDst = Join-Path $DeployDir 'powershell'
if (-not (Test-Path -LiteralPath $psSrc)) {
    Write-Bad ("缺少脚本树：" + $psSrc)
} elseif (-not $apply) {
    Write-Note ("将复制 " + $psSrc + " → " + $psDst)
} else {
    try {
        if (Test-Path -LiteralPath $psDst) { Remove-Item -LiteralPath $psDst -Recurse -Force }
        Copy-Item -LiteralPath $psSrc -Destination $psDst -Recurse -Force
        $n = @(Get-ChildItem -LiteralPath $psDst -Recurse -File).Count
        if (Test-Path -LiteralPath (Join-Path $psDst 'Worker\Invoke-QuietModeWorker.ps1')) {
            Write-Ok ("脚本树已部署（" + $n + " 个文件），Worker 可达")
        } else {
            Write-Bad '脚本树复制后找不到 Worker，编译产物将无法执行任何计划步骤'
        }
    } catch { Write-Bad ("复制脚本树失败：" + $_.Exception.Message) }
}

$script:QmctlPath = Join-Path $DeployDir 'qmctl.exe'
if (-not (Test-Path -LiteralPath $script:QmctlPath)) { $script:QmctlPath = $srcQmctl }

# ================================================================ 备份
Write-Step '备份现有资料（改动前先留退路）'

$backupTargets = @(
    @{ Path = (Join-Path $Root 'config.json'); Sub = 'config' },
    @{ Path = (Join-Path $Root 'state.json'); Sub = 'config' },
    @{ Path = (Join-Path $Root 'baseline.json'); Sub = 'baseline' },
    @{ Path = (Join-Path $Root 'baseline.previous.json'); Sub = 'baseline' },
    @{ Path = $legacySettings; Sub = 'v1' },
    @{ Path = $legacyStateIni; Sub = 'v1' },
    @{ Path = $legacyDiskConf; Sub = 'v1' }
)

foreach ($bt in $backupTargets) {
    if (-not (Test-Path -LiteralPath $bt.Path)) { continue }
    if (-not $apply) { Write-Note ("将备份 " + $bt.Path); continue }
    $dir = Join-Path $backupRoot $bt.Sub
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    try {
        Copy-Item -LiteralPath $bt.Path -Destination (Join-Path $dir (Split-Path -Leaf $bt.Path)) -Force
        Write-Ok ("备份 " + $bt.Path)
    } catch { Write-Bad ("备份失败 " + $bt.Path + "：" + $_.Exception.Message) }
}

# v1 任务的 XML 也要留：迁移后若发现判断错了，得能照原样装回去
foreach ($full in $script:LegacyTasks) {
    $s = Get-TaskState -FullName $full
    if ($s.Exists -ne $true) { continue }
    if (-not $apply) { Write-Note ("将导出任务 XML " + $full); continue }
    $tp = Split-TaskPath -FullName $full
    $dir = Join-Path $backupRoot 'tasks'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    try {
        $xml = Export-ScheduledTask -TaskPath $tp.Path -TaskName $tp.Name -ErrorAction Stop
        $file = Join-Path $dir ($tp.Name + '.xml')
        Set-Content -LiteralPath $file -Value $xml -Encoding UTF8
        Write-Ok ("导出任务 XML " + $full)
    } catch { Write-Warn2 ("导出任务 XML 失败 " + $full + "：" + $_.Exception.Message) }
}

if ($script:Failures.Count -gt 0) {
    Write-Log ''
    Write-Log '备份不完整，停止：没有退路就不动系统。' 'Red'
    exit 4
}

# ---------------------------------------------------------------- 迁移前状态记录
# v1 的「现在」不等于「原始」：旧闸门可能已经把维护任务禁用了。
# 把迁移前看到的一切写下来，迁移后再有争议时能对照，而不是靠回忆。
if ($apply) {
    $rec = New-Object System.Collections.Generic.List[string]
    $rec.Add('QuietMode v2 迁移前状态记录')
    $rec.Add('生成时间   ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    $rec.Add('模式       ' + $Mode)
    $rec.Add('提权       ' + $isAdmin)
    $rec.Add('配置根     ' + $Root)
    $rec.Add('部署目录   ' + $DeployDir)
    if ($null -ne $probeData -and $null -ne $probeData.window) {
        $rec.Add('窗口判定   ' + $probeData.window.band + ' / ' + $probeData.window.reason)
    }
    $rec.Add('')

    $rec.Add('--- v1 配置文件 ---')
    foreach ($p in @($legacySettings, $legacyStateIni, $legacyDiskConf)) {
        if (Test-Path -LiteralPath $p) {
            $rec.Add('[' + $p + ']')
            try { foreach ($line in (Get-Content -LiteralPath $p -ErrorAction Stop)) { $rec.Add('  ' + $line) } }
            catch { $rec.Add('  (读取失败：' + $_.Exception.Message + ')') }
        } else {
            $rec.Add('[' + $p + '] 不存在')
        }
    }
    $rec.Add('')

    $rec.Add('--- v1 计划任务 ---')
    foreach ($full in $script:LegacyTasks) {
        $s = Get-TaskState -FullName $full
        $t = '无法确认'
        if ($s.Exists -eq $false) { $t = '不存在' }
        elseif ($s.Exists -eq $true) {
            $t = '存在，'
            if ($null -eq $s.Enabled) { $t += '启用状态未知' }
            elseif ($s.Enabled) { $t += '启用' }
            else { $t += '禁用' }
            if ($s.State) { $t += '，' + $s.State }
        }
        $rec.Add('  ' + $full + ' → ' + $t)
    }
    $rec.Add('')

    $rec.Add('--- 维护任务（迁移前观测值，不等于原始值）---')
    $ml = Invoke-QmctlJson -Arguments @('maint', 'list')
    if ($ml.Data -and $ml.Data.tasks) {
        foreach ($t in @($ml.Data.tasks)) {
            $en = '?'
            if ($null -ne $t.enabled) {
                if ($t.enabled) { $en = 'on' } else { $en = 'off' }
            }
            $rec.Add('  ' + $t.fullName + '  presence=' + $t.presence + ' enabled=' + $en + ' state=' + $t.state)
        }
    } else {
        $rec.Add('  (maint list 读取失败，退出码 ' + $ml.Code + ')')
    }

    $recFile = Join-Path $backupRoot 'migration-before.txt'
    try {
        Set-Content -LiteralPath $recFile -Value $rec -Encoding UTF8
        Write-Ok ('迁移前状态已记录 ' + $recFile)
    } catch { Write-Warn2 ('写迁移前状态记录失败：' + $_.Exception.Message) }
}

# ================================================================ 3 迁移配置
Write-Step '迁移配置'
if ($hasConfig -and -not $Force) {
    Write-Note 'config.json 已存在，跳过迁移（-Force 可覆盖，会先备份）'
} elseif ($apply) {
    if ($hasConfig -and $Force) {
        Copy-Item -LiteralPath (Join-Path $Root 'config.json') -Destination (Join-Path $backupRoot 'config.json') -Force
        Remove-Item -LiteralPath (Join-Path $Root 'config.json') -Force
        Write-Note '已备份旧 config.json 并移除，准备重新迁移'
    }
    $rc = Invoke-Qmctl -Arguments @('config', 'migrate')
    if ($rc -eq 0) { Write-Ok '迁移完成' } else { Write-Bad ("config migrate 退出码 " + $rc) }
} else {
    Write-Note '将执行 qmctl config migrate（读 v1 settings.ini → config.json）'
}

Write-Step '校验配置'
if ($apply) {
    $rc = Invoke-Qmctl -Arguments @('config', 'validate')
    if ($rc -eq 0) { Write-Ok '配置校验通过' } else { Write-Bad ("配置校验失败，退出码 " + $rc) }
} else {
    Write-Note '将执行 qmctl config validate'
}

if ($script:Failures.Count -gt 0) {
    Write-Log ''
    Write-Log '配置不可用，停止。' 'Red'
    Write-Log ("日志：" + $script:LogPath) 'Yellow'
    exit 4
}

# ================================================================ 4 快照
Write-Step '抓 baseline 快照'
if ($hasBaseline -and -not $Force) {
    Write-Note 'baseline.json 已存在，跳过（-Force 会 --reset 重建）'
} elseif ($apply) {
    $snapArgs = @('snapshot', 'create')
    if ($Force) { $snapArgs += '--reset' }
    $rc = Invoke-Qmctl -Arguments $snapArgs
    if ($rc -eq 0) { Write-Ok 'baseline 已生成并自校验通过' } else { Write-Bad ("snapshot create 退出码 " + $rc) }
} else {
    Write-Note '将执行 qmctl snapshot create（拒绝覆盖已有快照）'
}

if ($script:Failures.Count -gt 0) {
    Write-Log ''
    Write-Log '快照失败，停止：没有 baseline 就不该注册调度任务（恢复时会无依据）。' 'Red'
    exit 4
}

# 快照里的 CaptureNotes 是给人看的：夜间抓快照会留下「原始状态未知」，必须说出来
if ($apply) {
    $snapShow = Invoke-QmctlJson -Arguments @('snapshot', 'show')
    if ($snapShow.Data -and $snapShow.Data.captureNotes) {
        $notes = @($snapShow.Data.captureNotes)
        if ($notes.Count -gt 0) {
            Write-Log ''
            Write-Log '快照采集说明（不是故障，但需要你确认）：' 'Yellow'
            foreach ($n in $notes) { Write-Log ("   - " + $n) 'Yellow' }
        }
    }
}

# ================================================================ 权限加固
Write-Step '配置根权限加固（SYSTEM 会读的内容不许普通用户写）'

$secModule = Join-Path $repoRoot 'powershell\Modules\QuietMode.Security.psm1'
if (-not (Test-Path -LiteralPath $secModule)) {
    Write-Warn2 ("缺少权限模块 " + $secModule + "，跳过加固（安装仍可继续，但存在提权面）")
} else {
    try {
        Import-Module $secModule -Force
        $permBefore = Test-QmPrivilegedLayout -Root $Root -DeployDir $DeployDir
        if ($permBefore.ok) {
            Write-Ok ("权限布局已合规（检查 " + $permBefore.checked + " 项）")
        } else {
            foreach ($p in $permBefore.problems) { Write-Warn2 $p }
            if ($apply) {
                $fix = Protect-QuietModeRoot -Root $Root -DeployDir $DeployDir -Apply
                foreach ($l in $fix.lines) {
                    if ($l -like 'FAIL*') { Write-Bad $l } else { Write-Note $l }
                }
                $permAfter = Test-QmPrivilegedLayout -Root $Root -DeployDir $DeployDir
                if ($permAfter.ok) { Write-Ok '加固后复检通过' }
                else { foreach ($p in $permAfter.problems) { Write-Bad ("加固后仍不合规：" + $p) } }
            } else {
                Write-Note '将拒绝 Users 对上述路径的写/删/改权限（logs 单独放行追加）'
            }
        }
    } catch { Write-Warn2 ("权限检查失败：" + $_.Exception.Message) }
}

# ================================================================ 5 注册任务
Write-Step '注册统一计划任务'
if ($apply) {
    $rc = Invoke-Qmctl -Arguments @('task', 'install', '--apply')
    if ($rc -ne 0) { Write-Bad ("task install 退出码 " + $rc) }
} else {
    Write-Note '将执行 qmctl task install --apply（\QuietMode\QuietMode-Reconcile，SYSTEM + 最高权限）'
}

$st = Invoke-QmctlJson -Arguments @('task', 'status')
if ($st.Data -and $st.Data.exists -eq $true) { Write-Ok ("v2 任务已存在，状态 " + $st.Data.state) }
elseif ($apply) { Write-Bad '注册后回读不到 v2 任务' }
else { Write-Note '当前未注册（符合 DryRun 预期）' }

# 不变量：v2 没确认落地之前，绝不动 v1。否则停掉 v1 就变成「两套都没有」。
if ($apply -and -not ($st.Data -and $st.Data.exists -eq $true)) {
    Write-Log ''
    Write-Log 'v2 任务没注册成功：保留 v1 现状，不做任何切换。' 'Red'
    Write-Log ("排查：" + $script:LogPath) 'Yellow'
    exit 6
}

# 任务文件落在 \QuietMode\ 下，托盘要能读到才算安装完成
if ($apply) {
    $v2TaskFile = Join-Path $env:WINDIR 'System32\Tasks\QuietMode\QuietMode-Reconcile'
    if ((Test-PathSafe $v2TaskFile) -eq $true) { Write-Ok '任务文件已落盘' }
    else { Write-Warn2 ("找不到任务文件 " + $v2TaskFile + "（可能被策略重定向）") }
}

# ================================================================ 6 授权读取
Write-Step '授予普通用户读取计划任务（托盘免提权才看得到状态）'
$taskFiles = @(
    (Join-Path $env:WINDIR 'System32\Tasks\QuietMode\QuietMode-Reconcile'),
    (Join-Path $env:WINDIR 'System32\Tasks\QuietHDD\QuietHDD_NightGate'),
    (Join-Path $env:WINDIR 'System32\Tasks\QuietHDD\QuietHDD_DiskGate')
)
$usersSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
foreach ($tf in $taskFiles) {
    $exists = Test-PathSafe $tf
    if ($exists -eq $false) { Write-Note "不存在：$tf"; continue }
    if ($exists -eq $null) { Write-Note ("无权访问 " + $tf + "（读不到 ≠ 不存在；Apply 时会以管理员重试）"); continue }
    try {
        $acl = Get-Acl -LiteralPath $tf
        $hasRead = $false
        foreach ($ace in $acl.Access) {
            if ($ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $usersSid.Value -and
                ($ace.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Read) -ne 0) {
                $hasRead = $true
            }
        }
        if ($hasRead) { Write-Ok ("已有读取权限：" + (Split-Path -Leaf $tf)); continue }
        if (-not $apply) { Write-Note ("将授予 Users 读取：" + (Split-Path -Leaf $tf)); continue }

        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($usersSid, 'ReadAndExecute', 'Allow')
        $acl.AddAccessRule($rule) | Out-Null
        Set-Acl -LiteralPath $tf -AclObject $acl
        $after = Get-Acl -LiteralPath $tf
        $ok = $false
        foreach ($ace in $after.Access) {
            if ($ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $usersSid.Value -and
                ($ace.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Read) -ne 0) {
                $ok = $true
            }
        }
        if ($ok) { Write-Ok ("已授予读取：" + (Split-Path -Leaf $tf)) }
        else { Write-Bad ("授予读取后回读不到 ACE：" + (Split-Path -Leaf $tf)) }
    } catch { Write-Warn2 ("处理 " + $tf + " 失败：" + $_.Exception.Message) }
}

# ================================================================ 7 停用 v1
Write-Step '停用 v1 闸门'
$legacyStillOn = New-Object System.Collections.Generic.List[string]
if ($KeepLegacy) {
    Write-Warn2 '按 -KeepLegacy 保留 v1 闸门（两套调度会互相覆盖，仅用于短时观察）'
} else {
    foreach ($full in $script:LegacyTasks) {
        $s = Get-TaskState -FullName $full
        if ($s.Exists -eq $false) { Write-Note "$full 不存在"; continue }
        if ($s.Exists -eq $null) {
            # 读不到就不算「已停用」：这是两套调度并行的风险，必须当成未完成处理
            Write-Warn2 ("$full 无法确认（" + $s.Error + "），未改动")
            $legacyStillOn.Add($full)
            continue
        }
        if ($s.Enabled -eq $false) { Write-Ok "$full 已禁用"; continue }
        if (-not $apply) { Write-Note "$full 当前启用 → 将禁用"; continue }
        try {
            $a = Set-LegacyEnabled -FullName $full -Enabled $false
            if ($a.Enabled -eq $false) { Write-Ok "$full 已禁用（回读确认）" }
            else { Write-Bad "$full 禁用后回读仍为启用（可能被外部策略改回）"; $legacyStillOn.Add($full) }
        } catch { Write-Bad ("禁用 $full 失败：" + $_.Exception.Message); $legacyStillOn.Add($full) }
    }
}

# 不变量检查：两套调度同时开着比只有 v1 更糟 —— 夜里它们会互相把对方的状态改回去。
# 既然 v1 停不掉，就把刚装的 v2 撤掉，回到「只有 v1」这个已知安全的状态。
if ($apply -and -not $KeepLegacy -and $legacyStillOn.Count -gt 0) {
    Write-Log ''
    Write-Log ('有 ' + $legacyStillOn.Count + ' 个 v1 闸门停不掉：' + ($legacyStillOn -join ', ')) 'Red'
    Write-Log '为避免两套调度器同时控制磁盘，回滚刚注册的 v2 任务。' 'Yellow'
    $rc = Invoke-Qmctl -Arguments @('task', 'uninstall', '--apply')
    if ($rc -eq 0) { Write-Ok 'v2 任务已移除，回到只有 v1 的状态' }
    else { Write-Bad ("回滚 v2 任务失败（退出码 " + $rc + "）：现在可能两套并存，请手工禁用其中一套") }
    if ($script:LogPath) { Write-Log ("日志：" + $script:LogPath) 'Yellow' }
    exit 7
}

# ================================================================ 8 交回自动
Write-Step '交回自动调度'
if ($apply) {
    $rc = Invoke-Qmctl -Arguments @('state', 'auto')
    if ($rc -eq 0) { Write-Ok 'ModeOn=false / AutoSilentSuppressed=false / SchedulePaused=false' }
    else { Write-Bad ("state auto 退出码 " + $rc) }
} else {
    Write-Note '将执行 qmctl state auto（清掉手动静音与「今晚不再自动开」的抑制标记）'
}

# ================================================================ 9 立即对齐一次
Write-Step '立即对齐当前时间窗口'
if ($apply) {
    $rc = Invoke-Qmctl -Arguments @('reconcile', '--apply', '--trigger', 'install')
    if ($rc -eq 0) { Write-Ok 'reconcile 成功' }
    elseif ($rc -eq 2) { Write-Warn2 'reconcile 部分步骤未执行（多为占用或未提权），见 logs\worker.log' }
    else { Write-Bad ("reconcile 退出码 " + $rc) }
} else {
    Write-Note '将执行 qmctl reconcile --apply --trigger install'
}

# ================================================================ 10 托盘
Write-Step '托盘'
Start-Tray -ExePath (Join-Path $DeployDir $script:ExeName) -Apply $apply

# ================================================================ 11 收尾自检
Write-Step '收尾自检'
$finalTask = Invoke-QmctlJson -Arguments @('task', 'status')
if ($finalTask.Data -and $finalTask.Data.exists -eq $true) {
    Write-Ok ('v2 调度任务在位，状态 ' + $finalTask.Data.state)
} elseif ($apply) {
    Write-Bad '收尾时读不到 v2 调度任务：调度已不可靠'
} else {
    Write-Note 'v2 任务当前未注册（DryRun 预期）'
}

if (-not $KeepLegacy) {
    $reEnabled = @()
    foreach ($full in $script:LegacyTasks) {
        $s = Get-TaskState -FullName $full
        if ($s.Exists -eq $true -and $s.Enabled -eq $true) { $reEnabled += $full }
        elseif ($s.Exists -eq $null) { Write-Warn2 ("收尾复核：" + $full + " 仍读不到，无法确认未启用") }
    }
    if ($reEnabled.Count -gt 0) { Write-Bad ('收尾时仍有 v1 闸门在启用：' + ($reEnabled -join ', ') + '（可能被策略改回）') }
    else { Write-Ok '没有 v1 闸门与新调度并行' }
}

# ================================================================ 汇总
Write-Log ''
Write-Log '============================================================' 'White'
if ($script:Failures.Count -eq 0 -and -not $apply) {
    Write-Log ' 演练完成：系统状态未改变。加 -Mode Apply（管理员）执行。' 'Yellow'
} elseif ($script:Failures.Count -eq 0) {
    Write-Log ' 安装完成。' 'Green'
} else {
    Write-Log ' 安装有失败项，见下方清单。' 'Red'
}
Write-Log (" 失败 " + $script:Failures.Count + " 项 / 警告 " + $script:Warnings.Count + " 项")
foreach ($f in $script:Failures) { Write-Log ("   FAIL " + $f) 'Red' }
foreach ($w in $script:Warnings) { Write-Log ("   WARN " + $w) 'Yellow' }
if ($script:LogPath) { Write-Log (" 日志 " + $script:LogPath) }
Write-Log '============================================================' 'White'
Write-Log ''

if ($script:Failures.Count -gt 0) { exit 2 }
exit 0
