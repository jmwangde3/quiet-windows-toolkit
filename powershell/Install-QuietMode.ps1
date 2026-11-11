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
$script:LegacyTasks = @('\QuietHDD\QuietHDD_NightGate', '\QuietHDD\QuietHDD_DiskGate')

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

# 用部署目录里的 qmctl（才是任务将来调用的那个）
$script:QmctlPath = Join-Path $DeployDir 'qmctl.exe'
$srcQmctl = Join-Path $cliBin 'qmctl.exe'

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

# ================================================================ 1 预检
Write-Step '预检'

$required = @(
    @{ Path = $srcQmctl; Name = 'qmctl.exe' },
    @{ Path = (Join-Path $cliBin 'QuietMode.Core.dll'); Name = 'QuietMode.Core.dll' },
    @{ Path = (Join-Path $cliBin 'QuietMode.Windows.dll'); Name = 'QuietMode.Windows.dll' },
    @{ Path = (Join-Path $trayBin 'QuietMode.exe'); Name = 'QuietMode.exe' }
)
foreach ($p in $required) {
    if (Test-Path -LiteralPath $p.Path) {
        $f = Get-Item -LiteralPath $p.Path
        Write-Ok ("{0}  {1:N0} B  {2}" -f $p.Name, $f.Length, (Get-Date $f.LastWriteTime -Format 'MM-dd HH:mm'))
    } else {
        Write-Bad ("缺少 $($p.Name)：$($p.Path)（先 dotnet build QuietMode.sln -c Release）")
    }
}

if ($script:Failures.Count -gt 0) {
    Write-Log ''
    Write-Log '预检失败，未做任何改动。' 'Red'
    exit 3
}

$hasConfig = Test-Path -LiteralPath (Join-Path $Root 'config.json')
$hasBaseline = Test-Path -LiteralPath (Join-Path $Root 'baseline.json')
$legacySettings = Join-Path $env:APPDATA 'QuietMode\settings.ini'

$cfgText = '不存在'
if ($hasConfig) { $cfgText = '已存在' }
$baseText = '不存在'
if ($hasBaseline) { $baseText = '已存在' }
$legacyText = '不存在'
if (Test-Path -LiteralPath $legacySettings) { $legacyText = '存在（可迁移）' }

Write-Note ("config.json    " + $cfgText)
Write-Note ("baseline.json  " + $baseText)
Write-Note ("v1 settings.ini " + $legacyText)

# 用源 qmctl 做只读体检（部署目录里可能还是旧版）
if (-not $hasConfig) {
    Write-Note '配置还没迁移，先跳过 task status 探针（qmctl 读不到 config.json 属预期）'
} else {
    $probePath = $script:QmctlPath
    $script:QmctlPath = $srcQmctl
    $st0 = Invoke-QmctlJson -Arguments @('task', 'status')
    $script:QmctlPath = $probePath

    if ($st0.Code -eq 0 -or $st0.Code -eq 2) { Write-Ok 'qmctl 可执行，task status 正常响应' }
    else { Write-Warn2 ("qmctl task status 退出码 " + $st0.Code) }

    if ($st0.Data) {
        $taskText = '未注册'
        if ($st0.Data.exists -eq $true) { $taskText = '已注册，状态 ' + $st0.Data.state }
        Write-Note ("v2 任务        " + $taskText)
        if ($st0.Data.conflict) { Write-Note 'v1 闸门        仍在启用（会与 v2 抢同一件事）' }
    }
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

# ================================================================ 6 授权读取
Write-Step '授予普通用户读取计划任务（托盘免提权才看得到状态）'
$taskFiles = @(
    (Join-Path $env:WINDIR 'System32\Tasks\QuietMode\QuietMode-Reconcile'),
    (Join-Path $env:WINDIR 'System32\Tasks\QuietHDD\QuietHDD_NightGate'),
    (Join-Path $env:WINDIR 'System32\Tasks\QuietHDD\QuietHDD_DiskGate')
)
$usersSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
foreach ($tf in $taskFiles) {
    if (-not (Test-Path -LiteralPath $tf)) { Write-Note "不存在：$tf"; continue }
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
if ($KeepLegacy) {
    Write-Warn2 '按 -KeepLegacy 保留 v1 闸门（两套调度会互相覆盖，仅用于短时观察）'
} else {
    foreach ($full in $script:LegacyTasks) {
        $s = Get-TaskState -FullName $full
        if ($s.Exists -eq $false) { Write-Note "$full 不存在"; continue }
        if ($s.Exists -eq $null) { Write-Warn2 ("$full 无法确认（" + $s.Error + "），未改动"); continue }
        if ($s.Enabled -eq $false) { Write-Ok "$full 已禁用"; continue }
        if (-not $apply) { Write-Note "$full 当前启用 → 将禁用"; continue }
        try {
            $a = Set-LegacyEnabled -FullName $full -Enabled $false
            if ($a.Enabled -eq $false) { Write-Ok "$full 已禁用（回读确认）" }
            else { Write-Bad "$full 禁用后回读仍为启用（可能被外部策略改回）" }
        } catch { Write-Bad ("禁用 $full 失败：" + $_.Exception.Message) }
    }
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
