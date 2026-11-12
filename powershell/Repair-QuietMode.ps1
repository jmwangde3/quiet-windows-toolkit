#Requires -Version 5.1
<#
    Repair-QuietMode.ps1 — 诊断并（可选）修复一套已安装的 QuietMode。

    它只回答一个问题：「现在这套东西还能不能在今晚开工？」
    逐项体检、每项给结论，默认不改任何东西；-Apply 才会做三件低风险修复：
      * 重新注册计划任务（任务被外部删掉/改坏时）
      * 立刻对齐一次当前时间窗口
      * 重新加固配置根权限

    不做的事：不动磁盘、不动维护任务、不重建 baseline。
    这些属于「有后果」的操作，交给 reconcile / snapshot create 显式执行。

    用法：
      .\Repair-QuietMode.ps1                      # 只诊断
      .\Repair-QuietMode.ps1 -Apply               # 诊断 + 对齐一次
      .\Repair-QuietMode.ps1 -Apply -ReRegisterTask
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\ProgramData\QuietMode',
    [string]$DeployDir,
    [switch]$Apply,
    [switch]$ReRegisterTask
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$script:Bad = 0
$script:Warn = 0

function Say-Ok    { param([string]$T) Write-Host ("  OK    " + $T) -ForegroundColor Green }
function Say-Warn  { param([string]$T) Write-Host ("  WARN  " + $T) -ForegroundColor Yellow; $script:Warn++ }
function Say-Bad   { param([string]$T) Write-Host ("  FAIL  " + $T) -ForegroundColor Red; $script:Bad++ }
function Say-Note  { param([string]$T) Write-Host ("        " + $T) -ForegroundColor DarkGray }
function Section    { param([string]$T) Write-Host ''; Write-Host ("== " + $T + " ==") -ForegroundColor Cyan }

function Resolve-RepoRoot {
    $dir = $PSScriptRoot
    for ($i = 0; $i -lt 6 -and $dir; $i++) {
        if (Test-Path -LiteralPath (Join-Path $dir 'QuietMode.sln')) { return $dir }
        $dir = Split-Path -Parent $dir
    }
    return (Split-Path -Parent $PSScriptRoot)
}

$repoRoot = Resolve-RepoRoot
if (-not $DeployDir) { $DeployDir = Join-Path $repoRoot 'QuietMode' }
$DeployDir = [IO.Path]::GetFullPath($DeployDir)
$qmctl = Join-Path $DeployDir 'qmctl.exe'
if (-not (Test-Path -LiteralPath $qmctl)) {
    $alt = Join-Path $repoRoot 'src\QuietMode.Cli\bin\Release\net481\qmctl.exe'
    if (Test-Path -LiteralPath $alt) { $qmctl = $alt }
}

function Invoke-Qm {
    param([string[]]$Arguments)
    $out = & $qmctl (@('--root', $Root) + $Arguments) 2>&1 | Out-String
    return [pscustomobject]@{ Text = $out; Code = $LASTEXITCODE }
}

function Invoke-QmJson {
    param([string[]]$Arguments)
    $tmp = Join-Path $env:TEMP ('qm-repair-' + [Guid]::NewGuid().ToString('N') + '.json')
    & $qmctl (@('--root', $Root) + $Arguments + @('--json', '--out', $tmp, '--quiet')) | Out-Null
    $code = $LASTEXITCODE
    $data = $null
    if (Test-Path -LiteralPath $tmp) {
        try { $data = [System.IO.File]::ReadAllText($tmp, [System.Text.Encoding]::UTF8) | ConvertFrom-Json } catch { }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ Code = $code; Data = $data }
}

Write-Host ''
Write-Host 'QuietMode 体检' -ForegroundColor White
Write-Host ("  root    " + $Root)
Write-Host ("  deploy  " + $DeployDir)
Write-Host ("  apply   " + [bool]$Apply)

Section '文件与脚本'
if (Test-Path -LiteralPath $qmctl) { Say-Ok ("qmctl.exe  " + $qmctl) } else { Say-Bad '找不到 qmctl.exe' }
# Worker 必须跟 qmctl 在同一棵树里：qmctl 从自身目录向上找 powershell\Modules
$scriptRoot = Split-Path -Parent $qmctl
$worker = Join-Path $scriptRoot 'powershell\Worker\Invoke-QuietModeWorker.ps1'
if (-not (Test-Path -LiteralPath $worker)) {
    $altWorker = Join-Path $repoRoot 'powershell\Worker\Invoke-QuietModeWorker.ps1'
    if (Test-Path -LiteralPath $altWorker) {
        $worker = $altWorker
        Say-Warn '部署目录里没有 Worker，用的是仓库那份（-DeployDir 可能指错了）'
    }
}
if (Test-Path -LiteralPath $worker) { Say-Ok ("Worker 脚本可达  " + $worker) }
else { Say-Bad ("Worker 脚本缺失：" + $worker + "（夜间任务会整夜空转）") }
$tray = Join-Path $scriptRoot 'QuietMode.exe'
if (Test-Path -LiteralPath $tray) { Say-Ok '托盘可执行文件存在' } else { Say-Warn '托盘可执行文件缺失（不影响调度）' }

Section '配置'
$cfg = Invoke-QmJson @('config', 'validate')
if ($null -eq $cfg.Data) { Say-Bad ("config validate 无输出（退出码 " + $cfg.Code + "）") }
elseif ($cfg.Code -eq 0) { Say-Ok 'config.json 通过校验' }
else {
    Say-Bad 'config.json 未通过校验'
    foreach ($e in @($cfg.Data.errors)) { if ($e) { Say-Note $e } }
}

Section 'baseline'
$base = Invoke-QmJson @('snapshot', 'verify')
if ($null -eq $base.Data) { Say-Bad 'snapshot verify 无输出' }
else {
    switch ([string]$base.Data.state) {
        'Complete' { Say-Ok 'baseline 完整（可据此恢复）' }
        'Partial'  { Say-Warn 'baseline 不完整：已记录的能恢复，没记录的不会被猜' }
        'Corrupt'  { Say-Bad 'baseline 损坏：不能用于恢复；需要确认后 snapshot create --reset' }
        default    { Say-Bad '没有 baseline：白天恢复没有依据' }
    }
    foreach ($e in @($base.Data.errors)) { if ($e) { Say-Note $e } }
    foreach ($w in @($base.Data.warnings)) { if ($w) { Say-Note $w } }
}

Section '调度'
$task = Invoke-QmJson @('task', 'status')
if ($null -eq $task.Data) { Say-Bad 'task status 无输出' }
else {
    if ($task.Data.exists -eq $true) { Say-Ok ("v2 任务存在，状态 " + $task.Data.state) }
    elseif ($task.Data.exists -eq $false) { Say-Bad 'v2 任务不存在（夜间不会自动开始）' }
    else { Say-Bad 'v2 任务状态读不到（可能被 ACL 挡住）' }

    if ($task.Data.conflict -eq $true) {
        Say-Warn 'v1 闸门可能仍在启用或读不到（两套调度会互相覆盖）；以管理员身份再查一次'
    } else { Say-Ok '没有检测到 v1 闸门冲突' }

    if ($task.Data.heartbeat) { Say-Note ("最后心跳：" + $task.Data.heartbeat) }
    else { Say-Warn '没有心跳记录（任务可能从未成功运行过）' }
}

Section '时间窗口与决策（只读）'
$plan = Invoke-QmJson @('reconcile')
if ($null -eq $plan.Data) { Say-Bad 'reconcile 演练无输出' }
else {
    $d = $plan.Data.decision
    if ($d) {
        Say-Note ("窗口：" + $plan.Data.window.reason)
        Say-Note ("决策：" + $d.stateText + " → " + $d.action + "（" + $d.reason + "）")
        if ($d.action -eq 'none') { Say-Ok '当前无需动作' } else { Say-Warn ('下次 reconcile 会执行 ' + $d.action) }
    }
    $w = $plan.Data.verification
    if ($w) {
        Say-Note ("现状核对：" + $w.verdict + " / " + $w.classification)
        foreach ($p in @($w.problems)) { if ($p) { Say-Bad $p } }
        foreach ($x in @($w.warnings)) { if ($x) { Say-Warn $x } }
    }
}

Section '组件'
$spin = Invoke-QmJson @('spin', 'show')
if ($spin.Data) {
    if ($spin.Data.matches -eq $true) { Say-Ok ("DISKIDLE AC=" + $spin.Data.acSeconds + "s 与配置一致") }
    elseif ($spin.Data.reconcileManaged -eq $true) { Say-Warn ("DISKIDLE AC=" + $spin.Data.acSeconds + "s，期望 " + $spin.Data.expectedSeconds + "s（下次 reconcile 会拉回）") }
    else { Say-Note ("DISKIDLE AC=" + $spin.Data.acSeconds + "s（配置未启用，不参与对账）") }
}

$disks = Invoke-QmJson @('disks', 'resolve')
if ($disks.Data -and $disks.Data.targets) {
    foreach ($t in @($disks.Data.targets)) {
        if ($t.status -eq 'matched') { Say-Ok ("磁盘 " + $t.uniqueId + " 身份唯一") }
        else { Say-Bad ("磁盘 " + $t.uniqueId + "：" + $t.status + "（" + $t.reason + "）") }
    }
} else { Say-Note '没有配置磁盘目标' }

Section '配置根权限'
$secModule = Join-Path $PSScriptRoot 'Modules\QuietMode.Security.psm1'
if (Test-Path -LiteralPath $secModule) {
    Import-Module $secModule -Force
    $perm = Test-QmPrivilegedLayout -Root $Root -DeployDir $DeployDir
    if ($perm.ok) { Say-Ok ("已检查 " + $perm.checked + " 条路径，没有普通用户可写的 SYSTEM 输入") }
    else { foreach ($p in $perm.problems) { Say-Bad $p } }

    if (-not $perm.ok -and $Apply) {
        $fix = Protect-QuietModeRoot -Root $Root -DeployDir $DeployDir -Apply
        foreach ($line in $fix.lines) { if ($line -like 'FAIL*') { Say-Bad $line } else { Say-Note $line } }
    } elseif (-not $perm.ok) {
        Say-Note '加 -Apply 可自动加固（只加 Deny ACE，不改其它系统策略）'
    }
} else { Say-Warn '找不到 QuietMode.Security 模块，跳过权限体检' }

if ($Apply) {
    Section '修复动作'
    if ($ReRegisterTask) {
        $r = Invoke-Qm @('task', 'install', '--apply')
        if ($r.Code -eq 0) { Say-Ok '已重新注册计划任务' } else { Say-Bad ("task install 退出码 " + $r.Code + "：" + $r.Text.Trim()) }
    }
    $r2 = Invoke-Qm @('reconcile', '--apply', '--trigger', 'repair')
    if ($r2.Code -eq 0) { Say-Ok '已按当前窗口对齐一次' }
    elseif ($r2.Code -eq 2) { Say-Warn '对齐完成但有失败项，见 logs\reconcile.log' }
    else { Say-Bad ("reconcile --apply 退出码 " + $r2.Code) }
}

Write-Host ''
if ($script:Bad -gt 0) {
    Write-Host ("体检结果：{0} 项失败 / {1} 项警告" -f $script:Bad, $script:Warn) -ForegroundColor Red
    exit 2
}
if ($script:Warn -gt 0) {
    Write-Host ("体检结果：通过，但有 {0} 项警告" -f $script:Warn) -ForegroundColor Yellow
    exit 0
}
Write-Host '体检结果：全部通过' -ForegroundColor Green
exit 0
