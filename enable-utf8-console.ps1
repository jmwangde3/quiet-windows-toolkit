param([switch]$Rollback, [switch]$SystemWide)
# Pure ASCII. Sets console code page to UTF-8 for the current user (no reboot).
# New console windows pick it up. Use -Rollback to restore CP936.
# -SystemWide additionally flips HKLM Nls\CodePage ACP/OEMCP (needs admin + REBOOT,
# and can break legacy GBK-only apps). Not executed automatically.
$ErrorActionPreference = 'Stop'
$cp = if ($Rollback) { 936 } else { 65001 }
$action = if ($Rollback) { 'ROLLBACK' } else { 'APPLY' }
$keys = @(
    'HKCU:\Console',
    'HKCU:\Console\%SystemRoot%_system32_cmd.exe',
    'HKCU:\Console\%SystemRoot%_System32_WindowsPowerShell_v1.0_powershell.exe'
)
foreach ($k in $keys) {
    if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
    New-ItemProperty -Path $k -Name 'CodePage' -Value $cp -PropertyType DWord -Force | Out-Null
    Write-Output ("$action HKCU $k CodePage=$cp")
}
if ($SystemWide) {
    $nls = 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage'
    $val = if ($Rollback) { '936' } else { '65001' }
    Set-ItemProperty -Path $nls -Name 'ACP' -Value $val
    Set-ItemProperty -Path $nls -Name 'OEMCP' -Value $val
    Write-Output ("$action HKLM ACP/OEMCP=$val (REBOOT REQUIRED)")
}
Write-Output "NOTE: $action affects NEW console windows only. Close and reopen terminals."
Write-Output "NOTE: rollback = powershell -NoProfile -File .\enable-utf8-console.ps1 -Rollback"
