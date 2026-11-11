#Requires -Version 5.1
<#
.SYNOPSIS
    磁盘安全闸门的集成测试（只读，不需要管理员，不改动任何磁盘状态）。

.DESCRIPTION
    用真实磁盘清单构造一个临时配置根，然后验证：
      1. 所有真实磁盘都能被唯一身份解析（不允许 ambiguous / not-found）；
      2. 系统盘 / 启动盘即使带 --force --apply 也被拒绝，且错误码是 SAFETY_BLOCKED；
      3. 数据盘在演练模式（不带 --apply）下不会被安全闸门误拦；
      4. 未提权时 --apply 返回 ELEVATION_REQUIRED 而不是假装成功。

    退出码 0 = 全部符合预期。
.EXAMPLE
    .\Test-DiskSafety.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
$qmctl = Join-Path $repoRoot ("src\QuietMode.Cli\bin\{0}\net481\qmctl.exe" -f $Configuration)
if (-not (Test-Path -LiteralPath $qmctl)) {
    Write-Host "找不到 qmctl：$qmctl（请先编译）" -ForegroundColor Red
    exit 1
}

$failures = New-Object System.Collections.Generic.List[string]
$tmp = Join-Path $env:TEMP ("qm-disk-safety-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

function Check {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ("--- {0}" -f $Name) -ForegroundColor Cyan
    try {
        & $Body
        Write-Host "    PASS" -ForegroundColor Green
    } catch {
        $failures.Add("$Name — $($_.Exception.Message)")
        Write-Host ("    FAIL  {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

function Invoke-Qm {
    param([string[]]$Arguments)
    $out = & $qmctl @Arguments 2>&1 | Out-String
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $out }
}

try {
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    # 1) 用默认策略起一个临时根，再把真实磁盘写成目标
    $init = Invoke-Qm @('--root', $tmp, 'config', 'init', '--quiet')
    if ($init.Exit -ne 0) { throw "config init 失败：$($init.Text)" }

    $listJson = (& $qmctl disks list --json | Out-String)
    $disks = @(($listJson | ConvertFrom-Json).disks)
    if ($disks.Count -eq 0) { throw '磁盘清单为空，无法继续' }

    $targets = @()
    foreach ($d in $disks) {
        $targets += [ordered]@{
            uniqueId = [string]$d.uniqueId; serialNumber = [string]$d.serialNumber
            friendlyName = [string]$d.friendlyName; sizeBytes = [long]$d.sizeBytes
            busType = [string]$d.busType; enabled = $true
        }
    }
    $cfgPath = Join-Path $tmp 'config.json'
    $cfg = [System.IO.File]::ReadAllText($cfgPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $cfg.disks.enabled = $true
    $cfg.disks.targets = $targets
    [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 12),
        (New-Object System.Text.UTF8Encoding($false)))

    # 2) 唯一身份解析
    Check '全部真实磁盘可唯一解析' {
        $r = Invoke-Qm @('--root', $tmp, 'disks', 'resolve', '--quiet')
        if ($r.Exit -ne 0) { throw "disks resolve 退出码 $($r.Exit)" }
    }

    # 3) 系统盘必须被拒绝（含 force + apply）
    $sysIndex = -1
    for ($i = 0; $i -lt $disks.Count; $i++) {
        if ([bool]$disks[$i].isSystem -or [bool]$disks[$i].isBoot) { $sysIndex = $i; break }
    }
    if ($sysIndex -lt 0) {
        Write-Host '    SKIP  本机没有可识别的系统盘标志' -ForegroundColor DarkGray
    } else {
        Check '系统盘：inspect 判定为禁止脱机' {
            $r = Invoke-Qm @('--root', $tmp, 'disks', 'inspect', "$sysIndex", '--force')
            if ($r.Exit -ne 2) { throw "期望退出码 2，实际 $($r.Exit)" }
            if ($r.Text -notmatch '禁止脱机') { throw '输出中没有「禁止脱机」' }
        }
        Check '系统盘：offline --apply --force 返回 SAFETY_BLOCKED' {
            $r = Invoke-Qm @('--root', $tmp, 'disks', 'offline', "$sysIndex", '--apply', '--force')
            if ($r.Exit -ne 2) { throw "期望退出码 2，实际 $($r.Exit)" }
            if ($r.Text -notmatch 'SAFETY_BLOCKED') { throw '错误码不是 SAFETY_BLOCKED' }
        }
    }

    # 4) 数据盘演练不应被安全闸门误拦
    $dataIndex = -1
    for ($i = 0; $i -lt $disks.Count; $i++) {
        if (-not [bool]$disks[$i].isSystem -and -not [bool]$disks[$i].isBoot) { $dataIndex = $i; break }
    }
    if ($dataIndex -lt 0) {
        Write-Host '    SKIP  没有数据盘可测' -ForegroundColor DarkGray
    } else {
        Check '数据盘：演练模式不被安全闸门误拦' {
            $r = Invoke-Qm @('--root', $tmp, 'disks', 'offline', "$dataIndex")
            if ($r.Text -match 'SAFETY_BLOCKED') { throw "演练被误拦：$($r.Text)" }
        }
    }

    # 5) 未提权时 apply 不能假装成功
    Check '未提权时 apply 不得报成功' {
        if ($dataIndex -lt 0) { return }
        $elevated = (& $qmctl --root $tmp disks verify $dataIndex --json | Out-String | ConvertFrom-Json).elevated
        if ($elevated) {
            Write-Host '    SKIP  当前进程已提权，跳过未提权路径' -ForegroundColor DarkGray
            return
        }
        # 选一个「需要真正改变状态」的方向，避免命中 ALREADY_IN_STATE
        $op = if ([bool]$disks[$dataIndex].isOffline) { 'online' } else { 'offline' }
        $r = Invoke-Qm @('--root', $tmp, 'disks', $op, "$dataIndex", '--apply')
        if ($r.Text -match ('OK ' + $op.ToUpperInvariant())) { throw "未提权却报告成功：$($r.Text)" }
        if ($r.Text -notmatch 'ELEVATION_REQUIRED') { throw "未返回 ELEVATION_REQUIRED：$($r.Text)" }
        if ($r.Exit -ne 2) { throw "期望退出码 2，实际 $($r.Exit)" }
    }
}
finally {
    try { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'DISK SAFETY: ALL PASS' -ForegroundColor Green
    exit 0
}
Write-Host ("DISK SAFETY FAILED {0} 项：" -f $failures.Count) -ForegroundColor Red
foreach ($f in $failures) { Write-Host ("  " + $f) -ForegroundColor Red }
exit 1
