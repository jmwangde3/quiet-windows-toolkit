<#
.SYNOPSIS
  只修正 C:\ProgramData\QuietHDD\diskgate.conf 的 Disks= 一行。

.DESCRIPTION
  不动计划任务、不动电源设置、不脱机任何磁盘。适用于主脚本已装好、
  但 conf 里磁盘号写少了的场景（提权时 -File 数组参数被错误绑定）。

  默认 Disks=0,1 -> J: / D: / E: 三卷夜间全部脱机。
  自带安全锁（系统盘/启动盘/含页面文件一律拒绝）与夜间干跑预演，
  预演不会真的调用 Set-Disk。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-setdisks.ps1"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-setdisks.ps1" -Disks '0,1'

.EXAMPLE
  # D: 上仍有常驻进程（wb2api.exe）时，强制让三卷全部脱机
  powershell -ExecutionPolicy Bypass -File ".\quiet-hdd-setdisks.ps1" -Disks '0,1' -Force
#>
[CmdletBinding()]
param(
    [string]$Disks = '0,1',
    [switch]$Force,
    [switch]$NoElevate,
    [switch]$FromElevated
)

$ErrorActionPreference = 'Continue'
function W-Step($m) { Write-Host ('[*] ' + $m) -ForegroundColor Cyan }
function W-OK($m)   { Write-Host ('[OK] ' + $m) -ForegroundColor Green }
function W-Warn($m) { Write-Host ('[!!] ' + $m) -ForegroundColor Yellow }
function W-Err($m)  { Write-Host ('[XX] ' + $m) -ForegroundColor Red }

$ConfPath   = 'C:\ProgramData\QuietHDD\diskgate.conf'
$GateScript = 'C:\ProgramData\QuietHDD\diskgate.ps1'
$ArgsFile   = Join-Path $env:TEMP 'quiet-hdd-setdisks.args'

$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $NoElevate -and -not $FromElevated) {
    W-Step '需要管理员权限（conf 由 SYSTEM/Administrators 持有），正在请求 UAC...'
    $tmp = Join-Path $env:TEMP 'quiet-hdd-setdisks.ps1'
    # 参数走文件，避开 -File 参数绑定差异
    $payload = @(('Disks=' + $Disks), ('Force=' + $(if ($Force) { '1' } else { '0' })))
    [System.IO.File]::WriteAllLines($ArgsFile, $payload, (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
    $al = '-NoProfile -ExecutionPolicy Bypass -NoExit -File "' + $tmp + '" -FromElevated'
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $al -Verb RunAs -ErrorAction Stop
        W-OK '已在管理员窗口执行，请看那个窗口。'
    } catch {
        W-Warn ('提权失败: ' + $_.Exception.Message)
        W-Warn '请手动以管理员身份打开 PowerShell，运行本脚本并加 -NoElevate'
    }
    return
}

if ($FromElevated -and (Test-Path -LiteralPath $ArgsFile)) {
    foreach ($l in (Get-Content -LiteralPath $ArgsFile -Encoding UTF8)) {
        if     ($l -match '^Disks=(.+)$') { $Disks = $Matches[1].Trim() }
        elseif ($l -match '^Force=(\d)$') { $Force = ($Matches[1] -eq '1') }
    }
}

# ---------------- 校验 ----------------
W-Step '=== 1/4 参数与安全锁 ==='
# -Disks 0,1 会被 PowerShell 绑成 "0 1"，统一归一成逗号分隔
$Disks = ($Disks -replace '\s+', ',').Trim(',')
if ($Disks -notmatch '^\d+(,\d+)*$') { W-Err ('Disks 格式错误: ' + $Disks); return }
$nums = @($Disks.Split(',') | ForEach-Object { [int]$_.Trim() })
$pf = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Substring(0,1).ToUpper() })
$volAll = @()
foreach ($n in $nums) {
    try {
        $d = Get-Disk -Number $n -ErrorAction Stop
        $ls = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })
        if ($d.IsBoot -or $d.IsSystem) { W-Err ('Disk ' + $n + ' 是系统/启动盘，拒绝写入'); return }
        if (@($ls | Where-Object { $pf -contains $_ }).Count -gt 0) { W-Err ('Disk ' + $n + ' 含页面文件，拒绝写入'); return }
        $vols = @($ls | ForEach-Object { $_ + ':' })
        $volAll += $vols
        W-OK ('Disk ' + $n + '  ' + $d.FriendlyName.Trim() + '  卷=' + ($vols -join '/'))
    } catch {
        W-Err ('Disk ' + $n + ' 读取失败: ' + $_.Exception.Message); return
    }
}

# ---------------- 写 conf ----------------
W-Step '=== 2/4 写 diskgate.conf ==='
$forceVal = $(if ($Force) { '1' } else { '0' })
$lines = @(('Disks=' + $Disks), ('Force=' + $forceVal))
if ($Force) { W-Warn 'Force=1：即使盘上有进程本体也会强制脱机，该进程夜间 I/O 会失败' }
try {
    [System.IO.File]::WriteAllLines($ConfPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
} catch {
    W-Err ('写入失败: ' + $_.Exception.Message); return
}
W-OK ('已写入 ' + $ConfPath + ' -> ' + ($lines -join ' ; '))
$back = [string]((Get-Content -LiteralPath $ConfPath -Encoding UTF8 | Where-Object { $_ -match '^Disks=' } | Select-Object -First 1) -replace '^Disks=','')
if ($back -ne $Disks) { W-Err ('回读校验失败: 读到 ' + $back + '，期望 ' + $Disks); return }
W-OK ('回读校验通过: Disks=' + $back + '  -> 今晚将脱机 ' + ($volAll -join ' / '))

# ---------------- 闸门占用检测（真实正则） ----------------
W-Step '=== 3/4 闸门占用检测（不脱机） ==='
foreach ($n in $nums) {
    $ls = @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })
    $esc = ($ls | ForEach-Object { [regex]::Escape($_) }) -join ''
    $cls = '^"?' + '[' + $esc + ']:\\'
    $busy = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -match $cls -or $_.CommandLine -match $cls } | Select-Object -ExpandProperty ProcessId -Unique)
    if ($busy.Count -gt 0) {
        if ($Force) { W-Warn ('Disk ' + $n + ' 有进程本体占用 pids=' + ($busy -join '+') + ' -> Force=1，仍会强制脱机（该进程夜间 I/O 失败）') }
        else { W-Warn ('Disk ' + $n + ' 有进程本体占用 pids=' + ($busy -join '+') + ' -> 闸门会 SKIP_BUSY 跳过；要强制请加 -Force') }
    }
    else { W-OK ('Disk ' + $n + ' 无占用 -> 闸门会脱机') }
}

# ---------------- 夜间干跑预演 ----------------
W-Step '=== 4/4 夜间干跑预演（不调用 Set-Disk） ==='
if (Test-Path -LiteralPath $GateScript) {
    $body = Get-Content -LiteralPath $GateScript -Raw -Encoding UTF8
    $dryLog = Join-Path $env:TEMP 'setdisks-dryrun.log'
    $body = $body.Replace("`$log  = Join-Path `$dir 'diskgate.log'", "`$log = '" + $dryLog + "'")
    $body = $body.Replace('Set-Disk -Number $n -IsOffline $true -ErrorAction Stop', "Write-Output ('WOULD_OFFLINE_Disk' + `$n)")
    $body = $body.Replace('Set-Disk -Number $n -IsOffline $false -ErrorAction Stop', "Write-Output ('WOULD_ONLINE_Disk' + `$n)")
    $body = $body.Replace('Add-Content -LiteralPath $log -Value $line -Encoding UTF8', "Add-Content -LiteralPath `$log -Value `$line -Encoding UTF8`nWrite-Output `$line")
    $dry = Join-Path $env:TEMP 'setdisks-dryrun.ps1'
    [System.IO.File]::WriteAllText($dry, $body, (New-Object System.Text.UTF8Encoding($false)))
    & $dry -ForceNight 2>&1 | Out-String -Width 200
    Remove-Item -LiteralPath $dry -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $dryLog -Force -ErrorAction SilentlyContinue
    W-OK '预演输出里应看到每个目标盘各一行 OFF'
} else {
    W-Warn ('闸门脚本不存在: ' + $GateScript + '，请先跑 quiet-hdd-offline.ps1')
}

Write-Host ''
W-OK ('完成。今晚 23:58 将脱机：' + ($volAll -join ' / ') + '；10:02 自动联机。')
Write-Host '  可用 quiet-hdd-status.ps1 或 quiet-hdd-offline.ps1 -Status 复核。' -ForegroundColor Gray
