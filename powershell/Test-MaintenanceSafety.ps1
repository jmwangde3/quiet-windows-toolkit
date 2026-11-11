#Requires -Version 5.1
<#
    Test-MaintenanceSafety.ps1 — Phase 3 维护管理的只读集成测试。

    只做三件事：
      1. 只读读取全部维护任务，确认「存在 / 不存在 / 读不到」三种状态被正确区分。
      2. 验证计划与验证器对当前系统的判断自洽（不写任何东西）。
      3. 确认在执行脚本缺请求文件、未提权时不会报成功。

    绝不调用 Disable-ScheduledTask / Set-Service / 写注册表。
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ('qm-maint-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
} catch { }

$repo = Split-Path -Parent $PSScriptRoot
$qmctl = Join-Path $repo 'src\QuietMode.Cli\bin\Release\net481\qmctl.exe'
if (-not (Test-Path -LiteralPath $qmctl)) {
    Write-Host "找不到 qmctl.exe：$qmctl（先运行 build\build.ps1）" -ForegroundColor Red
    exit 2
}

$script:pass = 0
$script:fail = 0
function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:pass++
        Write-Host ("PASS  " + $Name) -ForegroundColor Green
    } catch {
        $script:fail++
        Write-Host ("FAIL  " + $Name + " — " + $_.Exception.Message) -ForegroundColor Red
    }
}

function Invoke-Qm {
    param([string[]]$Arguments)
    $out = & $qmctl @Arguments 2>&1
    return [pscustomobject]@{ Text = ($out | Out-String); Code = $LASTEXITCODE }
}

function Invoke-QmJson {
    param([string[]]$Arguments)
    $tmp = Join-Path $env:TEMP ('qm-' + [Guid]::NewGuid().ToString('N') + '.json')
    $all = @($Arguments) + @('--json', '--out', $tmp, '--quiet')
    & $qmctl @all | Out-Null
    if (-not (Test-Path -LiteralPath $tmp)) { throw "qmctl 没有生成 JSON：$tmp" }
    $text = [System.IO.File]::ReadAllText($tmp, [System.Text.Encoding]::UTF8)
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    return ($text | ConvertFrom-Json)
}

Write-Host "维护管理只读集成测试" -ForegroundColor Cyan
Write-Host ("root = " + $Root)
Write-Host ''

# 准备一个独立 root：默认配置 + 快照（只读采集，不改系统）
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
& $qmctl --root $Root config init --quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '准备配置失败' -ForegroundColor Red; exit 2 }
& $qmctl --root $Root snapshot create --quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '采集快照失败' -ForegroundColor Red; exit 2 }

Check '任务清单能区分存在 / 不存在' {
    $data = Invoke-QmJson @('--root', $Root, 'maint', 'list')
    if (-not $data.tasks -or @($data.tasks).Count -eq 0) { throw '清单为空' }
    $present = @($data.tasks | Where-Object { $_.presence -eq 'Present' })
    if ($present.Count -eq 0) { throw '一块任务都没读到，环境异常' }
    $states = @($data.tasks | ForEach-Object { $_.presence } | Sort-Object -Unique)
    Write-Host ("    presence 取值：" + ($states -join ', ')) -ForegroundColor DarkGray
}

Check 'Regular Maintenance 不存在也不报错' {
    $data = Invoke-QmJson @('--root', $Root, 'maint', 'list')
    $rm = @($data.tasks | Where-Object { $_.fullName -match 'Regular Maintenance' })[0]
    if (-not $rm) { throw '清单里缺少 Regular Maintenance 条目' }
    if ($rm.presence -eq 'Unknown') { throw 'Regular Maintenance 状态读取失败，应为 Present 或 NotPresent' }
}

Check '夜间计划只读、不产生写操作' {
    $before = Invoke-QmJson @('--root', $Root, 'maint', 'list')
    $plan = Invoke-QmJson @('--root', $Root, 'maint', 'plan', 'night')
    if (-not $plan.phase -or $plan.phase -ne 'night') { throw 'plan 未返回 night 阶段' }
    $after = Invoke-QmJson @('--root', $Root, 'maint', 'list')
    $b = ($before.tasks | ForEach-Object { "$($_.fullName)=$($_.enabled)" }) -join ';'
    $a = ($after.tasks | ForEach-Object { "$($_.fullName)=$($_.enabled)" }) -join ';'
    if ($b -ne $a) { throw 'maint plan 改变了系统任务状态' }
}

Check '未提权时 --apply 必须失败且标未执行' {
    $r = Invoke-Qm @('--root', $Root, 'maint', 'night', '--apply')
    if ($LASTEXITCODE -eq 0) { throw '未提权却返回成功' }
    if ($r.Text -notmatch '未执行|ELEVATION|管理员权限') { throw "没有给出未执行原因：$($r.Text)" }
}

Check '夜间验证对当前状态给出明确结论' {
    $v = Invoke-QmJson @('--root', $Root, 'maint', 'verify', 'night')
    if (-not $v.verdict) { throw '没有返回值' }
    Write-Host ("    verdict = " + $v.verdict) -ForegroundColor DarkGray
}

Check '执行脚本缺少请求文件时拒绝执行' {
    $script = Join-Path $repo 'powershell\Maintenance\Invoke-MaintenanceOperation.ps1'
    if (-not (Test-Path -LiteralPath $script)) { throw "找不到执行脚本：$script" }
    $res = Join-Path $env:TEMP ('qm-res-' + [Guid]::NewGuid().ToString('N') + '.json')
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script `
        -RequestFile (Join-Path $env:TEMP 'no-such-request.json') -ResultFile $res -Root $Root | Out-Null
    if ($LASTEXITCODE -eq 0) { throw '缺请求文件时不应返回 0' }
    if (-not (Test-Path -LiteralPath $res)) { throw '没有生成结果文件' }
    $payload = [System.IO.File]::ReadAllText($res, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    Remove-Item -LiteralPath $res -Force -ErrorAction SilentlyContinue
    if ($payload.errorCode -ne 'NO_REQUEST') { throw "错误码应为 NO_REQUEST，实际 $($payload.errorCode)" }
}

Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("MAINTENANCE SAFETY: " + $script:pass + " pass / " + $script:fail + " fail")
if ($script:fail -gt 0) { exit 1 }
Write-Host 'ALL PASS' -ForegroundColor Green
exit 0
