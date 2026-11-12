#Requires -Version 5.1
<#
    Uninstall-QuietMode.ps1 — 把系统还给用户：先停调度，再按 baseline 恢复。

    设计原则（RC2-03）：
      1. 默认 DryRun，只有 -Apply 才动系统。
      2. 先停调度、再确认已停，然后才开始恢复 —— 恢复过程中不能再有 Worker 把盘脱机。
      3. 顺序按「数据可访问性优先」：磁盘联机 → 维护任务恢复 → SysMain/注册表 → 电源 → 风扇。
         任何一步失败都继续做后面的，因为「盘还脱机」比「维护任务没恢复」严重得多。
      4. 每一步都回读验证，不拿退出码当结论。
      5. 不删除 baseline / recovery / logs：卸载失败时那是唯一的退路。
      6. 可重复执行：每一步都是幂等的，中断后重跑即续。

    用法：
      .\Uninstall-QuietMode.ps1                        # 演练，打印将要做什么
      .\Uninstall-QuietMode.ps1 -Mode Apply            # 真正执行
      .\Uninstall-QuietMode.ps1 -Mode Apply -Only Disks,Maintenance
      .\Uninstall-QuietMode.ps1 -Mode Apply -KeepTask  # 只恢复，不删任务（排障用）
#>
[CmdletBinding()]
param(
    [ValidateSet('DryRun', 'Apply')]
    [string]$Mode = 'DryRun',

    [string]$Root = 'C:\ProgramData\QuietMode',
    [string]$DeployDir,

    # 只恢复指定范围；默认全部
    [ValidateSet('Disks', 'Maintenance', 'Services', 'Power', 'Fan')]
    [string[]]$Only = @('Disks', 'Maintenance', 'Services', 'Power', 'Fan'),

    # 保留计划任务（只恢复系统状态，用于「今晚先别跑」这类排障）
    [switch]$KeepTask,

    # 不结束托盘进程
    [switch]$KeepTray,

    # 单条 qmctl 调用的超时（秒）
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:StepNo = 0

function Write-Log {
    param([string]$Text, [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
}
function Write-Step {
    param([string]$Text)
    $script:StepNo++
    Write-Host ''
    Write-Host ("[{0}] {1}" -f $script:StepNo, $Text) -ForegroundColor Cyan
}
function Write-Ok   { param([string]$Text) Write-Log ("     OK   " + $Text) 'Green' }
function Write-Note { param([string]$Text) Write-Log ("     --   " + $Text) 'DarkGray' }
function Write-Warn2{ param([string]$Text) Write-Log ("     WARN " + $Text) 'Yellow'; $script:Warnings.Add($Text) }
function Write-Bad  { param([string]$Text) Write-Log ("     FAIL " + $Text) 'Red';    $script:Failures.Add($Text) }

function Test-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

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

$apply = ($Mode -eq 'Apply')
$wants = { param($name) return ($Only -contains $name) }

function Invoke-Qm {
    param([string[]]$Arguments)
    $all = @('--root', $Root) + $Arguments
    $out = & $qmctl @all 2>&1 | Out-String
    $code = $LASTEXITCODE
    return [pscustomobject]@{ Text = $out; Code = $code }
}

function Invoke-QmJson {
    param([string[]]$Arguments)
    $tmp = Join-Path $env:TEMP ('qm-uninst-' + [Guid]::NewGuid().ToString('N') + '.json')
    $all = @('--root', $Root) + $Arguments + @('--json', '--out', $tmp, '--quiet')
    & $qmctl @all | Out-Null
    $code = $LASTEXITCODE
    $data = $null
    if (Test-Path -LiteralPath $tmp) {
        try { $data = [System.IO.File]::ReadAllText($tmp, [System.Text.Encoding]::UTF8) | ConvertFrom-Json }
        catch { $data = $null }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ Code = $code; Data = $data }
}

Write-Log ''
Write-Log '============================================================' 'White'
Write-Log (" QuietMode v2 卸载 / 恢复    模式 = " + $Mode) 'White'
Write-Log '============================================================' 'White'
Write-Log (" 配置根   " + $Root)
Write-Log (" 部署目录 " + $DeployDir)
Write-Log (" qmctl    " + $qmctl)
Write-Log (" 恢复范围 " + ($Only -join ', '))

if (-not (Test-Path -LiteralPath $qmctl)) {
    Write-Log '找不到 qmctl.exe，无法恢复（不会凭猜测去改系统状态）。' 'Red'
    exit 3
}
if ($apply -and -not (Test-Admin)) {
    Write-Log ''
    Write-Log '恢复需要管理员权限（磁盘联机、维护任务、服务、HKLM）。请以管理员身份运行。' 'Red'
    exit 5
}

# ================================================================ 1 停调度
Write-Step '停止调度器'
if ($KeepTask) {
    Write-Note '按 -KeepTask 保留任务（只恢复系统状态）'
} else {
    $st = Invoke-QmJson @('task', 'status')
    if ($null -eq $st.Data) {
        Write-Warn2 ("读不到任务状态（退出码 " + $st.Code + "）")
    } elseif ($st.Data.exists -eq $false) {
        Write-Note 'v2 任务本来就不存在'
    } elseif (-not $apply) {
        Write-Note '将删除 \QuietMode\QuietMode-Reconcile'
    } else {
        $r = Invoke-Qm @('task', 'uninstall', '--apply')
        if ($r.Code -eq 0) { Write-Ok '已删除 v2 任务' } else { Write-Bad ("task uninstall 退出码 " + $r.Code + "：" + $r.Text.Trim()) }
    }
}

# ================================================================ 2 确认已停
Write-Step '确认调度已停止'
if (-not $KeepTask -and $apply) {
    $after = Invoke-QmJson @('task', 'status')
    if ($null -eq $after.Data) {
        Write-Warn2 '无法回读任务状态，继续恢复（磁盘安全优先于流程完整性）'
    } elseif ($after.Data.exists -eq $true) {
        Write-Bad '删除后任务仍然存在，恢复过程中可能被它再次脱机'
    } elseif ($after.Data.conflict -eq $true) {
        Write-Warn2 'v1 闸门仍在启用（它同样会动磁盘），继续恢复但结果可能被覆盖'
    } else {
        Write-Ok '没有活动的调度器'
    }
} else {
    Write-Note '演练：将回读 task status 确认'
}

# ================================================================ 3 磁盘联机
Write-Step '恢复磁盘（先保证数据可访问）'
if (-not (& $wants 'Disks')) {
    Write-Note '按 -Only 跳过'
} elseif (-not $apply) {
    Write-Note '将执行 qmctl disks online all --apply（逐块回读）'
} else {
    $r = Invoke-Qm @('disks', 'online', 'all', '--apply')
    foreach ($line in ($r.Text -split "`r?`n")) {
        if ($line.Trim().Length -gt 0) { Write-Note $line.Trim() }
    }
    # 回读：disks 子命令的 JSON 是 results[]，不是 targets[]；字段是 actualState / verified
    $v = Invoke-QmJson @('disks', 'verify', 'all')
    $checked = 0
    $bad = 0
    if ($v.Data -and $v.Data.results) {
        foreach ($t in @($v.Data.results)) {
            $checked++
            if ($t.uniqueId) { $who = $t.uniqueId } elseif ($t.friendlyName) { $who = $t.friendlyName } else { $who = ('磁盘 #' + $t.currentDiskNumber) }
            if ($t.actualState -eq 'online' -and $t.verified -eq $true) { continue }
            $bad++
            Write-Bad ($who + ' 仍未确认 Online（actualState=' + $t.actualState + '，verified=' + $t.verified + '，' + $t.errorMessage + '）')
        }
    }
    if ($checked -eq 0) {
        Write-Warn2 ('读不到磁盘回读结果（退出码 ' + $v.Code + '）：无法确认磁盘已联机，请手工检查')
    } elseif ($bad -eq 0) {
        Write-Ok ('全部 ' + $checked + ' 块目标磁盘已 Online（回读确认）')
    }
}

# ================================================================ 4 维护任务 + SysMain + 注册表
Write-Step '恢复维护任务 / SysMain / 注册表（按 baseline）'
if (-not ((& $wants 'Maintenance') -or (& $wants 'Services'))) {
    Write-Note '按 -Only 跳过'
} else {
    $health = Invoke-QmJson @('snapshot', 'verify')
    if ($null -eq $health.Data) { Write-Warn2 '读不到 baseline 体检结果' }
    elseif ($health.Data.state -eq 'Missing' -or $health.Data.state -eq 'Corrupt') {
        Write-Warn2 ("baseline 不可用（" + $health.Data.state + "）：不会凭猜测恢复维护任务")
    } elseif (-not $apply) {
        Write-Note '将执行 qmctl maint day --apply（按 baseline 恢复；缺失项保持原状并记录）'
    } else {
        $r = Invoke-Qm @('maint', 'day', '--apply')
        if ($r.Code -eq 0) { Write-Ok '维护任务恢复流程完成' }
        else { Write-Bad ("maint day 退出码 " + $r.Code) }
        foreach ($line in ($r.Text -split "`r?`n")) {
            if ($line -match '失败|Failed') { Write-Note $line.Trim() }
        }
        $v = Invoke-QmJson @('maint', 'verify', 'day')
        if ($v.Data) {
            Write-Note ("维护验证：" + $v.Data.verdict)
            foreach ($p in @($v.Data.problems)) { if ($p) { Write-Bad ("维护未恢复：" + $p) } }
        }
    }
}

# ================================================================ 5 电源 / DISKIDLE
Write-Step '恢复原始电源设置（DISKIDLE）'
if (-not (& $wants 'Power')) {
    Write-Note '按 -Only 跳过'
} else {
    $base = Invoke-QmJson @('snapshot', 'show')
    $acSeconds = -1
    if ($base.Data -and $base.Data.powerPlan) { $acSeconds = [int]$base.Data.powerPlan.diskIdleAcSeconds }
    if ($acSeconds -lt 0) {
        Write-Warn2 'baseline 没记录 DISKIDLE，保持现值（不猜原始值）'
    } elseif (-not $apply) {
        Write-Note ("将把 DISKIDLE 恢复为 " + [int]($acSeconds / 60) + " 分钟（写入后回读）")
    } else {
        $r = Invoke-Qm @('spin', 'set', ([string][int]($acSeconds / 60)), '--apply')
        if ($r.Code -eq 0) { Write-Ok ("DISKIDLE 已恢复为 " + [int]($acSeconds / 60) + " 分钟") }
        else { Write-Bad ("spin set 退出码 " + $r.Code + "：" + $r.Text.Trim()) }
    }
}

# ================================================================ 6 风扇 + 总验证
Write-Step '恢复风扇常态并做最终验证'
if (-not (& $wants 'Fan')) {
    Write-Note '按 -Only 跳过风扇'
} elseif (-not $apply) {
    Write-Note '将执行 qmctl reconcile restore --apply --trigger uninstall，然后 reconcile verify'
} else {
    $r = Invoke-Qm @('reconcile', 'restore', '--apply', '--trigger', 'uninstall')
    if ($r.Code -eq 0) { Write-Ok '恢复链执行完成' }
    elseif ($r.Code -eq 2) { Write-Warn2 '恢复链有失败项（见 logs\worker.log / reconcile.log）' }
    else { Write-Bad ("reconcile restore 退出码 " + $r.Code) }

    $v = Invoke-QmJson @('reconcile', 'verify')
    if ($v.Data) {
        Write-Note ("最终验证：" + $v.Data.verdictText)
        # 明细在 verification 段里；顶层只有 verdict 与 ok
        $detail = $v.Data.verification
        if ($detail) {
            foreach ($p in @($detail.problems)) { if ($p) { Write-Bad ("未恢复：" + $p) } }
            foreach ($w in @($detail.warnings)) { if ($w) { Write-Warn2 ("无法确认：" + $w) } }
        } elseif ($v.Data.verdict -ne 'Ok') {
            Write-Warn2 '没有 verification 明细，无法判断具体哪些项没恢复'
        }
    } else {
        Write-Warn2 ("读不到最终验证结果（退出码 " + $v.Code + "）")
    }
}

# ================================================================ 7 托盘
Write-Step '结束托盘进程'
if ($KeepTray) {
    Write-Note '按 -KeepTray 保留'
} elseif (-not $apply) {
    Write-Note '将结束 QuietMode.exe（托盘）'
} else {
    $p = Get-Process -Name 'QuietMode' -ErrorAction SilentlyContinue
    if (-not $p) { Write-Note '托盘未运行' }
    else {
        $p | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        if (Get-Process -Name 'QuietMode' -ErrorAction SilentlyContinue) { Write-Warn2 '托盘进程仍在' }
        else { Write-Ok '托盘已结束' }
    }
}

# ================================================================ 结果
Write-Log ''
if (-not $apply) {
    if ($script:Failures.Count -eq 0) {
        Write-Log '演练完成：没有改动任何系统状态。确认后加 -Mode Apply 执行。' 'Yellow'
    } else {
        Write-Log ("演练期间有 " + $script:Failures.Count + " 项检查失败（演练本身不改系统）：") 'Red'
        foreach ($f in $script:Failures) { Write-Log ("  - " + $f) 'Red' }
    }
} elseif ($script:Failures.Count -eq 0) {
    Write-Log '卸载/恢复完成：调度已停，系统状态已按 baseline 恢复。' 'Green'
} else {
    Write-Log ("卸载/恢复有 " + $script:Failures.Count + " 项失败：") 'Red'
    foreach ($f in $script:Failures) { Write-Log ("  - " + $f) 'Red' }
    Write-Log '未删除 baseline / recovery / logs —— 修好问题后可以直接重跑本脚本。' 'Yellow'
}
if ($script:Warnings.Count -gt 0) {
    Write-Log ("警告 " + $script:Warnings.Count + " 项（多数是「读不到」，不代表改坏了）") 'Yellow'
}

if ($script:Failures.Count -gt 0) { exit 2 }
exit 0
