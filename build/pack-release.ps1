<#
.SYNOPSIS
    打一个可复现的发布包：QuietMode-v<版本>-win-x64.zip + SHA256SUMS.txt。

.DESCRIPTION
    「可复现」在这里指三件具体的事：
      1. 内容清单固定 —— 只收发布需要的文件，测试、临时产物、个人配置一律不进包；
      2. 条目顺序固定 —— 按相对路径序数排序后写入，不依赖文件系统枚举顺序；
      3. 条目时间戳固定 —— 全部写成同一个常量，同一份输入打两次得到同样的字节。

    包里必须能同时满足两个消费方：
      · 安装器 Install-QuietMode.ps1 —— 它按「仓库根 / 二进制目录 / 脚本树」三段找东西，
        发布包是扁平布局，二进制与 powershell\ 同级；
      · qmctl 的 RepoLocator —— 从可执行文件目录上溯找含 powershell\Modules 的目录。
      两者都要求「qmctl.exe 与 powershell\Modules 在同一棵树的 10 层以内」，本脚本会断言。

    不收：tests\、bin\、obj\、.git\、*.pdb、本机个人配置。
    产物默认落在 dist\。

.PARAMETER OutputDir
    产物输出目录，默认 dist\。

.PARAMETER Build
    打包前先跑 build\build.ps1 与 QuietMode\build\build.cmd 重新编译。

.EXAMPLE
    .\build\pack-release.ps1 -Build
#>
[CmdletBinding()]
param(
    [string]$OutputDir = '',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist' }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step { param([string]$Text) Write-Host ''; Write-Host ('== ' + $Text) -ForegroundColor Cyan }
function Write-OkFile { param([string]$Text) Write-Host ('   OK  ' + $Text) -ForegroundColor Green }

# ---------------------------------------------------------------- 1 编译（可选）
if ($Build) {
    Write-Step '重新编译'
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 失败，退出码 $LASTEXITCODE" }
    & cmd.exe /c (Join-Path $repoRoot 'QuietMode\build\build.cmd')
    if ($LASTEXITCODE -ne 0) { throw "QuietMode\build\build.cmd 失败，退出码 $LASTEXITCODE" }
}

# ---------------------------------------------------------------- 2 定位二进制
Write-Step '定位二进制'

$cliBin = Join-Path $repoRoot 'src\QuietMode.Cli\bin\Release\net481'
$trayBin = Join-Path $repoRoot 'QuietMode\build\bin\Release'

$required = @(
    @{ Dir = $cliBin; Name = 'qmctl.exe' },
    @{ Dir = $cliBin; Name = 'QuietMode.Core.dll' },
    @{ Dir = $cliBin; Name = 'QuietMode.Windows.dll' },
    @{ Dir = $trayBin; Name = 'QuietMode.exe' }
)
$missing = @()
foreach ($r in $required) {
    $p = Join-Path $r.Dir $r.Name
    if (-not (Test-Path -LiteralPath $p)) { $missing += $p }
}
if ($missing.Count -gt 0) {
    Write-Host '缺少编译产物，先编译再打包：' -ForegroundColor Red
    foreach ($m in $missing) { Write-Host ('   ' + $m) -ForegroundColor Red }
    Write-Host '   .\build\build.ps1 ; .\QuietMode\build\build.cmd' -ForegroundColor Yellow
    exit 2
}

# ---------------------------------------------------------------- 3 读版本号
Write-Step '读版本号'

# 版本只有一个来源：QuietModeConfig.Version（编译进 qmctl）。这里从 qmctl 自己嘴里读，
# 不去 grep .cs 文件 —— 免得打包脚本和产物说的不是同一件事。
$qmctl = Join-Path $cliBin 'qmctl.exe'
$help = (& $qmctl --help) 2>&1
$first = @($help | Where-Object { $_ -ne $null } | Select-Object -First 1)
if ("$first" -match '^\s*qmctl\s+(\d+\.\d+\.\d+)') {
    $version = $Matches[1]
} else {
    throw "无法从 qmctl --help 解析版本号，首行是：$first"
}
$pkgName = "QuietMode-v$version-win-x64"
Write-OkFile "版本 $version → $pkgName"

# ---------------------------------------------------------------- 4 组包内清单
Write-Step '组装文件清单'

$stageRoot = Join-Path $OutputDir ('stage-' + $pkgName)
$pkgRoot = Join-Path $stageRoot $pkgName
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $pkgRoot -Force | Out-Null

$script:files = New-Object System.Collections.ArrayList

function Add-PkgFile {
    param([string]$Source, [string]$Relative)

    if (-not (Test-Path -LiteralPath $Source)) { return $false }
    $dest = Join-Path $pkgRoot ($Relative -replace '/', '\')
    $destDir = Split-Path -Parent $dest
    if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $dest -Force
    [void]$script:files.Add($Relative)
    return $true
}

# 二进制：扁平放在包根，和 powershell\ 同级
foreach ($n in @('qmctl.exe', 'qmctl.exe.config', 'QuietMode.Core.dll', 'QuietMode.Windows.dll')) {
    [void](Add-PkgFile -Source (Join-Path $cliBin $n) -Relative $n)
}
foreach ($n in @('QuietMode.exe', 'QuietMode.exe.config')) {
    [void](Add-PkgFile -Source (Join-Path $trayBin $n) -Relative $n)
}

# 脚本树：计划任务运行时真正要执行的东西，文件逐个拷贝（不用 Copy-Item -Recurse，
# 免得把编辑器临时文件、.log、__pycache__ 之类顺手带进包）
$psSrc = Join-Path $repoRoot 'powershell'
$psFiles = @(Get-ChildItem -LiteralPath $psSrc -Recurse -File |
    Where-Object { $_.Extension -in '.ps1', '.psm1', '.cmd' } |
    Sort-Object { $_.FullName })
foreach ($f in $psFiles) {
    $rel = 'powershell/' + ($f.FullName.Substring($psSrc.Length).TrimStart('\') -replace '\\', '/')
    [void](Add-PkgFile -Source $f.FullName -Relative $rel)
}

# 文档与默认策略
# 注意：CHANGELOG.md 不进包。它在 2.0.5 一节里记着这个 zip 的 SHA256，
# 而它自己又在 zip 里面 —— 一旦打进去，包内那份记录的必然是上一代的哈希，
# 每次重打都会变，永远自洽不了。发布说明里写哈希就够了，包里不需要它。
foreach ($n in @('README.md', 'SAFETY.md', 'LICENSE')) {
    [void](Add-PkgFile -Source (Join-Path $repoRoot $n) -Relative $n)
}
[void](Add-PkgFile -Source (Join-Path $repoRoot 'config\default-policy.json') -Relative 'config/default-policy.json')

$docDir = Join-Path $repoRoot 'docs'
if (Test-Path -LiteralPath $docDir) {
    foreach ($f in @(Get-ChildItem -LiteralPath $docDir -File -Filter '*.md' | Sort-Object Name)) {
        [void](Add-PkgFile -Source $f.FullName -Relative ('docs/' + $f.Name))
    }
}

# ---------------------------------------------------------------- 5 断言包的契约
Write-Step '校验包结构（安装器 + RepoLocator 的硬约定）'

$contract = @(
    'qmctl.exe',
    'QuietMode.Core.dll',
    'QuietMode.Windows.dll',
    'QuietMode.exe',
    'powershell/Modules/QuietMode.Common.psm1',
    'powershell/Modules/QuietMode.TaskState.psm1',
    'powershell/Worker/Invoke-QuietModeWorker.ps1',
    # 下面三条是 C# 控制器按常量算出来的路径，少一个夜间链就断
    'powershell/Worker/Invoke-QuietModeWorker.ps1',
    'powershell/Maintenance/Invoke-MaintenanceOperation.ps1',
    'powershell/Disks/Invoke-DiskOperation.ps1',
    'config/default-policy.json',
    'README.md',
    'SAFETY.md',
    'LICENSE'
)
$bad = @()
foreach ($c in ($contract | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath (Join-Path $pkgRoot ($c -replace '/', '\')))) { $bad += $c }
}
if ($bad.Count -gt 0) {
    Write-Host '包内缺少契约要求的文件：' -ForegroundColor Red
    foreach ($b in $bad) { Write-Host ('   ' + $b) -ForegroundColor Red }
    exit 3
}
Write-OkFile ('契约文件 ' + ($contract | Select-Object -Unique).Count + ' 项齐全')

# RepoLocator：从 qmctl.exe 所在目录上溯 10 层找 powershell\Modules。
# 包根自己就命中，所以只要包根有 powershell\Modules 就算通过。
if (-not (Test-Path -LiteralPath (Join-Path $pkgRoot 'powershell\Modules'))) {
    Write-Host 'RepoLocator 不可达：包根没有 powershell\Modules' -ForegroundColor Red
    exit 3
}
Write-OkFile 'RepoLocator 可达（包根含 powershell\Modules）'

# 反例检查：测试与临时产物不应进包
$forbidden = @(Get-ChildItem -LiteralPath $pkgRoot -Recurse -File |
    Where-Object { $_.FullName -match '\\(tests|obj|\.git)\\' -or $_.Extension -eq '.pdb' })
if ($forbidden.Count -gt 0) {
    Write-Host '包内混进了不该收的文件：' -ForegroundColor Red
    foreach ($f in $forbidden) { Write-Host ('   ' + $f.FullName) -ForegroundColor Red }
    exit 3
}

# CHANGELOG 记录了本包的 SHA256，而它自己若在包里就是自指 —— 包内那份永远
# 落后一代，每次重打都对不上。这条断言防止有人「顺手」把它加回来。
if (Test-Path -LiteralPath (Join-Path $pkgRoot 'CHANGELOG.md')) {
    Write-Host 'CHANGELOG.md 不得进包：它记着本 zip 的哈希，进去就构成自指' -ForegroundColor Red
    exit 3
}

# ---------------------------------------------------------------- 6 SHA256SUMS
Write-Step '生成 SHA256SUMS.txt'

$sorted = @($script:files | Sort-Object { $_ } -CaseSensitive)
$lines = New-Object System.Collections.ArrayList
foreach ($rel in $sorted) {
    $abs = Join-Path $pkgRoot ($rel -replace '/', '\')
    $h = (Get-FileHash -LiteralPath $abs -Algorithm SHA256).Hash.ToLowerInvariant()
    [void]$lines.Add($h + '  ' + $rel)
}
$sumsPath = Join-Path $pkgRoot 'SHA256SUMS.txt'
# 校验和文件本身不进自己的清单，否则会自我指涉
[System.IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-OkFile ("$($lines.Count) 个文件的校验和")

# ---------------------------------------------------------------- 7 打包
Write-Step '写入 zip'

$zipPath = Join-Path $OutputDir ($pkgName + '.zip')
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# 固定时间戳：可复现的关键。zip 最小可表示时间是 1980-01-01，
# 用一个明确的常量而不是当前时间，同一份输入才会得到同样的字节。
$stamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

$entries = @(Get-ChildItem -LiteralPath $pkgRoot -Recurse -File | Sort-Object { $_.FullName })
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($f in $entries) {
        $rel = ($pkgName + '/' + $f.FullName.Substring($pkgRoot.Length).TrimStart('\')) -replace '\\', '/'
        $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stamp
        $out = $entry.Open()
        try {
            $in = [System.IO.File]::OpenRead($f.FullName)
            try { $in.CopyTo($out) } finally { $in.Dispose() }
        } finally { $out.Dispose() }
    }
} finally {
    $zip.Dispose()
}

$zipInfo = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = Join-Path $OutputDir ($pkgName + '.zip.sha256')
[System.IO.File]::WriteAllText($hashPath, ($zipHash + '  ' + $pkgName + '.zip' + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# 回读：确认 zip 里的条目数跟清单对得上（多一个少一个都算打包出错）
$reopen = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try { $entryCount = @($reopen.Entries).Count } finally { $reopen.Dispose() }
$expectCount = $entries.Count
if ($entryCount -ne $expectCount) {
    throw "zip 条目数 $entryCount 与清单 $expectCount 不一致"
}

Remove-Item -LiteralPath $stageRoot -Recurse -Force

Write-Step '完成'
Write-Host (" 包      " + $zipPath) -ForegroundColor Green
Write-Host (" 大小    " + ('{0:N0} B' -f $zipInfo.Length) + " / " + $entryCount + " 个条目") -ForegroundColor Green
Write-Host (" SHA256  " + $zipHash) -ForegroundColor Green
Write-Host (" 校验和  " + $hashPath) -ForegroundColor Green
