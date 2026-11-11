<#
.SYNOPSIS
    QuietMode v2 公共工具函数（路径、qmctl 调用、日志、提权检测）。
.DESCRIPTION
    约束：
      - 不在此文件里定义磁盘清单或维护任务清单，唯一来源是 QuietMode.Core 的 MaintenanceCatalog。
      - 调用 qmctl 一律带 --quiet / --out，避开控制台代码页把中文弄坏。
#>

$script:QuietModeModuleRoot = $PSScriptRoot

function Get-QuietModeRepoRoot {
    [CmdletBinding()]
    param()

    $dir = $PSScriptRoot
    for ($i = 0; $i -lt 8 -and $dir; $i++) {
        if ((Test-Path (Join-Path $dir 'README.md')) -and (Test-Path (Join-Path $dir 'src'))) {
            return (Resolve-Path $dir).Path
        }
        $dir = Split-Path $dir -Parent
    }
    throw "找不到仓库根目录（从 $PSScriptRoot 向上 8 层）"
}

function Get-QuietModeToolPath {
    [CmdletBinding()]
    param(
        [ValidateSet('Release', 'Debug')]
        [string]$Configuration = 'Release'
    )

    $root = Get-QuietModeRepoRoot
    $candidates = @(
        (Join-Path $root "src\QuietMode.Cli\bin\$Configuration\net481\qmctl.exe"),
        (Join-Path $root 'bin\qmctl.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return (Resolve-Path $c).Path }
    }
    throw "qmctl.exe 未找到；先运行 build/build.ps1 或 dotnet build src/QuietMode.Cli/QuietMode.Cli.csproj -c $Configuration"
}

function Test-QuietModeElevated {
    [CmdletBinding()]
    param()

    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-QuietModeTool {
    <#
    .SYNOPSIS
        调用 qmctl，返回退出码与原样输出（仅在 -PassThru 时收集输出）。
    .PARAMETER Arguments
        qmctl 参数数组，例如 @('config','validate','--json')
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$Root,

        [switch]$PassThru
    )

    $tool = Get-QuietModeToolPath
    $argv = New-Object System.Collections.Generic.List[string]
    if ($Root) { $argv.Add('--root'); $argv.Add($Root) }
    foreach ($a in $Arguments) { $argv.Add($a) }

    $output = $null
    if ($PassThru) {
        $output = & $tool @argv 2>&1 | ForEach-Object { $_.ToString() }
        $code = $LASTEXITCODE
    }
    else {
        & $tool @argv | Out-Null
        $code = $LASTEXITCODE
    }

    return [pscustomobject]@{
        ExitCode = $code
        Output   = $output
        Ok       = ($code -eq 0)
    }
}

function Invoke-QuietModeToolJson {
    <#
    .SYNOPSIS
        调用 qmctl 的 --json 模式：写临时文件 → UTF-8 读回 → ConvertFrom-Json。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$Root
    )

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("qm-" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + ".json")
    try {
        $argv = New-Object System.Collections.Generic.List[string]
        foreach ($a in $Arguments) { $argv.Add($a) }
        $argv.Add('--json')
        $argv.Add('--out')
        $argv.Add($tmp)

        $r = Invoke-QuietModeTool -Arguments $argv.ToArray() -Root $Root
        $data = $null
        if (Test-Path -LiteralPath $tmp) {
            $text = Get-Content -LiteralPath $tmp -Raw -Encoding UTF8
            if ($text -and $text.Trim().Length -gt 0) { $data = $text | ConvertFrom-Json }
        }
        return [pscustomobject]@{
            ExitCode = $r.ExitCode
            Ok       = $r.Ok
            Data     = $data
        }
    }
    finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

function Write-QuietModeLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [ValidateSet('INFO', 'WARN', 'ERROR')]
        [string]$Level = 'INFO',

        [string]$LogPath
    )

    if (-not $LogPath) {
        $LogPath = Join-Path $env:ProgramData 'QuietMode\logs\quietmode.log'
    }
    $dir = Split-Path $LogPath -Parent
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $line = "[{0}] {1,-5} {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

Export-ModuleMember -Function Get-QuietModeRepoRoot, Get-QuietModeToolPath, Test-QuietModeElevated,
    Invoke-QuietModeTool, Invoke-QuietModeToolJson, Write-QuietModeLog
