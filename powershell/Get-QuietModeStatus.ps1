<#
.SYNOPSIS
    只读状态汇报：路径、配置校验、调度参数、目标磁盘、baseline 完整性。
.DESCRIPTION
    不做任何修改。需要管理员才能读全的项会明确标成「需提权」，不猜。
.EXAMPLE
    .\Get-QuietModeStatus.ps1
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

Import-Module (Join-Path $PSScriptRoot 'Modules\QuietMode.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Modules\QuietMode.Config.psm1') -Force

$paths = Get-QuietModePaths -Root $Root

$cfgMark = if (Test-Path -LiteralPath $paths.config) { '' } else { '(缺失)' }
$stateMark = if (Test-Path -LiteralPath $paths.state) { '' } else { '(缺失)' }
$baseMark = if (Test-Path -LiteralPath $paths.baseline) { '' } else { '(未建立)' }

Write-Host "== QuietMode v2 状态 ==" -ForegroundColor Cyan
Write-Host ("root      : {0}" -f $paths.root)
Write-Host ("config    : {0} {1}" -f $paths.config, $cfgMark)
Write-Host ("state     : {0} {1}" -f $paths.state, $stateMark)
Write-Host ("baseline  : {0} {1}" -f $paths.baseline, $baseMark)
Write-Host ("elevated  : {0}" -f (Test-QuietModeElevated))
Write-Host ''

if (-not (Test-Path -LiteralPath $paths.config)) {
    Write-Host '未找到 config.json：先执行  qmctl config migrate  或  qmctl config init' -ForegroundColor Yellow
    return
}

$check = Test-QuietModeConfig -Root $Root
if ($check.Valid) {
    Write-Host '配置校验 : VALID' -ForegroundColor Green
}
else {
    Write-Host '配置校验 : INVALID' -ForegroundColor Red
}
foreach ($e in $check.Errors) { Write-Host ("  ERROR  " + $e) -ForegroundColor Red }
foreach ($w in $check.Warnings) { Write-Host ("  WARN   " + $w) -ForegroundColor Yellow }
Write-Host ''

$cfg = Get-QuietModeConfig -Root $Root
Write-Host '== 调度 =='
Write-Host ("enabled={0}  mode={1}  night={2}  day={3}" -f $cfg.schedule.enabled, $cfg.schedule.mode, $cfg.schedule.nightStart, $cfg.schedule.dayStart)
Write-Host ("startupOverride={0}  startupDayStart={1}  grace={2}min  reconcile={3}min" -f `
        $cfg.schedule.startupOverrideEnabled, $cfg.schedule.startupDayStart, $cfg.schedule.startupGraceMinutes, $cfg.schedule.reconcileIntervalMinutes)
Write-Host ''

Write-Host '== 风扇 =='
Write-Host ("enabled={0}  autoSwitch={1} ({2} → {3})" -f $cfg.fan.enabled, $cfg.fan.autoSwitch.enabled, $cfg.fan.autoSwitch.on, $cfg.fan.autoSwitch.off)
if ($cfg.fan.enabled) {
    Write-Host ("normal : {0}" -f $cfg.fan.normalProfile)
    Write-Host ("silent : {0}" -f $cfg.fan.silentProfile)
}
Write-Host ''

Write-Host '== 磁盘（按稳定身份） =='
if (-not $cfg.disks.enabled) {
    Write-Host 'disks.enabled=false（不脱机任何磁盘）'
}
else {
    $targets = Get-QuietModeDiskTargets -Root $Root
    if (-not $targets -or $targets.Count -eq 0) {
        Write-Host '没有启用的磁盘目标' -ForegroundColor Yellow
    }
    foreach ($t in $targets) {
        $gb = if ($t.sizeBytes -gt 0) { '{0:N0} GB' -f ($t.sizeBytes / 1GB) } else { '?' }
        Write-Host ("  {0,-28} {1,10}  {2,-6} uid={3}" -f $t.friendlyName, $gb, $t.busType, $t.uniqueId)
    }
}
Write-Host ("forceBusy={0}" -f $cfg.disks.forceBusy)
Write-Host ''

Write-Host '== 维护 / 停转 =='
Write-Host ("maintenance.enabled={0}  strictNight={1}  disableSysMain={2}  hour={3}  disableWake={4}" -f `
        $cfg.maintenance.enabled, $cfg.maintenance.strictNightDisable, $cfg.maintenance.disableSysMainAtNight,
        $cfg.maintenance.maintenanceHour, $cfg.maintenance.disableWake)
Write-Host ("spinDown.enabled={0}  minutes={1}" -f $cfg.spinDown.enabled, $cfg.spinDown.minutes)
Write-Host ''

$state = Get-QuietModeState -Root $Root
if ($state) {
    Write-Host '== 运行状态 =='
    Write-Host ("modeOn={0}  schedulePaused={1}  autoSilentActive={2}  suppressed={3}  autoOffDay={4}" -f `
            $state.modeOn, $state.schedulePaused, $state.autoSilentActive, $state.autoSilentSuppressed, $state.autoOffDayStamp)
    if ($state.recovery -and $state.recovery.active) {
        Write-Host ("恢复事务进行中：{0}（尝试 {1} 次）" -f $state.recovery.reason, $state.recovery.attempts) -ForegroundColor Yellow
        foreach ($s in $state.recovery.steps) {
            Write-Host ("  {0,-18} {1,-8} {2}" -f $s.name, $s.status, $s.detail)
        }
    }
    Write-Host ''
}

if (Test-Path -LiteralPath $paths.baseline) {
    $verify = Test-QuietModeBaseline -Root $Root
    Write-Host ('== baseline ==')
    Write-Host ("valid={0}  complete={1}" -f $verify.Valid, $verify.Complete)
    foreach ($e in $verify.Errors) { Write-Host ("  ERROR  " + $e) -ForegroundColor Red }
    foreach ($w in $verify.Warnings) { Write-Host ("  WARN   " + $w) -ForegroundColor Yellow }
}
