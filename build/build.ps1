<#
.SYNOPSIS
    编译整个解决方案并把 qmctl 及其依赖收集到 bin\。
.DESCRIPTION
    产物：
      bin\qmctl.exe        v2 机器侧入口
      bin\QuietMode.*.dll  依赖
    托盘程序仍由 QuietMode\build.cmd 单独产出（保持 v1 单文件分发形态）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

Write-Host "编译 QuietMode.sln ($Configuration)" -ForegroundColor Cyan
& $dotnet build (Join-Path $repoRoot 'QuietMode.sln') -c $Configuration -v minimal --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host 'BUILD FAILED' -ForegroundColor Red
    exit 1
}

$outDir = Join-Path $repoRoot 'bin'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$cliOut = Join-Path $repoRoot ("src\QuietMode.Cli\bin\{0}\net481" -f $Configuration)
Copy-Item -Path (Join-Path $cliOut 'qmctl.exe') -Destination $outDir -Force
Copy-Item -Path (Join-Path $cliOut 'QuietMode.Core.dll') -Destination $outDir -Force
Copy-Item -Path (Join-Path $cliOut 'QuietMode.Windows.dll') -Destination $outDir -Force

Write-Host ''
Write-Host ("BUILD OK → {0}" -f $outDir) -ForegroundColor Green
Write-Host '  qmctl.exe / QuietMode.Core.dll / QuietMode.Windows.dll'
