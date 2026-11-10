param([switch]$Rollback)
# Pure ASCII. Switches the two Win+X "Windows PowerShell" shortcuts to pwsh 7.
# Run this in YOUR OWN PowerShell window (agent tool sessions block COM objects).
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\set-winx-pwsh.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\set-winx-pwsh.ps1 -Rollback
$ErrorActionPreference = 'Stop'
$wx = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WinX\Group3'
$names = @('01a - Windows PowerShell.lnk', '02a - Windows PowerShell.lnk')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($n in $names) {
    $p = Join-Path $wx $n
    if (-not (Test-Path -LiteralPath $p)) { Write-Output ("SKIP_MISSING=" + $p); continue }
    if ($Rollback) {
        $bak = Get-ChildItem $wx -Filter ($n + '.bak-*') -EA SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $bak) { Write-Output ("NO_BACKUP=" + $p); continue }
        [System.IO.File]::Copy($bak.FullName, $p, $true)
        Write-Output ("RESTORED=" + $p + " FROM=" + $bak.FullName)
        continue
    }
    $bak = $p + '.bak-' + $stamp
    [System.IO.File]::Copy($p, $bak, $true)
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($p)
    $sh.TargetPath = (Get-Command pwsh).Source
    $sh.Arguments = '-NoLogo'
    $sh.Save()
    $chk = (New-Object -ComObject WScript.Shell).CreateShortcut($p)
    Write-Output ("SWITCHED=" + $p + " NEW=" + $chk.TargetPath + " BACKUP=" + $bak)
}
if (-not $Rollback) {
    Write-Output "NOTE: Win+X caches shortcut hashes. Restart Explorer or sign out to refresh the menu."
    Write-Output "NOTE: If the menu does not change, run with -Rollback to restore the originals."
}
