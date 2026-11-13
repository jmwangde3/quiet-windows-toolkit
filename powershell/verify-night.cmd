@echo off
chcp 936 >nul
setlocal EnableExtensions
title QuietMode - night run acceptance check (read only)
rem 只读验收：对着 worker.log / reconcile.log / state.json / qmctl --json status
rem 逐条核对 V2_SPEC 第 12 节的 A01-A23，不改任何系统状态，不需要管理员。
rem 双击即可，也可以带日期参数跑历史：verify-night.cmd 20261114

set "SRC=%~dp0_publish\quiet-windows-toolkit\powershell\Verify-NightRun.ps1"
set "LOG=%~dp0_verify-night.log"

if not exist "%SRC%" goto nosrc

if "%~1"=="" (
    for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set "STAMP=%%i"
) else (
    set "STAMP=%~1"
)
set "REPORT=%~dp0QuietMode验收_%STAMP%.md"

echo.
echo ================= QUIETMODE ACCEPTANCE =================
powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%" -Md "%REPORT%" > "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Encoding UTF8"
echo.
echo   exit code = %RC%   [0 all pass / 2 something failed]
echo   log       = %LOG%
echo   report    = %REPORT%
echo.
if "%RC%"=="0" (
    echo   ACCEPTANCE OK
) else (
    echo   CHECK THE FAILED ROWS ABOVE
)
echo.
pause
exit /b %RC%

:nosrc
echo [ERROR] script not found: %SRC%
pause
exit /b 3
