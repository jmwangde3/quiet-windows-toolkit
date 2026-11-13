#Requires -Version 5.1
<#
    Test-Privilege.ps1 — 特权目录的权限体检 + 攻击检测能力自测（RC2-04）。

    两段，用途不同：

      A. 实机体检（只读，仅供参考）
         报告 C:\ProgramData\QuietMode 下哪些路径普通用户可写、哪些是重解析点。
         这一段的结论因机器而异，不作为退出码依据 —— 没加固的机器本来就是可写的。

      B. 检测能力自测（必须全部通过）
         在一套临时目录里模拟三种攻击，验证我们的检测真的会报警：
           1. junction 指向别处            → Test-QmReparsePoint 必须为真
           2. 名字里带 ..\ 想逃出基目录     → Resolve-QmSafeChild 必须返回 $null
           3. 演练模式不得真的改权限        → ACE 数量必须不变
         全部在 %TEMP% 下进行，结束即删，不碰真实配置根。
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\ProgramData\QuietMode',
    [string]$DeployDir
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# 调用方可能传空串（例如上层脚本还没解析出 Root）：回落到默认值，
# 否则 Join-Path 会因为空路径直接抛异常，把「体检」变成「崩溃」。
if (-not $Root) { $Root = 'C:\ProgramData\QuietMode' }
if (-not $DeployDir) { $DeployDir = $null }

$script:Checks = 0
$script:Failed = 0

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $script:Checks++
    if ($Ok) {
        Write-Host ("  PASS  " + $Name) -ForegroundColor Green
    } else {
        $script:Failed++
        $suffix = ''
        if ($Detail) { $suffix = '  → ' + $Detail }
        Write-Host ("  FAIL  " + $Name + $suffix) -ForegroundColor Red
    }
}

$module = Join-Path $PSScriptRoot 'Modules\QuietMode.Security.psm1'
if (-not (Test-Path -LiteralPath $module)) {
    Write-Host ("找不到安全模块：" + $module) -ForegroundColor Red
    exit 3
}
Import-Module $module -Force

Write-Host ''
Write-Host '特权目录体检（只读）' -ForegroundColor White
Write-Host ("  root   " + $Root)
if ($DeployDir) { Write-Host ("  deploy " + $DeployDir) }

$layout = Test-QmPrivilegedLayout -Root $Root -DeployDir $DeployDir
Write-Host ("  检查 " + $layout.checked + " 条路径")
if ($layout.ok) {
    Write-Host '  结论：没有普通用户可写的 SYSTEM 输入' -ForegroundColor Green
} else {
    Write-Host '  结论：存在可被普通用户写入的 SYSTEM 输入（用 Install/Repair 的 -Apply 加固）' -ForegroundColor Yellow
    foreach ($p in $layout.problems) { Write-Host ("    - " + $p) -ForegroundColor Yellow }
}

foreach ($p in (Get-QmPrivilegedPaths -Root $Root -DeployDir $DeployDir)) {
    if (-not (Test-Path -LiteralPath $p)) { continue }
    if (Test-QmReparsePoint -Path $p) {
        Write-Host ("  重解析点：" + $p + "（SYSTEM 的读写可能被重定向）") -ForegroundColor Yellow
    }
}
if (Test-QmReparsePoint -Path $Root) {
    Write-Host ("  重解析点：" + $Root) -ForegroundColor Yellow
}

Write-Host ''
Write-Host '检测能力自测（临时目录，结束即删）' -ForegroundColor White

$sandbox = Join-Path $env:TEMP ('qm-privtest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$base = Join-Path $sandbox 'root'
$target = Join-Path $sandbox 'somewhere-else'
try {
    New-Item -ItemType Directory -Path $base -Force | Out-Null
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $target 'secret.txt') -Value 'x' -Encoding UTF8

    # ---- 1. junction 必须被识别 ----
    $link = Join-Path $base 'run'
    $made = $false
    try {
        New-Item -ItemType Junction -Path $link -Target $target -ErrorAction Stop | Out-Null
        $made = $true
    } catch {
        Write-Host ("  SKIP  junction 创建失败（" + $_.Exception.Message + "），跳过该项") -ForegroundColor DarkGray
    }
    if ($made) {
        Check 'junction 被识别为重解析点' (Test-QmReparsePoint -Path $link)
        Check '普通目录不被误判为重解析点' (-not (Test-QmReparsePoint -Path $target))
        $r = Resolve-QmSafeChild -Base $link -Name 'reconcile-request-x.json'
        Check '名字合法时能拼出路径（拼得出不等于可以写，写入前还要查重解析点）' ($null -ne $r)
    }

    # ---- 2. 路径穿越必须被拒 ----
    Check '拒绝 ..\ 逃逸' ($null -eq (Resolve-QmSafeChild -Base $base -Name '..\evil.json'))
    Check '拒绝下级目录逃逸' ($null -eq (Resolve-QmSafeChild -Base $base -Name 'sub\evil.json'))
    Check '拒绝绝对路径' ($null -eq (Resolve-QmSafeChild -Base $base -Name 'C:\Windows\evil.json'))
    Check '拒绝形如盘符的名字' ($null -eq (Resolve-QmSafeChild -Base $base -Name 'C:evil.json'))
    Check '拒绝 . 与 ..' (($null -eq (Resolve-QmSafeChild -Base $base -Name '.')) -and ($null -eq (Resolve-QmSafeChild -Base $base -Name '..')))
    Check '接受普通文件名' ($null -ne (Resolve-QmSafeChild -Base $base -Name 'reconcile-result-1.json'))

    # ---- 3. 演练模式不得改权限 ----
    $before = @(Get-Acl -LiteralPath $base).Access.Count
    $plan = Protect-QuietModeRoot -Root $base -DeployDir $null
    $after = @(Get-Acl -LiteralPath $base).Access.Count
    Check 'Protect-QuietModeRoot 默认只给计划不动手' ($plan.apply -eq $false -and $before -eq $after)

    # ---- 4. 加固必须真的削权，且「只读」不能被误判成「可写」 ----
    # 早先的实现拿 FullControl 这个超集掩码做 -band，ReadAndExecute 也会命中，
    # 于是六条全是只读的路径被报成「普通用户可写」。这一段把它钉死。
    $vroot = Join-Path $sandbox 'vroot'
    New-Item -ItemType Directory -Path $vroot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $vroot 'logs') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $vroot 'config.json') -Value '{}' -Encoding UTF8

    $sidAdm = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $sidSys = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $sidUsr = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $vacl = Get-Acl -LiteralPath $vroot
    $vacl.SetAccessRuleProtection($true, $false)
    foreach ($a in @($vacl.Access)) { [void]$vacl.RemoveAccessRule($a) }
    $vacl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidAdm, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))) | Out-Null
    $vacl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidSys, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))) | Out-Null
    $vacl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidUsr, 'ReadAndExecute, Write', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))) | Out-Null
    Set-QmAcl -Path $vroot -Acl $vacl

    Check '加固前：Users 可写被识别' (Test-QmUserWritable -Path $vroot)
    (Protect-QuietModeRoot -Root $vroot -Apply) | Out-Null
    Check '加固后：Users 不再可写' (-not (Test-QmUserWritable -Path $vroot))
    $usersRead = $false
    foreach ($ace in (Get-Acl -LiteralPath $vroot).Access) {
        $sid = ''
        try { $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { }
        if ($sid -eq 'S-1-5-32-545' -and [string]$ace.AccessControlType -eq 'Allow' -and
            ($ace.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Read) -ne 0) { $usersRead = $true }
    }
    Check '加固后：Users 仍保留读取（托盘/非提权 qmctl 要用）' $usersRead
    Check '加固后：没有残留 Deny（Deny 会挡住提权管理员）' (-not (Test-QmUserDeny -Path $vroot))
    Check '加固后：子项随父目录一起变只读' (-not (Test-QmUserWritable -Path (Join-Path $vroot 'config.json')))

    # 只剩 ReadAndExecute 的路径不能被判成可写（回归：超集掩码误判）
    $plain = Join-Path $sandbox 'plain'
    New-Item -ItemType Directory -Path $plain -Force | Out-Null
    $pacl = Get-Acl -LiteralPath $plain
    $pacl.SetAccessRuleProtection($true, $false)
    foreach ($a in @($pacl.Access)) { [void]$pacl.RemoveAccessRule($a) }
    $pacl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sidUsr, 'ReadAndExecute', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))) | Out-Null
    Set-QmAcl -Path $plain -Acl $pacl
    Check '只有 ReadAndExecute 时判为只读（不误报）' (-not (Test-QmUserWritable -Path $plain))
} finally {
    # 自测里会故意把临时目录加固成只读，删之前先把继承与 ACE 还原
    try { if (Test-Path -LiteralPath $sandbox) { & icacls $sandbox /reset /T /C | Out-Null } } catch { }
    try { if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
}

Write-Host ''
if ($script:Failed -gt 0) {
    Write-Host ("检测自测：" + $script:Failed + " / " + $script:Checks + " 项失败") -ForegroundColor Red
    exit 2
}
Write-Host ("检测自测：" + $script:Checks + " 项全部通过") -ForegroundColor Green
exit 0
