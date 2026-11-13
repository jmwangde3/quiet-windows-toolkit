<#
.SYNOPSIS
    重建 baseline.json（把旧 schema 的基线升级成当前 schema）。

.DESCRIPTION
    baseline.json 记的是「维护任务在被 QuietMode 改动之前的原始状态」。
    schema 2 的基线没有「原始启用状态未知」这个标记，所以校验时会一直报：

        WARN 基线来自旧 schema（2，当前 3）：已按兼容模式读取；旧基线不含
             「原始启用状态未知」标记，若当时是在夜间采集的，维护任务可能被
             误记为禁用

    这条 WARN 不会自己消失，只能重建。重建 = 在当前时刻重新采一次现场。

    ★ 必须在白天（维护任务处于启用状态）执行 ★
    夜里采会把「已禁用」当成原始状态记下来，之后每天白天都会把维护任务
    恢复成禁用 —— 那正是 v1 时代踩过的坑。脚本会先看 state.json 的相位，
    夜间直接拒绝（确需强行重建加 -Force，但知道自己在做什么）。

    重建前会把旧 baseline.json 复制到 recovery\baseline-<时间戳>\，出问题
    拷回来即可。

    用法：
      .\Reset-Baseline.ps1              # 演练：只看计划，不动文件
      .\Reset-Baseline.ps1 -Apply       # 真重建（需要管理员）

    本机入口：H:\AI工作区\电脑相关\reset-baseline.cmd（自提权，双击即跑）
#>
[CmdletBinding()]
param(
    [string]$Qmctl = '',
    [string]$Root = 'C:\ProgramData\QuietMode',
    [switch]$Apply,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
try { $OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Write-Step { param([string]$Text) Write-Host ''; Write-Host ('== ' + $Text) -ForegroundColor White }
function Write-Ok   { param([string]$Text) Write-Host ('  OK   ' + $Text) -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host ('  FAIL ' + $Text) -ForegroundColor Red }
function Write-Note { param([string]$Text) Write-Host ('  ' + $Text) -ForegroundColor Gray }
function Write-Warn2{ param([string]$Text) Write-Host ('  WARN ' + $Text) -ForegroundColor Yellow }

# ---------------------------------------------------------------- 身份与路径
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host 'BASELINE REBUILD'
Write-Host ('  身份   : ' + $identity.Name)
Write-Host ('  管理员 : ' + $elevated)
Write-Host ('  模式   : ' + $(if ($Apply) { 'Apply' } else { 'DryRun' }))
Write-Host ('  Root   : ' + $Root)

if (-not (Test-Path -LiteralPath $Root)) {
    Write-Bad ('配置根不存在：' + $Root)
    exit 2
}

# qmctl 定位：参数 > 部署树相对路径 > PATH
# 脚本住在 _publish\quiet-windows-toolkit\powershell，部署树在 电脑相关\QuietMode，
# 从 $PSScriptRoot 往上三级才是工作区根（两级会落进 _publish）。
if ([string]::IsNullOrWhiteSpace($Qmctl)) {
    $candidates = @(
        (Join-Path $PSScriptRoot '..\..\..\QuietMode\qmctl.exe'),
        (Join-Path $PSScriptRoot '..\QuietMode\qmctl.exe'),
        (Join-Path $env:ProgramFiles 'QuietMode\qmctl.exe')
    )
    foreach ($c in $candidates) {
        $full = [IO.Path]::GetFullPath($c)
        if (Test-Path -LiteralPath $full) { $Qmctl = $full; break }
    }
    if ([string]::IsNullOrWhiteSpace($Qmctl)) {
        $cmd = Get-Command qmctl.exe -ErrorAction SilentlyContinue
        if ($cmd) { $Qmctl = $cmd.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($Qmctl) -or -not (Test-Path -LiteralPath $Qmctl)) {
    Write-Bad '找不到 qmctl.exe（用 -Qmctl 指定完整路径）'
    exit 2
}
Write-Host ('  qmctl  : ' + $Qmctl)

$BaselinePath = Join-Path $Root 'baseline.json'
$StatePath    = Join-Path $Root 'state.json'

function Invoke-Qmctl {
    # 别把参数命名为 $Args —— 那是 PowerShell 自动变量，会和 @splat 展开打架。
    param([string[]]$ArgList)
    $out = & $Qmctl @ArgList 2>&1 | Out-String
    return [pscustomobject]@{ Text = $out; Code = $LASTEXITCODE }
}

# ---------------------------------------------------------------- 相位闸门
Write-Step '相位检查（必须白天重建）'
$phase = ''
$action = ''
if (Test-Path -LiteralPath $StatePath) {
    try {
        $st = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $phase  = [string]$st.controllerState
        $action = [string]$st.lastReconcileAction
        $lastUtc = [string]$st.lastReconcileUtc
        Write-Note ('controllerState = ' + $phase + '   lastReconcileAction = ' + $action)
        if ($lastUtc) {
            try {
                $styles = [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal
                $t = [DateTime]::Parse($lastUtc, [Globalization.CultureInfo]::InvariantCulture, $styles)
                Write-Note ('最近一次 reconcile = ' + $t.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss') + '（本地）')
            } catch { Write-Note ('最近一次 reconcile = ' + $lastUtc) }
        }
    } catch { Write-Warn2 ('state.json 读不出来：' + $_.Exception.Message) }
} else {
    Write-Warn2 'state.json 不存在（全新安装？）'
}

$nightish = ($action -eq 'night') -or ($phase -match 'Night')
if ($nightish -and -not $Force) {
    Write-Bad ('当前看起来是夜间相位（' + $phase + ' / ' + $action + '），维护任务可能正被禁用。')
    Write-Note '此时重建会把「已禁用」记成原始状态，之后每天白天都会把维护任务恢复成禁用。'
    Write-Note '等到 10:02 白天链跑完再执行；确需强行重建加 -Force。'
    exit 3
}
if ($nightish) { Write-Warn2 '夜间相位 + -Force：继续，但请确认维护任务当前确实是启用状态。' }
else { Write-Ok '白天相位，可以重建' }

# ---------------------------------------------------------------- 现状
Write-Step '现有 baseline'
if (Test-Path -LiteralPath $BaselinePath) {
    $fi = Get-Item -LiteralPath $BaselinePath
    $hash = (Get-FileHash -LiteralPath $BaselinePath -Algorithm SHA256).Hash
    Write-Note ('路径   : ' + $BaselinePath)
    Write-Note ('大小   : ' + $fi.Length + ' B   修改时间 : ' + $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    Write-Note ('SHA256 : ' + $hash.ToLower())
    $r = Invoke-Qmctl @('snapshot','verify')
    foreach ($line in ($r.Text -split "`r?`n")) {
        if ($line.Trim()) { Write-Note ('  ' + $line.TrimEnd()) }
    }
    $schemaWarn = ($r.Text -match '旧 schema')
} else {
    Write-Note '（baseline.json 不存在，将新建）'
    $schemaWarn = $false
}

if (-not $Apply) {
    Write-Host ''
    Write-Host '练习结束：系统未改变。加 -Apply（管理员）执行重建。' -ForegroundColor Yellow
    if (-not $schemaWarn -and (Test-Path -LiteralPath $BaselinePath)) {
        Write-Note '提示：当前 baseline 没有 schema 版本告警，可能不需要重建。'
    }
    exit 0
}

if (-not $elevated) {
    Write-Bad '重建需要管理员（baseline.json 所在目录对普通用户只读）'
    exit 2
}

# ---------------------------------------------------------------- 备份
Write-Step '备份旧 baseline'
$backupDir = ''
if (Test-Path -LiteralPath $BaselinePath) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDir = Join-Path $Root ('recovery\baseline-' + $stamp)
    try {
        if (-not (Test-Path -LiteralPath $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
        Copy-Item -LiteralPath $BaselinePath -Destination (Join-Path $backupDir 'baseline.json') -Force
        $bak = Join-Path $backupDir 'baseline.json'
        $h1 = (Get-FileHash -LiteralPath $BaselinePath -Algorithm SHA256).Hash
        $h2 = (Get-FileHash -LiteralPath $bak -Algorithm SHA256).Hash
        if ($h1 -ne $h2) { throw '备份校验和不一致' }
        Write-Ok ('已备份：' + $backupDir)
    } catch {
        Write-Bad ('备份失败，放弃重建：' + $_.Exception.Message)
        exit 3
    }
} else {
    Write-Note '没有旧文件可备份'
}

# ---------------------------------------------------------------- 重建
Write-Step '重建（snapshot create --reset）'
$r = Invoke-Qmctl @('snapshot','create','--reset')
foreach ($line in ($r.Text -split "`r?`n")) {
    if ($line.Trim()) { Write-Host ('  ' + $line.TrimEnd()) }
}
if ($r.Code -ne 0) {
    Write-Bad ('重建失败，退出码 ' + $r.Code)
    if ($backupDir) { Write-Note ('旧文件仍在：' + (Join-Path $backupDir 'baseline.json')) }
    exit 4
}

# ---------------------------------------------------------------- 回读校验
Write-Step '回读校验'
$ok = $true

if (-not (Test-Path -LiteralPath $BaselinePath)) {
    Write-Bad 'baseline.json 没生成'
    exit 4
}
Write-Ok ('文件存在：' + $BaselinePath + '（' + (Get-Item -LiteralPath $BaselinePath).Length + ' B）')

$after = Invoke-Qmctl @('snapshot','verify')
foreach ($line in ($after.Text -split "`r?`n")) {
    if ($line.Trim()) { Write-Host ('  ' + $line.TrimEnd()) }
}
if ($after.Text -match '旧 schema') {
    Write-Bad '仍然报旧 schema —— 重建没生效'
    $ok = $false
} else {
    Write-Ok 'schema 版本告警已消失'
}

$afterShow = Invoke-Qmctl @('snapshot','show')
$m = [regex]::Match($afterShow.Text, 'schemaVersion\s*[=:]\s*(\d+)')
if ($m.Success) { Write-Note ('新 schemaVersion = ' + $m.Groups[1].Value) }

# 完整性：不再 Partial
if ($after.Text -match 'complete=False') { Write-Warn2 '新基线仍不完整（complete=False），部分任务探针没读到' }
else { Write-Ok '新基线完整（complete=True）' }

# ---------------------------------------------------------------- 对照
Write-Step '维护任务对照（BASELINE vs NOW）'
$ms = Invoke-Qmctl @('maint','status')
$tail = ($ms.Text -split "`r?`n") | Where-Object { $_ -match '\S' }
$collect = $false
foreach ($line in $tail) {
    if ($line -match '^\s*TASK\s+BASELINE\s+NOW') { $collect = $true }
    if ($collect) { Write-Host ('  ' + $line.TrimEnd()) }
}
if (-not $collect) {
    foreach ($line in ($tail | Select-Object -Last 14)) { Write-Host ('  ' + $line.TrimEnd()) }
}

Write-Host ''
if ($ok) {
    Write-Host '完成。baseline 已是当前 schema，今晚夜跑照常。' -ForegroundColor Green
    if ($backupDir) { Write-Host ('回滚方式：把 ' + (Join-Path $backupDir 'baseline.json') + ' 复制回 ' + $BaselinePath) -ForegroundColor DarkGray }
    exit 0
} else {
    Write-Host '重建后仍有问题，看上面的 FAIL 行。' -ForegroundColor Red
    exit 5
}
