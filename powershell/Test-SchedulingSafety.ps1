#Requires -Version 5.1
<#
    Test-SchedulingSafety.ps1 — Phase 4 统一调度的只读集成测试。

    验证的是「只有一个权威控制器」这件事：
      1. 任务 XML 里只有一个任务、5 个触发器、WakeToRun=false、SYSTEM、忽略并发。
      2. 决策与计划是纯计算：跑一遍不改变 state.json，也不改变磁盘与风扇。
      3. 未提权时 --apply 必须失败，且不能报告成功。
      4. Worker 在未提权时会拒绝执行夜间动作（全部标 notrun），而不是做一半。
      5. Pause / Resume 能影响决策。

    绝不注册计划任务、绝不 Set-Disk、绝不改 FanControl 配置。
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ('qm-sched-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

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

Write-Host "统一调度只读集成测试" -ForegroundColor Cyan
Write-Host ("root = " + $Root)
Write-Host ''

if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
& $qmctl --root $Root config init --quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '准备配置失败' -ForegroundColor Red; exit 2 }
& $qmctl --root $Root snapshot create --quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host '采集快照失败' -ForegroundColor Red; exit 2 }

Check '任务 XML 只有一个任务且设置正确' {
    $xml = (& $qmctl --root $Root task print) -join "`n"
    if ($xml -notmatch '<Task version="1\.2"') { throw 'XML 结构不对' }
    if ($xml -notmatch 'QuietMode-Reconcile') { throw '缺少任务名' }
    if ($xml -notmatch '<WakeToRun>false</WakeToRun>') { throw 'WakeToRun 必须为 false' }
    if ($xml -notmatch '<UserId>S-1-5-18</UserId>') { throw '必须以 SYSTEM 运行' }
    if ($xml -notmatch '<RunLevel>HighestAvailable</RunLevel>') { throw '必须最高权限' }
    if ($xml -notmatch '<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>') { throw '必须忽略并发' }
    if ($xml -notmatch '<ExecutionTimeLimit>PT10M</ExecutionTimeLimit>') { throw '缺少执行上限' }
    $triggers = ([regex]::Matches($xml, '<(Calendar|Boot|Event|Logon)Trigger>')).Count
    if ($triggers -lt 5) { throw "触发器只有 $triggers 个，至少要有 5 个（23:58 / 10:02 / 开机 / 恢复 / 解锁）" }
    # 只允许一个 <Task> 根，避免又出现两套调度
    if (([regex]::Matches($xml, '<Task ')).Count -ne 1) { throw 'XML 里出现了多个 Task 根' }
}

Check '任务 XML 可被 XML 解析且动作指向 qmctl reconcile' {
    $xml = (& $qmctl --root $Root task print) -join "`n"
    $doc = New-Object System.Xml.XmlDocument
    try { $doc.LoadXml($xml) } catch { throw "XML 不合法：$($_.Exception.Message)" }
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $cmd = $doc.SelectSingleNode('//t:Actions/t:Exec/t:Command', $ns)
    $arg = $doc.SelectSingleNode('//t:Actions/t:Exec/t:Arguments', $ns)
    if (-not $cmd -or -not $arg) { throw '缺少 Exec 动作' }
    if ($cmd.InnerText -notmatch 'qmctl\.exe$') { throw "Command 不是 qmctl.exe：$($cmd.InnerText)" }
    if ($arg.InnerText -notmatch 'reconcile') { throw "Arguments 不含 reconcile：$($arg.InnerText)" }
    if ($arg.InnerText -notmatch '--apply') { throw 'Arguments 缺 --apply，任务跑了也不会生效' }
}

Check 'task status 能跑通并给出结论' {
    $r = Invoke-Qm @('--root', $Root, 'task', 'status')
    if ($r.Text -notmatch 'QuietMode-Reconcile') { throw '没有输出任务名' }
    if ($r.Text -notmatch '结论') { throw '没有给出结论' }
    Write-Host ("    " + (($r.Text -split "`n") | Select-Object -Last 1).Trim()) -ForegroundColor DarkGray
}

Check 'reconcile 演练不改变 state.json' {
    $stateFile = Join-Path $Root 'state.json'
    $before = if (Test-Path -LiteralPath $stateFile) { (Get-FileHash -LiteralPath $stateFile -Algorithm SHA256).Hash } else { '' }
    $r = Invoke-Qm @('--root', $Root, 'reconcile')
    if ($r.Code -ne 0) { throw "演练应返回 0，实际 $($r.Code)：$($r.Text)" }
    $after = if (Test-Path -LiteralPath $stateFile) { (Get-FileHash -LiteralPath $stateFile -Algorithm SHA256).Hash } else { '' }
    if ($before -ne $after) { throw '演练修改了 state.json' }
}

Check 'reconcile 演练不碰磁盘，也不承诺成功' {
    $before = Invoke-QmJson @('disks', 'list')
    $r = Invoke-Qm @('--root', $Root, 'reconcile', 'night')
    if ($r.Text -match 'OK|成功') {
        if ($r.Text -notmatch '演练|未改动') { throw '演练输出看起来像真的执行了' }
    }
    $after = Invoke-QmJson @('disks', 'list')
    $b = ($before.disks | ForEach-Object { "$($_.number)=$($_.isOffline)" }) -join ';'
    $a = ($after.disks | ForEach-Object { "$($_.number)=$($_.isOffline)" }) -join ';'
    if ($b -ne $a) { throw '演练改变了磁盘状态' }
}

Check '未提权时 reconcile --apply 必须失败' {
    $r = Invoke-Qm @('--root', $Root, 'reconcile', 'night', '--apply')
    if ($r.Code -eq 0) { throw '未提权却返回成功' }
    if ($r.Text -notmatch 'ELEVATION|管理员|未执行|失败') { throw "没有给出失败原因：$($r.Text)" }
}

Check 'Worker 未提权时全部步骤标 notrun' {
    $script = Join-Path $repo 'powershell\Worker\Invoke-QuietModeWorker.ps1'
    if (-not (Test-Path -LiteralPath $script)) { throw "找不到 Worker：$script" }
    $req = Join-Path $env:TEMP ('qm-wreq-' + [Guid]::NewGuid().ToString('N') + '.json')
    $res = Join-Path $env:TEMP ('qm-wres-' + [Guid]::NewGuid().ToString('N') + '.json')
    $payload = @{
        action = 'night'; trigger = 'test'; root = $Root
        steps = @(
            @{ order = 1; module = 'maintenance'; action = 'night'; detail = 'x' },
            @{ order = 2; module = 'disk'; action = 'offline'; detail = 'y' }
        )
    } | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($req, $payload, (New-Object System.Text.UTF8Encoding($false)))
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script `
        -RequestFile $req -ResultFile $res -Root $Root | Out-Null
    $code = $LASTEXITCODE
    if ($code -eq 0) { throw '未提权时 Worker 不应返回 0' }
    if (-not (Test-Path -LiteralPath $res)) { throw '没有生成结果文件' }
    $out = [System.IO.File]::ReadAllText($res, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    Remove-Item -LiteralPath $req, $res -Force -ErrorAction SilentlyContinue
    if ($out.errorCode -notin @('ELEVATION_REQUIRED', 'SCRIPT_ERROR')) { throw "错误码异常：$($out.errorCode)" }
    foreach ($s in @($out.steps)) {
        if ($s.status -ne 'notrun') { throw "步骤 $($s.module) 状态应为 notrun，实际 $($s.status)" }
    }
}

Check 'Worker 缺请求文件时拒绝执行' {
    $script = Join-Path $repo 'powershell\Worker\Invoke-QuietModeWorker.ps1'
    $res = Join-Path $env:TEMP ('qm-wres-' + [Guid]::NewGuid().ToString('N') + '.json')
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script `
        -RequestFile (Join-Path $env:TEMP 'no-such-request.json') -ResultFile $res -Root $Root | Out-Null
    if ($LASTEXITCODE -eq 0) { throw '缺请求文件时不应返回 0' }
    $out = [System.IO.File]::ReadAllText($res, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    Remove-Item -LiteralPath $res -Force -ErrorAction SilentlyContinue
    if ($out.errorCode -ne 'NO_REQUEST') { throw "错误码应为 NO_REQUEST，实际 $($out.errorCode)" }
}

Check 'Pause 让决策变成 SchedulePaused' {
    & $qmctl --root $Root state pause --quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'state pause 失败' }
    $d = Invoke-QmJson @('--root', $Root, 'reconcile')
    if ($d.decision.state -ne 'SchedulePaused') { throw "决策应为 SchedulePaused，实际 $($d.decision.state)" }
    & $qmctl --root $Root state resume --quiet | Out-Null
    $d2 = Invoke-QmJson @('--root', $Root, 'reconcile')
    if ($d2.decision.state -eq 'SchedulePaused') { throw 'Resume 后仍是暂停状态' }
}

Check '手动静音优先于时间调度' {
    & $qmctl --root $Root state manual-quiet --quiet | Out-Null
    $d = Invoke-QmJson @('--root', $Root, 'reconcile')
    if ($d.decision.state -ne 'ManualQuiet') { throw "决策应为 ManualQuiet，实际 $($d.decision.state)" }
    if ($d.decision.action -ne 'night') { throw "动作应为 night，实际 $($d.decision.action)" }
    & $qmctl --root $Root state auto --quiet | Out-Null
}

Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("SCHEDULING SAFETY: " + $script:pass + " pass / " + $script:fail + " fail")
if ($script:fail -gt 0) { exit 1 }
Write-Host 'ALL PASS' -ForegroundColor Green
exit 0
